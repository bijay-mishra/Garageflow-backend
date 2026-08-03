using System.Text;
using System.Text.Json;
using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using GarageFlow.Api.Services.Payments;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ── Database ─────────────────────────────────────────────────────────────────
// Defaults to SQL Server LocalDB, which ships with Visual Studio and the SQL
// Server Express installer. Point ConnectionStrings:GarageFlow at a full
// instance in appsettings and nothing else has to change.
var connectionString = builder.Configuration.GetConnectionString("GarageFlow")
    ?? throw new InvalidOperationException("Missing connection string 'GarageFlow'.");

builder.Services.AddDbContext<GarageFlowDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(TimeProvider.System);
// Which company the request belongs to. Scoped, bound from the token by
// TenantMiddleware, and read by the DbContext to filter every tenant-owned
// query — see ITenantOwned.
builder.Services.AddScoped<TenantContext>();

builder.Services.AddScoped<ActivityLog>();

// Mobile apps. NotificationService and CurrentUserService are scoped because
// they share the request's DbContext — the first so a notification commits in
// the same transaction as the change it announces, the second so the user row
// is looked up once per request rather than once per endpoint that needs it.
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<CurrentUserService>();

// Which branch and accounting year the request is answered against. Scoped for
// the same reason as CurrentUserService: it caches its answer per request, and
// every list endpoint asks.
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<MenuService>();
builder.Services.AddScoped<RoleService>();
builder.Services.AddSingleton<PhotoStorage>();

// Turns catalogue services into priced job lines. Scoped for the same reason as
// the two above: it works on the request's tracked entities and leaves the
// SaveChanges to whoever called it.
builder.Services.AddScoped<JobServiceAppender>();

// Handovers — collection or delivery, the distance fee, and the driver's trail.
builder.Services.AddScoped<DeliveryService>();

// ── Online payment ───────────────────────────────────────────────────────────
// Gateways are registered as a set and resolved by name, so adding a third
// provider is one class and one line here — nothing that already exists has to
// know about it.
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.SectionName));

// Named clients so each gateway gets its own connection pool and timeout. A
// wallet that hangs must not exhaust the pool the other one is using.
builder.Services.AddHttpClient(nameof(EsewaGateway), client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient(nameof(KhaltiGateway), client => client.Timeout = TimeSpan.FromSeconds(20));

builder.Services.AddScoped<IPaymentGateway, EsewaGateway>();
builder.Services.AddScoped<IPaymentGateway, KhaltiGateway>();
builder.Services.AddScoped<PaymentService>();

// ── Authentication ───────────────────────────────────────────────────────────
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
// Sign in with Google. Off unless a client ID is configured, and the app
// hides the button when it is — see GoogleAuth.
builder.Services.Configure<GoogleAuthOptions>(builder.Configuration.GetSection("GoogleAuth"));
builder.Services.AddHttpClient(nameof(GoogleAuth));
builder.Services.AddScoped<GoogleAuth>();

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<IEmailSender, EmailSender>();

// PBKDF2 with a per-password salt, from ASP.NET Core Identity. Only the hasher
// is used — the app has no need for the rest of Identity's machinery.
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Missing 'Jwt' configuration section.");

if (jwtOptions.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters for HMAC-SHA256.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            // Default is 5 minutes of leeway, which would keep expired tokens
            // working well past their stated lifetime.
            ClockSkew = TimeSpan.Zero,
        };

        // 401s must use the same envelope as everything else, so the dashboard
        // reads `message` here exactly as it does elsewhere.
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";

                await context.Response.WriteAsync(JsonSerializer.Serialize(
                    ApiResponse.Failure("Your session has expired. Please sign in again."),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            },
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";

                await context.Response.WriteAsync(JsonSerializer.Serialize(
                    ApiResponse.Failure("You do not have permission to do that."),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            },
        };
    });

builder.Services.AddAuthorization();

// The half-signed-in gate is global rather than per controller: a rule that
// depends on remembering an attribute holds until the next endpoint somebody
// adds. Opting out is the deliberate act — see AllowWhileSettingPassword.
builder.Services.AddControllers(o => o.Filters.Add<MustSetPasswordFilter>());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// ── Validation failures use the same envelope as everything else ─────────────
// [ApiController] would otherwise return RFC 7807 problem details here, giving
// the client a second error shape to parse. Instead the first validation
// message becomes `message`, with the full field map alongside it.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value!.Errors.Select(e => e.ErrorMessage).ToArray());

        var firstMessage = errors.Values.SelectMany(messages => messages).FirstOrDefault()
            ?? "The request was not valid.";

        return new BadRequestObjectResult(ApiResponse.Failure(firstMessage, errors));
    };
});

