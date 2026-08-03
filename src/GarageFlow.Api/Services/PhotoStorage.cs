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
