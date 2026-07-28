using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GarageFlow.Api.Services;

/// <summary>SMTP settings, bound from the <c>Email</c> section of appsettings.</summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "no-reply@garageflow.local";
    public string FromName { get; set; } = "GarageFlow";
    public bool UseStartTls { get; set; } = true;

    /// <summary>
    /// Where the reset link points — the dashboard's origin, not the API's.
    /// </summary>
    public string DashboardUrl { get; set; } = "http://localhost:5000";

    /// <summary>
    /// True once a host *and* a password are present. The password is expected
    /// to come from user-secrets, not appsettings — so until it is set, mail is
    /// logged rather than sent, and the app is usable either way.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Password);
}

public interface IEmailSender
{
    Task SendAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>
/// Sends mail over SMTP, or writes it to the log when SMTP is not configured.
/// </summary>
/// <remarks>
/// The logging fallback is what makes password reset usable out of the box: with
/// no credentials in appsettings the reset link is written to the API console,
/// so the flow can be exercised end to end. Fill in the <c>Email</c> section and
/// the same code posts it for real — no other change.
///
/// It logs the whole body, reset link included. That is fine for a dev console
/// and wrong for a shared environment, so it only ever runs unconfigured.
/// </remarks>
public class EmailSender(IOptions<EmailOptions> options, ILogger<EmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            logger.LogWarning(
                "SMTP password not set — email NOT sent. To deliver it, run from src/GarageFlow.Api:\n"
                + "    dotnet user-secrets set \"Email:Password\" \"<your gmail app password>\"\n"
                + "To: {To}\nSubject: {Subject}\n{Body}",
                toAddress, subject, htmlBody);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();

        try
        {
            await client.ConnectAsync(
                _options.Host,
                _options.Port,
                _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto,
                ct);

            if (!string.IsNullOrWhiteSpace(_options.Username))
                await client.AuthenticateAsync(_options.Username, _options.Password, ct);

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            logger.LogInformation("Password reset email sent to {To}", toAddress);
        }
        catch (Exception ex)
        {
            // A failed send must not tell the caller whether the address exists,
            // so this is logged and swallowed — the endpoint answers the same
            // either way.
            logger.LogError(ex, "Failed to send email to {To}", toAddress);
        }
    }
}
