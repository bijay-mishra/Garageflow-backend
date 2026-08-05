using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Mapping;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// The signed-in user's notification feed.
/// </summary>
/// <remarks>
/// Pull, not push: the app polls this while it is open. That is the whole
/// mechanism, and it needs no external service, no device tokens and no
/// credentials — the trade being that nothing arrives while the app is closed.
/// Adding real push later means keeping these rows and sending a copy through
/// FCM as they are written, not replacing anything here.
///
/// Every action is scoped to the caller's own id, so there is no such thing as
/// reading someone else's feed.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/notifications")]
[Produces("application/json")]
public class NotificationsController(
    GarageFlowDbContext db,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// The feed, newest first, with the unread count alongside it.
    /// </summary>
    /// <remarks>
    /// Count and page come back together so the app can paint the list and the
    /// badge from one response — this is polled, and a second round trip per
    /// poll would double the traffic for a number.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<ApiResponse<NotificationFeedDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NotificationFeedDto>>> Feed(
        [FromQuery] TableQuery query,
        [FromQuery] bool unreadOnly = false,
        CancellationToken ct = default)
    {
        var userId = CurrentUserService.IdOf(User);
        if (userId is null) return Forbid();

        var mine = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);

        var unreadCount = await mine.CountAsync(n => n.ReadAt == null, ct);

        if (unreadOnly)
            mine = mine.Where(n => n.ReadAt == null);

        var page = await mine
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .ToDto()
            // The feed is a phone screen, not a report: default to a page rather
            // than every notification the account has ever received.
            .ToPagedListAsync(query.EffectiveSkip, query.EffectiveTake ?? 50, ct);

        var feed = new NotificationFeedDto(unreadCount, page.List);

        return Ok(ApiResponse<NotificationFeedDto>.Ok(
            feed,
            unreadCount == 0 ? "You are all caught up." : $"{unreadCount} unread notification(s)."));
    }

    /// <summary>Unread count on its own — cheap enough to poll often for a badge.</summary>
    [HttpGet("unread-count")]
    [ProducesResponseType<ApiResponse<int>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<int>>> UnreadCount(CancellationToken ct)
    {
        var userId = CurrentUserService.IdOf(User);
        if (userId is null) return Forbid();

        var count = await db.Notifications
            .CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);

        return Ok(ApiResponse<int>.Ok(count, $"{count} unread."));
    }

    /// <summary>Marks one notification read.</summary>
    [HttpPut("{id:int}/read")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> MarkRead(int id, CancellationToken ct)
    {
        var userId = CurrentUserService.IdOf(User);
        if (userId is null) return Forbid();

        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);

        if (notification is null)
            return NotFound(ApiResponse.Failure("That notification was not found."));

        // Idempotent: opening the same item twice keeps the first read time
        // rather than moving it.
        notification.ReadAt ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success("Marked as read."));
    }

    /// <summary>Marks the whole feed read.</summary>
    [HttpPut("read-all")]
    [ProducesResponseType<ApiResponse<int>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<int>>> MarkAllRead(CancellationToken ct)
    {
        var userId = CurrentUserService.IdOf(User);
        if (userId is null) return Forbid();

        var now = clock.GetUtcNow().UtcDateTime;

        // A set-based update: the feed can be long, and loading every row to
        // stamp a date on it would be pure waste.
        var updated = await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAt, now), ct);

        return Ok(ApiResponse<int>.Ok(updated, updated == 0 ? "Nothing to mark." : $"{updated} marked as read."));
    }

    /// <summary>Deletes one notification from the feed.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Delete(int id, CancellationToken ct)
    {
        var userId = CurrentUserService.IdOf(User);
        if (userId is null) return Forbid();

        var removed = await db.Notifications
            .Where(n => n.Id == id && n.UserId == userId)
            .ExecuteDeleteAsync(ct);

        return removed == 0
            ? NotFound(ApiResponse.Failure("That notification was not found."))
            : Ok(ApiResponse.Success("Notification removed."));
    }

    /// <summary>Whether this account wants its phone to buzz.</summary>
    [HttpGet("preferences")]
    [ProducesResponseType<ApiResponse<NotificationPreferencesDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NotificationPreferencesDto>>> Preferences(
        CancellationToken ct)
    {
        var userId = CurrentUserService.IdOf(User);
        if (userId is null) return Forbid();

        var enabled = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.NotificationsEnabled)
            .FirstOrDefaultAsync(ct);

        return Ok(ApiResponse<NotificationPreferencesDto>.Ok(
            new NotificationPreferencesDto { Enabled = enabled }, "Preferences loaded."));
    }

    /// <summary>
    /// Turns push on or off for this account.
    /// </summary>
    /// <remarks>
    /// Only ever the caller's own — there is no user id in the route, so this
    /// cannot be pointed at somebody else's phone. Switching it off silences
    /// delivery, not the record: the in-app feed keeps filling, because it is
    /// this account's history of what happened and muting a phone should not
    /// erase it.
    /// </remarks>
    [HttpPut("preferences")]
    [ProducesResponseType<ApiResponse<NotificationPreferencesDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<NotificationPreferencesDto>>> SetPreferences(
        NotificationPreferencesRequest request, CancellationToken ct)
    {
        var userId = CurrentUserService.IdOf(User);
        if (userId is null) return Forbid();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return Forbid();

        user.NotificationsEnabled = request.Enabled;
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<NotificationPreferencesDto>.Ok(
            new NotificationPreferencesDto { Enabled = user.NotificationsEnabled },
            request.Enabled ? "Notifications on." : "Notifications off."));
    }
}
