using System.Net;
using System.Text;
using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using GarageFlow.Api.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// Paying a bill online, through eSewa or Khalti.
/// </summary>
/// <remarks>
/// Three steps, and the middle one does not belong to us:
///
/// <list type="number">
/// <item><c>POST /api/payments/start</c> — opens an attempt and returns where to
/// send the customer.</item>
/// <item>the customer authorises on the provider's own page;</item>
/// <item>the provider redirects to <c>/api/payments/callback/{provider}</c>,
/// which confirms the money with the provider server-to-server and settles the
/// invoice.</item>
/// </list>
///
/// The callback is <c>[AllowAnonymous]</c> because it arrives from a redirect in
/// the customer's browser, which carries no bearer token. That is safe only
/// because it proves nothing by itself: the reference is a 32-character random
/// value, and the endpoint independently asks the provider whether the payment
/// happened rather than believing the query string.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/payments")]
[Produces("application/json")]
public class PaymentsController(
    GarageFlowDbContext db,
    PaymentService payments,
    CurrentUserService currentUser,
    ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>Gateways that can actually be used right now.</summary>
    [HttpGet("providers")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<string>>>(StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<IReadOnlyList<string>>> Providers()
    {
        var available = payments.AvailableProviders;

        return Ok(ApiResponse<IReadOnlyList<string>>.Ok(
            available,
            available.Count == 0
                ? "No online payment is set up. Cash and bank transfer only."
                : $"{available.Count} online payment option(s) available."));
    }

    /// <summary>
    /// Starts an online payment against an invoice.
    /// </summary>
    /// <remarks>
    /// A customer may only pay their own bills; staff may start a payment for
    /// anyone, which is what a counter does when it hands over a QR code.
    /// </remarks>
    [HttpPost("start")]
    [ProducesResponseType<ApiResponse<PaymentStartDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PaymentStartDto>>> Start(
        StartPaymentRequest request, CancellationToken ct)
    {
        var invoices = db.Invoices.Where(i => i.Id == request.InvoiceId);

        // Scoping applied here rather than trusted to the client, so a guessed
        // invoice id gets a 404 rather than somebody else's bill.
        var customerId = await currentUser.CustomerIdAsync(User, ct);
        if (customerId is not null) invoices = invoices.Where(i => i.CustomerId == customerId);

        var invoice = await invoices.FirstOrDefaultAsync(ct);

        if (invoice is null)
            return NotFound(ApiResponse.Failure($"Invoice '{request.InvoiceId}' was not found."));

        var result = await payments.StartAsync(invoice, request.Provider, ct);

        if (!result.Ok)
            return BadRequest(ApiResponse.Failure(result.Error!));

        var dto = new PaymentStartDto
        {
            Reference = result.Payment.Reference!,
            Provider = result.Payment.Method,
            Amount = result.Payment.Amount,
            Method = result.Start.Method,
            Url = result.Start.Method == "form-post"
                // eSewa needs an HTML form POST, which an app cannot do by
                // opening a URL. We serve a page that carries the signed fields
                // and submits itself, so the client only ever has to open a link.
                ? $"{Request.Scheme}://{Request.Host}/api/payments/redirect/{result.Payment.Reference}"
                : result.Start.Url,
            Fields = result.Start.Fields,
        };

        return Ok(ApiResponse<PaymentStartDto>.Ok(
            dto, $"Continue to {result.Payment.Method} to pay Rs {result.Payment.Amount:N2}."));
    }

    /// <summary>
    /// Confirms an attempt with the provider. Safe to call more than once.
    /// </summary>
    /// <remarks>
    /// The app calls this when it comes back to the foreground: the customer has
    /// been away in a browser, and this is how the app finds out what happened
    /// without needing a deep link registered on both platforms.
    /// </remarks>
    [HttpPost("verify")]
    [ProducesResponseType<ApiResponse<PaymentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<PaymentDto>>> Verify(
        VerifyPaymentRequest request, CancellationToken ct)
    {
        var (payment, settled, message) = await payments.VerifyAsync(request.Reference, request.Data, ct);

        if (payment is null)
            return BadRequest(ApiResponse.Failure(message));

        // A customer must not be able to probe references belonging to others.
        var customerId = await currentUser.CustomerIdAsync(User, ct);

        if (customerId is not null)
        {
            var owns = await db.Invoices
                .AnyAsync(i => i.Id == payment.InvoiceId && i.CustomerId == customerId, ct);

            if (!owns) return BadRequest(ApiResponse.Failure("That payment could not be found."));
        }

        var dto = ToDto(payment);

        return settled
            ? Ok(ApiResponse<PaymentDto>.Ok(dto, message))
            // Carries the payment even though it failed: the app needs the
            // status to decide whether to keep waiting or offer another wallet.
            : BadRequest(ApiResponse<PaymentDto>.FailWith(dto, message));
    }

    /// <summary>
    /// The page eSewa's form is posted from.
    /// </summary>
    /// <remarks>
    /// eSewa v2 takes an HTML form POST with a signature over three of the
    /// fields; there is no GET equivalent. A phone cannot POST by opening a
    /// link, so the app opens this instead and the page submits itself.
    ///
    /// Anonymous, and safe to be: it renders fields that were signed when the
    /// attempt was created, for a reference that is unguessable, and it moves no
    /// money — settling still requires the provider to confirm.
    /// </remarks>
    [HttpGet("redirect/{reference}")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Redirect(string reference, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Reference == reference, ct);

        if (payment is null || payment.Status != "Pending")
            return Content(Page("This payment link is no longer valid.", null), "text/html");

        // Re-signed rather than stored: the fields are cheap to rebuild and a
        // signature sitting in a database column is a signature that can go
        // stale against a rotated key.
        var gateway = HttpContext.RequestServices
            .GetServices<IPaymentGateway>()
            .FirstOrDefault(g => g.Provider == payment.Method);

        if (gateway is null)
            return Content(Page($"{payment.Method} is no longer available.", null), "text/html");

        var start = await gateway.StartAsync(reference, payment.Amount, $"Invoice {payment.InvoiceId}", ct);

        var inputs = string.Join("\n", start.Fields.Select(f =>
            $"""<input type="hidden" name="{WebUtility.HtmlEncode(f.Key)}" value="{WebUtility.HtmlEncode(f.Value)}" />"""));

        // `$$` so the CSS braces below stay literal and interpolation is `{{…}}`.
        var html = $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>Continuing to {{WebUtility.HtmlEncode(payment.Method)}}</title>
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <style>body{font:15px system-ui,sans-serif;display:grid;place-items:center;height:100vh;margin:0;color:#334155}</style>
            </head><body>
            <p>Taking you to {{WebUtility.HtmlEncode(payment.Method)}}…</p>
            <form id="f" method="POST" action="{{WebUtility.HtmlEncode(start.Url)}}">{{inputs}}</form>
            <script>document.getElementById('f').submit();</script>
            <noscript><button form="f">Continue to {{WebUtility.HtmlEncode(payment.Method)}}</button></noscript>
            </body></html>
            """;

        return Content(html, "text/html", Encoding.UTF8);
    }

    /// <summary>
    /// Where the gateway sends the customer back to.
    /// </summary>
    /// <remarks>
    /// Anonymous by necessity — a redirect carries no bearer token. It is not a
    /// trust boundary: the reference identifies an attempt, and what happened to
    /// that attempt is decided by asking the provider directly.
    ///
    /// Returns HTML rather than JSON because a person is looking at it. The app
    /// does not read this page; it re-verifies through <c>POST /verify</c> when
    /// it returns to the foreground.
    /// </remarks>
    [HttpGet("callback/{provider}")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Callback(
        string provider,
        [FromQuery] string? data,
        [FromQuery] string? pidx,
        [FromQuery(Name = "purchase_order_id")] string? purchaseOrderId,
        [FromQuery] string? transaction_uuid,
        [FromQuery] bool failed,
        CancellationToken ct)
    {
        // Each gateway names things its own way. eSewa base64s a blob into
        // `data`; Khalti sends `pidx` plus the reference it was given as
        // `purchase_order_id`.
        var reference = purchaseOrderId ?? transaction_uuid ?? ReferenceFromEsewa(data);

        if (string.IsNullOrWhiteSpace(reference))
        {
            logger.LogWarning("Payment callback from {Provider} carried no reference", provider);
            return Content(Page("We could not identify that payment.", null), "text/html");
        }

        if (failed)
            return Content(Page("The payment was cancelled.", "You can try again from the app."), "text/html");

        var callbackData = pidx ?? data;
        var (_, settled, message) = await payments.VerifyAsync(reference, callbackData, ct);

        return Content(
            settled
                ? Page("Payment received", message)
                : Page("Payment not completed", message),
            "text/html",
            Encoding.UTF8);
    }

    /// <summary>Digs our reference out of eSewa's base64 response blob.</summary>
    private static string? ReferenceFromEsewa(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;

        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(
                Encoding.UTF8.GetString(Convert.FromBase64String(data)));

            return json.RootElement.TryGetProperty("transaction_uuid", out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The page the customer lands on after paying.
    /// </summary>
    /// <remarks>
    /// Deliberately plain and self-contained: it is opened in whatever browser
    /// the phone chose, often with no network left to fetch a stylesheet, and
    /// its only job is to tell someone standing in a workshop whether their
    /// money arrived.
    /// </remarks>
    private static string Page(string title, string? detail) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <style>
          body{font:16px system-ui,-apple-system,sans-serif;margin:0;display:grid;place-items:center;
               min-height:100vh;background:#f8fafc;color:#0f172a;padding:24px}
          .card{background:#fff;border:1px solid #e2e8f0;border-radius:14px;padding:28px;max-width:340px;text-align:center}
          h1{font-size:19px;margin:0 0 8px}
          p{font-size:14px;color:#64748b;margin:0;line-height:1.5}
          .hint{margin-top:18px;font-size:13px;color:#94a3b8}
        </style></head>
        <body><div class="card">
          <h1>{{WebUtility.HtmlEncode(title)}}</h1>
          <p>{{WebUtility.HtmlEncode(detail ?? "")}}</p>
          <p class="hint">You can close this page and return to the app.</p>
        </div></body></html>
        """;

    internal static PaymentDto ToDto(Payment p) => new()
    {
        Id = p.Id,
        Amount = p.Amount,
        Method = p.Method,
        Channel = p.Channel,
        Status = p.Status,
        Reference = p.Reference,
        ProviderRef = p.ProviderRef,
        FailureReason = p.FailureReason,
        At = p.At,
    };
}
