using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Services;

/// <summary>
/// Writes rows into users' notification feeds.
/// </summary>
/// <remarks>
/// Queues rows on the change tracker and leaves the <c>SaveChangesAsync</c> to
/// the caller, exactly like <see cref="ActivityLog"/>. That way a notification
/// and the change it describes commit together — a customer can never be told
/// their job is ready by a transaction that then rolls back.
/// </remarks>
public class NotificationService(GarageFlowDbContext db, TimeProvider clock)
{
    /// <summary>Queues one notification for one user.</summary>
    public void Notify(string userId, string title, string body, string kind, string? entityId = null)
    {
        db.Notifications.Add(new Notification
        {
            UserId = userId,
            Title = title,
            Body = body,
            Kind = kind,
            EntityId = entityId,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        });
    }

    /// <summary>
    /// Queues a notification for the customer who owns <paramref name="customerId"/>,
    /// if they have an app login. Customers without one are simply skipped —
    /// not every customer is on the app, and that is not an error.
    /// </summary>
    public async Task NotifyCustomerAsync(
        string customerId, string title, string body, string kind, string? entityId = null,
        CancellationToken ct = default)
    {
        var userIds = await db.Users
            .Where(u => u.CustomerId == customerId && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var userId in userIds)
            Notify(userId, title, body, kind, entityId);
    }

    /// <summary>
    /// Queues a notification for the mechanic assigned to a job, matched on the
    /// name written on the job card. No account for that name means no-op.
    /// </summary>
    public async Task NotifyMechanicAsync(
        string mechanicName, string title, string body, string kind, string? entityId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mechanicName)) return;

        var userIds = await db.Users
            .Where(u => u.MechanicName == mechanicName && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var userId in userIds)
            Notify(userId, title, body, kind, entityId);
    }

    /// <summary>
    /// Queues a notification for every active staff account — used when a
    /// customer does something the workshop has to act on, like requesting a
    /// booking.
    /// </summary>
    public async Task NotifyStaffAsync(
        string companyCode, string title, string body, string kind, string? entityId = null,
        CancellationToken ct = default)
    {
        var userIds = await db.Users
            .Where(u => u.CompanyCode == companyCode
                        && u.IsActive
                        && Vocabulary.StaffRoles.Contains(u.Role))
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var userId in userIds)
            Notify(userId, title, body, kind, entityId);
    }
}
