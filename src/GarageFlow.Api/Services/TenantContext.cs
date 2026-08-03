using System.Security.Claims;

namespace GarageFlow.Api.Services;

/// <summary>
/// Which company the current request belongs to.
/// </summary>
/// <remarks>
/// <para>
/// One object, read by <see cref="Data.GarageFlowDbContext"/> to filter every
/// tenant-scoped query and to stamp every new row. Scoped, so it is resolved
/// once per request from the token and cannot drift mid-request.
/// </para>
/// <para>
/// <b>Why a global filter rather than a <c>Where</c> in each controller.</b>
/// The controllers already got customer scoping wrong once — a `null` that meant
/// "staff" in one place and "no customer" in another leaked a whole workshop.
/// Manual filtering fails the same way every time: it is correct until somebody
/// adds the sixteenth query and forgets. A filter attached to the model applies
/// to queries that have not been written yet.
/// </para>
/// </remarks>
public class TenantContext
{
    /// <summary>
    /// The company this request may see, or null for an unscoped context.
    /// </summary>
    /// <remarks>
    /// Null means <i>no filter at all</i> and exists for exactly two callers:
    /// the seeder, which runs before any request, and the superadmin console,
    /// which is allowed to look across companies on purpose.
    ///
    /// It deliberately does <b>not</b> mean "anonymous" — an anonymous request
    /// never reaches a tenant-scoped table, and a signed-in user always carries
    /// a company code even when that code is the empty string.
    /// </remarks>
    public string? CompanyCode { get; private set; }

    /// <summary>True when this context sees across every company.</summary>
    public bool IsUnscoped => CompanyCode is null;

    /// <summary>
    /// Binds the context to a company, from the token.
    /// </summary>
    /// <remarks>
    /// An empty string is a real value and is kept as one: a customer who has
    /// joined no garage carries <c>""</c>, and the filter then matches nothing,
    /// which is the correct answer. Coercing it to null would hand them every
    /// company's data — the same mistake, one layer down.
    /// </remarks>
    public void BindTo(string companyCode) => CompanyCode = companyCode;

    /// <summary>
    /// Lifts the filter for this request.
    /// </summary>
    /// <remarks>
    /// Only the superadmin endpoints and the seeder call this, and it is a
    /// method rather than a settable property so every use is greppable.
    /// </remarks>
    public void SeeEveryCompany() => CompanyCode = null;

    /// <summary>Reads the company code out of a signed-in principal.</summary>
    public static string? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;

        return principal.FindFirstValue(TokenService.CompanyCodeClaim);
    }
}

/// <summary>
/// Binds <see cref="TenantContext"/> to the signed-in user's company.
/// </summary>
/// <remarks>
/// Middleware rather than a DbContext constructor lookup: the token has been
/// validated by the time this runs, and doing it in one place means the
/// DbContext never has to know that HTTP exists.
/// </remarks>
public class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        var companyCode = TenantContext.FromPrincipal(context.User);

        // Anonymous requests leave the context unscoped. That is safe because
        // nothing anonymous reads a tenant-scoped table — the garage directory
        // reads Workshops, which is deliberately cross-company.
        if (companyCode is not null) tenant.BindTo(companyCode);

        await next(context);
    }
}
