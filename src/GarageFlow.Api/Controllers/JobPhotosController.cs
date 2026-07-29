using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Mapping;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// Photos attached to a job card — the mechanic app's camera roll.
/// </summary>
/// <remarks>
/// Bytes go to disk under <c>wwwroot/uploads</c> and are served as static files;
/// only the path is stored. Access is checked per role: a mechanic may act on
/// their own jobs, staff on any job, and a customer may look at photos of their
/// own vehicle but never upload.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/job-cards/{jobCardId}/photos")]
[Produces("application/json")]
public class JobPhotosController(
    GarageFlowDbContext db,
    PhotoStorage storage,
    CurrentUserService currentUser,
    NotificationService notifications,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Photos on a job card, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedList<JobPhotoDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PagedList<JobPhotoDto>>>> List(
        string jobCardId, CancellationToken ct)
    {
        if (!await CanReadAsync(jobCardId, ct))
            return NotFound(ApiResponse.Failure($"Job '{jobCardId}' was not found."));

        var page = await db.JobPhotos.AsNoTracking()
            .Where(p => p.JobCardId == jobCardId)
            .OrderByDescending(p => p.UploadedAt)
            .ToDto(BaseUrl)
            .ToPagedListAsync(new TableQuery(), ct);

        return Ok(ApiResponse<PagedList<JobPhotoDto>>.Ok(
            page,
            page.Count == 0 ? "No photos on this job yet." : $"{page.Count} photo(s)."));
    }

    /// <summary>
    /// Uploads a photo against a job card. <c>multipart/form-data</c> with a
    /// <c>file</c> part, plus optional <c>kind</c> and <c>caption</c> fields.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Owner,Manager,Advisor,Mechanic")]
    [RequestSizeLimit(PhotoStorage.MaxBytes + 8192)] // payload + multipart envelope
    [ProducesResponseType<ApiResponse<JobPhotoDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<JobPhotoDto>>> Upload(
        string jobCardId,
        IFormFile file,
        [FromForm] UploadJobPhotoRequest request,
        CancellationToken ct)
    {
        var job = await db.JobCards
            .Include(j => j.Vehicle)
            .FirstOrDefaultAsync(j => j.Id == jobCardId, ct);

        if (job is null)
            return NotFound(ApiResponse.Failure($"Job '{jobCardId}' was not found."));

        // A mechanic may only photograph their own work. Staff may photograph
        // anything.
        var mechanicName = await currentUser.MechanicNameAsync(User, ct);

        if (mechanicName is not null && job.Mechanic != mechanicName)
            return NotFound(ApiResponse.Failure($"Job '{jobCardId}' was not found among your assigned jobs."));

        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse.Failure("Choose a photo to upload."));

        if (file.Length > PhotoStorage.MaxBytes)
            return BadRequest(ApiResponse.Failure(
                $"That photo is {file.Length / 1024 / 1024f:0.#} MB. The limit is {PhotoStorage.MaxBytes / 1024 / 1024} MB."));

        if (!PhotoStorage.IsAllowed(file.ContentType))
            return BadRequest(ApiResponse.Failure($"Only images are accepted ({PhotoStorage.AllowedList})."));

        var user = await currentUser.GetAsync(User, ct);
        var path = await storage.SaveAsync(file, jobCardId, ct);

        var photo = new JobPhoto
        {
            JobCardId = jobCardId,
            Path = path,
            // Kept only to label a download. It is never used to build a path.
            FileName = Path.GetFileName(file.FileName),
            SizeBytes = file.Length,
            ContentType = file.ContentType,
            Kind = request.Kind,
            Caption = request.Caption.Trim(),
            UploadedBy = mechanicName ?? user?.FullName ?? "",
            UploadedAt = clock.GetUtcNow().UtcDateTime,
        };

        db.JobPhotos.Add(photo);

        if (job.Vehicle is not null)
        {
            await notifications.NotifyCustomerAsync(
                job.Vehicle.CustomerId,
                "New photo from the workshop",
                $"A photo was added to job {job.Id} for {job.Vehicle.Plate}.",
                "job",
                job.Id,
                ct);
        }

        await db.SaveChangesAsync(ct);

        var dto = await db.JobPhotos.AsNoTracking()
            .Where(p => p.Id == photo.Id)
            .ToDto(BaseUrl)
            .FirstAsync(ct);

        return CreatedAtAction(nameof(List), new { jobCardId }, ApiResponse<JobPhotoDto>.Ok(dto, "Photo uploaded."));
    }

    /// <summary>Deletes a photo, and the file behind it.</summary>
    [HttpDelete("{photoId:int}")]
    [Authorize(Roles = "Owner,Manager,Advisor,Mechanic")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Delete(string jobCardId, int photoId, CancellationToken ct)
    {
        var photo = await db.JobPhotos
            .FirstOrDefaultAsync(p => p.Id == photoId && p.JobCardId == jobCardId, ct);

        if (photo is null)
            return NotFound(ApiResponse.Failure("That photo was not found."));

        var mechanicName = await currentUser.MechanicNameAsync(User, ct);

        if (mechanicName is not null)
        {
            var isMine = await db.JobCards
                .AnyAsync(j => j.Id == jobCardId && j.Mechanic == mechanicName, ct);

            if (!isMine)
                return NotFound(ApiResponse.Failure("That photo was not found."));
        }

        // File first: a stale row pointing at a deleted file renders as a broken
        // image, but a deleted row leaving the file behind leaks disk forever
        // with nothing left to find it by.
        storage.Delete(photo.Path);

        db.JobPhotos.Remove(photo);
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success("Photo deleted."));
    }

    /// <summary>
    /// Whether the caller may see this job's photos: staff and the assigned
    /// mechanic may, and a customer may for their own vehicle.
    /// </summary>
    private async Task<bool> CanReadAsync(string jobCardId, CancellationToken ct)
    {
        var user = await currentUser.GetAsync(User, ct);
        if (user is null) return false;

        var job = await db.JobCards.AsNoTracking()
            .Where(j => j.Id == jobCardId)
            .Select(j => new { j.Mechanic, j.Vehicle!.CustomerId })
            .FirstOrDefaultAsync(ct);

        if (job is null) return false;

        return user.Role switch
        {
            Vocabulary.MechanicRole => job.Mechanic == user.MechanicName,
            Vocabulary.CustomerRole => job.CustomerId == user.CustomerId,
            _ => true,
        };
    }

    /// <summary>
    /// Origin the app should fetch photos from, e.g. <c>http://10.0.2.2:5100</c>.
    /// Built from the request so it is correct behind a proxy and on a phone,
    /// neither of which sees the same host the server does.
    /// </summary>
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";
}
