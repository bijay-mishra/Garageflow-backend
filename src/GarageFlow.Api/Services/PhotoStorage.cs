using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;

namespace GarageFlow.Api.Services;

/// <summary>
/// Saves job photos to disk under <c>wwwroot/uploads</c>.
/// </summary>
/// <remarks>
/// The whole of the app's knowledge of *where* photos live is in this class, so
/// swapping in blob storage later means reimplementing two methods rather than
/// hunting through controllers.
/// </remarks>
public class PhotoStorage(IWebHostEnvironment environment, ILogger<PhotoStorage> logger)
{
    /// <summary>Folder under wwwroot. Also the first segment of the public URL.</summary>
    public const string UploadsFolder = "uploads";

    /// <summary>4 MB. Phone cameras exceed this, so the app downsizes before sending.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// What a job photo is allowed to be, keyed by content type with the
    /// extension to save it under.
    /// </summary>
    /// <remarks>
    /// An allow-list, not a deny-list, and the extension comes from *here*
    /// rather than from the uploaded filename — a client that sends
    /// "photo.jpg.aspx" must not get to choose what lands on disk.
    /// </remarks>
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/heic"] = ".heic",
    };

    public static bool IsAllowed(string? contentType) =>
        contentType is not null && AllowedTypes.ContainsKey(contentType);

    public static string AllowedList => string.Join(", ", AllowedTypes.Keys);

    /// <summary>
    /// Writes <paramref name="file"/> under the job's folder and returns the
    /// path relative to wwwroot, ready to store and to append to a base URL.
    /// </summary>
    public async Task<string> SaveAsync(IFormFile file, string jobCardId, CancellationToken ct = default)
    {
        var extension = AllowedTypes[file.ContentType];

        // A random name, never the client's. Two mechanics photographing the
        // same panel must not overwrite one another, and an uploaded name is
        // attacker-controlled input that has no business reaching the file
        // system.
        var name = $"{Guid.NewGuid():N}{extension}";

        // jobCardId is a server-generated id matching JOB-nnnn, never free text,
        // so it cannot climb out of the uploads folder.
        var relativeDirectory = Path.Combine(UploadsFolder, jobCardId);
        var absoluteDirectory = Path.Combine(WebRoot, relativeDirectory);

        Directory.CreateDirectory(absoluteDirectory);

        var absolutePath = Path.Combine(absoluteDirectory, name);

        await using (var stream = File.Create(absolutePath))
            await file.CopyToAsync(stream, ct);

        // Stored with forward slashes: this becomes part of a URL, and Windows
        // would otherwise write "uploads\JOB-1042\x.jpg" into the database.
        return $"{UploadsFolder}/{jobCardId}/{name}";
    }

    /// <summary>Folder holding profile photos, under wwwroot.</summary>
    public const string AvatarsFolder = "avatars";

    /// <summary>
    /// Saves a profile photo and returns the path relative to wwwroot.
    /// </summary>
    /// <remarks>
    /// One file per user, and the previous one is deleted by the caller. Named
    /// from the user id plus a random suffix rather than the id alone: the id
    /// alone would be a stable, guessable URL that stays valid after the photo
    /// is replaced, so a cached copy could outlive the change. The suffix makes
    /// each upload its own URL, which also means no cache-busting query string.
    /// </remarks>
    public async Task<string> SaveAvatarAsync(
        IFormFile file, string userId, CancellationToken ct = default)
    {
        var extension = AllowedTypes[file.ContentType];
        var name = $"{userId}-{Guid.NewGuid():N}{extension}";

        var absoluteDirectory = Path.Combine(WebRoot, AvatarsFolder);
        Directory.CreateDirectory(absoluteDirectory);

        await using (var stream = File.Create(Path.Combine(absoluteDirectory, name)))
            await file.CopyToAsync(stream, ct);

        return $"{AvatarsFolder}/{name}";
    }

    /// <summary>Folder holding company logos, under wwwroot.</summary>
    public const string LogosFolder = "logos";

    /// <summary>
    /// 1 MB. A logo is a mark at the top of a page, not a photograph, and one
    /// that needs more than this is a scan of a letterhead — which prints badly
    /// and would be better redrawn than uploaded.
    /// </summary>
    public const long MaxLogoBytes = 1024 * 1024;

    /// <summary>
    /// What <c>[RequestSizeLimit]</c> on the logo endpoints is set to.
    /// </summary>
    /// <remarks>
    /// Deliberately larger than <see cref="MaxLogoBytes"/>. A multipart body is
    /// the file *plus* its boundary and headers, so a request carrying a
    /// 1,048,576-byte file is always over 1,048,576 bytes on the wire — setting
    /// the two equal means the framework rejects a file of exactly the allowed
    /// size, with its own message ("Request body too large") rather than ours.
    ///
    /// The slack lets the request reach the action, where the real check reads
    /// the file's own length and answers in words a workshop can act on. This
    /// is still a hard ceiling for anything absurd.
    /// </remarks>
    public const long MaxLogoRequestBytes = MaxLogoBytes + 64 * 1024;

    /// <summary>
    /// What a logo may be, keyed by content type.
    /// </summary>
    /// <remarks>
    /// The photo list plus SVG, which is the format a designer actually hands
    /// over and the only one that stays sharp on a printed invoice — the place
    /// this matters most, since a 200px PNG scaled to a letterhead is visibly
    /// soft on paper in a way it never is on screen.
    ///
    /// SVG is markup, though, and markup can carry script. It is served from
    /// wwwroot on this API's own origin, so a hostile file would run there — see
    /// <see cref="LooksLikeScriptedSvg"/>, which is why an upload is screened
    /// rather than only type-checked. HEIC is left out: no browser renders it,
    /// so a logo saved as one would be invisible everywhere it is meant to show.
    /// </remarks>
    private static readonly Dictionary<string, string> AllowedLogoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
    };

    public static bool IsAllowedLogo(string? contentType) =>
        contentType is not null && AllowedLogoTypes.ContainsKey(contentType);

    public static string AllowedLogoList => string.Join(", ", AllowedLogoTypes.Keys);

    /// <summary>
    /// Saves a company logo and returns the path relative to wwwroot.
    /// </summary>
    /// <remarks>
    /// Named from the company code plus a random suffix, for the same reason as
    /// <see cref="SaveAvatarAsync"/>: the code alone would be a stable URL that
    /// stays valid after the logo is replaced, so a browser — or a print
    /// preview — could keep showing the old mark. A fresh URL per upload sidesteps
    /// cache-busting entirely.
    /// </remarks>
    public async Task<string> SaveLogoAsync(
        IFormFile file, string companyCode, CancellationToken ct = default)
    {
        var extension = AllowedLogoTypes[file.ContentType];

        // The code is validated on the way in (letters, digits and hyphens) and
        // uppercased, so it cannot climb out of the folder. Lowercased here only
        // so the URL reads tidily.
        var name = $"{companyCode.ToLowerInvariant()}-{Guid.NewGuid():N}{extension}";

        var absoluteDirectory = Path.Combine(WebRoot, LogosFolder);
        Directory.CreateDirectory(absoluteDirectory);

        await using (var stream = File.Create(Path.Combine(absoluteDirectory, name)))
            await file.CopyToAsync(stream, ct);

        return $"{LogosFolder}/{name}";
    }

    /// <summary>
    /// True when an uploaded SVG contains anything that could execute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately blunt: any <c>&lt;script&gt;</c>, any <c>on…=</c> handler,
    /// any <c>javascript:</c> URL, and anything that pulls in another document
    /// (<c>&lt;foreignObject&gt;</c>, <c>&lt;use href&gt;</c> to a remote file).
    /// A logo needs none of those, so refusing all of them costs a workshop
    /// nothing and closes the hole.
    /// </para>
    /// <para>
    /// This is not a sanitiser and does not pretend to be one — it does not
    /// clean the file, it declines it, and the caller says so. Sanitising SVG
    /// properly is a library's job, and the honest version of that judgement is
    /// to reject rather than to half-clean and serve the result from our own
    /// origin.
    /// </para>
    /// </remarks>
    public static bool LooksLikeScriptedSvg(string markup)
    {
        // Entities first, or the obvious spellings walk past a substring check:
        // "&#106;avascript:" is javascript: to a parser and not to us.
        var text = WebUtility.HtmlDecode(markup);

        // Two normalisations, because the two families of check want opposite
        // things from whitespace.
        //
        // Tag and URL names must survive it being *removed*: "java\nscript:" is
        // a live URL to a browser, and so is a leading-space href.
        var squashed = Regex.Replace(text, @"\s+", "", RegexOptions.None, TimeSpan.FromSeconds(2));

        // Event handlers want it *kept*. Removing it welds the attribute onto
        // the tag — "<svg onload=" becomes "<svgonload=" — and there is then no
        // word boundary in front of "on" for \b to match, so every handler in
        // the file becomes invisible. That is not hypothetical: it is what this
        // check did before, and onload= is the whole reason it exists.
        var spaced = Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(2));

        return squashed.Contains("<script", StringComparison.OrdinalIgnoreCase)
               || squashed.Contains("<foreignobject", StringComparison.OrdinalIgnoreCase)
               || squashed.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
               || squashed.Contains("data:text/html", StringComparison.OrdinalIgnoreCase)
               // An attribute always follows whitespace, which is exactly what
               // `spaced` guarantees is still there. Two letters minimum after
               // "on" so prose like "step one=2" in a <text> node does not trip
               // it — every real handler (onload, onclick, onbegin) has more.
               || Regex.IsMatch(
                   spaced, @"\son[a-z]{2,}\s*=",
                   RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// An absolute URL for a stored file, or null when there is nothing stored.
    /// </summary>
    /// <remarks>
    /// Absolute because the clients are on other origins — the dashboard on its
    /// own port, the Flutter app on a phone — and a relative path resolves
    /// against them rather than against this API.
    /// </remarks>
    public static string? PublicUrl(HttpRequest request, string? relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? null
            : $"{request.Scheme}://{request.Host}/{relativePath}";

    /// <summary>
    /// Deletes a stored file. A missing file is not an error — the row is going
    /// away either way, and a half-deleted photo should not block the request.
    /// </summary>
    public void Delete(string relativePath)
    {
        try
        {
            var absolute = Path.Combine(WebRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolute)) File.Delete(absolute);
        }
        catch (Exception ex)
        {
            // Losing the bytes is untidy; failing the user's request over it
            // would be worse.
            logger.LogWarning(ex, "Could not delete photo {Path}", relativePath);
        }
    }

    /// <summary>Deletes a whole job's photo folder, used when the job is deleted.</summary>
    public void DeleteJobFolder(string jobCardId)
    {
        try
        {
            var directory = Path.Combine(WebRoot, UploadsFolder, jobCardId);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete photo folder for {JobCardId}", jobCardId);
        }
    }

    /// <summary>
    /// wwwroot, created on demand. A fresh clone has no such folder, and
    /// <see cref="IWebHostEnvironment.WebRootPath"/> is null until it exists.
    /// </summary>
    private string WebRoot
    {
        get
        {
            var root = environment.WebRootPath
                       ?? Path.Combine(environment.ContentRootPath, "wwwroot");

            Directory.CreateDirectory(root);
            return root;
        }
    }
}