// Swagger stays stock: a plain list of routes with their parameters and
// response shapes. The /// comments on the controllers are deliberately NOT
// fed in — they are for whoever reads the code, and they turn the UI into
// walls of prose. To show them, add:
//   options.IncludeXmlComments(
//       Path.Combine(AppContext.BaseDirectory, "GarageFlow.Api.xml"),
//       includeControllerXmlComments: true);
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "GarageFlow API", Version = "v1" });

    // Adds the Authorize button. Paste the accessToken from POST /api/auth/login
    // and every subsequent "Try it out" carries the bearer header.
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the accessToken value only — Swagger adds the \"Bearer \" prefix.",
    });

    // Swashbuckle 10 / Microsoft.OpenApi 2.x: the requirement is built from the
    // document, and a scheme is referenced by OpenApiSecuritySchemeReference
    // rather than the older OpenApiSecurityScheme.Reference property.
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
    });
});

// ── CORS ─────────────────────────────────────────────────────────────────────
// The dashboard runs on its own origin in development (Vite on :5000), so the
// browser needs these headers. Origins come from configuration — add your
// deployed frontend there rather than widening this to AllowAnyOrigin.
const string CorsPolicy = "dashboard";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddPolicy(CorsPolicy, policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

// ── Pipeline ─────────────────────────────────────────────────────────────────

// Unhandled exceptions come back as the standard { data, status, message }
// envelope — see ApiExceptionHandler. The empty options delegate is required:
// without it the middleware demands either a fallback path or ProblemDetails,
// which this API deliberately does not use.
app.UseExceptionHandler(_ => { });

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "GarageFlow API v1");
        options.DocumentTitle = "GarageFlow API";
    });

    // Landing on the root during development should show the docs.
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}

app.UseCors(CorsPolicy);

// Serves the job photos written under wwwroot/uploads. Deliberately before
// authentication: an <img> tag cannot send a bearer header, so the app could
// not display a photo it had just been given the URL for. The names are
// 128 bits of random hex inside a folder named after the job, so a URL is
// unguessable — but it is a URL, and anyone holding one can open it.
//
// The provider is built explicitly rather than left to the default. A fresh
// clone has no wwwroot, and the host resolves WebRootPath to null when the
// folder is missing at startup — after which UseStaticFiles() has no root and
// silently 404s every upload, including the ones written moments earlier.
var webRoot = app.Environment.WebRootPath
              ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");

Directory.CreateDirectory(webRoot);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(webRoot),
});

// Order matters: who are you, then what may you do.
app.UseAuthentication();
app.UseAuthorization();

// After UseAuthorization: the principal has to exist before the company can be
// read off it.
app.UseMiddleware<TenantMiddleware>();

app.MapControllers();

// Liveness probe — also a quick way to confirm the API is up before starting the UI.
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

// ── Startup: migrate and seed ────────────────────────────────────────────────
// Applying migrations at startup keeps local setup to a single `dotnet run`. In
// production this belongs in your deploy step, not in the app.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GarageFlowDbContext>();
    await db.Database.MigrateAsync();

    // The seeder writes tenant-owned rows with no request behind it, so it has
    // to say which company they belong to. Said out loud here rather than
    // defaulted inside the seeder: a silent fallback is how rows end up in the
    // wrong company once there is more than one.
    scope.ServiceProvider.GetRequiredService<TenantContext>()
        .BindTo(DbSeeder.DemoCompanyCode);

    await DbSeeder.SeedAsync(db);
    await DbSeeder.SeedServicesAsync(db);
    await DbSeeder.SeedWorkshopAsync(db);
    await DbSeeder.SeedBranchesAsync(db);
    await DbSeeder.SeedUsersAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>());

    // The platform operator, who belongs to no company. Seeded last so the
    // id generator sees every other user first.
    await DbSeeder.SeedSuperAdminAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>());

    // Additive on every start: a new release's menu rows appear, and anything an
    // operator has renamed or reordered is left alone. Resetting the table to
    // match the code would silently undo their work on each deploy.
    await MenuSeeder.SeedAsync(db);
}

app.Run();
