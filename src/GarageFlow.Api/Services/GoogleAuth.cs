using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GarageFlow.Api.Services;

/// <summary>Configuration for "Sign in with Google".</summary>
public class GoogleAuthOptions
{
    /// <summary>
    /// The OAuth client IDs a token may be issued for.
    /// </summary>
    /// <remarks>
    /// Plural because Android and iOS each get their own client, and the token
    /// arrives carrying whichever one signed the user in. All of them are
    /// accepted; anything else is rejected.
    ///
    /// Empty means the feature is off, and the app hides the button rather than
    /// offering one that cannot work — the same rule Khalti follows.
    /// </remarks>
    public string[] ClientIds { get; set; } = [];

    public bool IsConfigured => ClientIds.Any(id => !string.IsNullOrWhiteSpace(id));
}

/// <summary>
/// Verifies a Google ID token.
/// </summary>
/// <remarks>
/// <para>
/// The token is a JWT signed by Google. Verifying it means three things, and
/// skipping any one of them makes the whole feature worthless:
/// </para>
/// <list type="number">
///   <item>the signature is Google's, checked against their published keys;</item>
///   <item>the audience is one of *our* client IDs — otherwise a token minted
///         for any other Google app would sign someone in here;</item>
///   <item>the issuer is Google and the token has not expired.</item>
/// </list>
/// <para>
/// This calls Google's tokeninfo endpoint, which performs all three server-side
/// and is the approach Google documents for backends that do not want to cache
/// and rotate JWKS themselves. It costs one HTTPS round trip per sign-in, which
/// happens once per session and is not worth optimising away in exchange for
/// hand-rolled key rotation.
/// </para>
/// </remarks>
public class GoogleAuth(
    IHttpClientFactory httpClientFactory,
    IOptions<GoogleAuthOptions> options,
    ILogger<GoogleAuth> logger)
{
    private readonly GoogleAuthOptions _options = options.Value;

    private const string TokenInfoUrl =
        "https://oauth2.googleapis.com/tokeninfo?id_token=";

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// The verified identity behind a token, or null when it cannot be trusted.
    /// </summary>
    /// <remarks>
    /// Null for every failure — bad signature, wrong audience, expired,
    /// unreachable network. The caller turns that into one message. Telling a
    /// client *why* a token was rejected only helps somebody probing it.
    /// </remarks>
    public async Task<GoogleIdentity?> VerifyAsync(string idToken, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(idToken)) return null;

        // A cheap shape check before spending a round trip on obvious rubbish.
        if (!new JwtSecurityTokenHandler().CanReadToken(idToken)) return null;

        try
        {
            var client = httpClientFactory.CreateClient(nameof(GoogleAuth));

            using var response = await client.GetAsync(
                TokenInfoUrl + Uri.EscapeDataString(idToken), ct);

            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var root = json.RootElement;

            var audience = Text(root, "aud");
            var issuer = Text(root, "iss");
            var email = Text(root, "email");

            // The check that matters most. Google will happily verify a token
            // it issued for somebody else's app; only the audience ties it to
            // ours.
            if (!_options.ClientIds.Contains(audience, StringComparer.Ordinal))
            {
                logger.LogWarning(
                    "Google token rejected: audience {Audience} is not one of ours", audience);
                return null;
            }

            if (issuer is not ("accounts.google.com" or "https://accounts.google.com"))
                return null;

            if (string.IsNullOrWhiteSpace(email)) return null;

            // An unverified address must not be trusted: it is the key we match
            // accounts on, and Google does not guarantee it otherwise.
            if (Text(root, "email_verified") is not ("true" or "True")) return null;

            return new GoogleIdentity(
                Email: email.Trim(),
                Name: Text(root, "name") ?? email.Split('@')[0],
                Subject: Text(root, "sub") ?? "",
                PictureUrl: Text(root, "picture"));
        }
        catch (Exception ex)
        {
            // A dead connection is not the user's fault and not a security
            // event; it is still a refusal, because nothing was proved.
            logger.LogWarning(ex, "Could not verify a Google token");
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() : null;
}

/// <summary>A person, as Google vouches for them.</summary>
public record GoogleIdentity(string Email, string Name, string Subject, string? PictureUrl);
