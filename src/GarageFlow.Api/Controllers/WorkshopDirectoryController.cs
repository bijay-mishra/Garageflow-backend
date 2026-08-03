using System.Security.Claims;
using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// The garages a customer can choose from, and joining one.
/// </summary>
/// <remarks>
/// The consumer-facing half of the product. Everything else in this API answers
/// "what is happening inside *my* workshop"; this answers "which workshops are
/// there at all", which is a different question with a different audience.
///
/// Listing is anonymous on purpose — somebody deciding whether to install the app
/// should be able to see whether any garage near them uses it. Joining is not:
/// that creates a customer record inside a real business's books.
///
/// Only workshops that have opted in appear. A garage that bought this to run its
/// own books has not agreed to be advertised, and publishing its name and address
/// without asking would be a decision made on its behalf.
/// </remarks>
[ApiController]
[Route("api/garages")]
[Produces("application/json")]
public class WorkshopDirectoryController(
    GarageFlowDbContext db,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Garages using GarageFlow.
    /// </summary>
    /// <remarks>
    /// Pass <c>lat</c> and <c>lng</c> — the phone's own position — and the list
    /// comes back nearest first with a distance on each card. Without them it is
    /// alphabetical, which is the honest fallback: there is no meaningful
    /// ordering of garages to somebody whose location is unknown.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<PagedList<WorkshopCardDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<WorkshopCardDto>>>> List(
        [FromQuery] TableQuery query,
        [FromQuery] double? lat,
        [FromQuery] double? lng,
        CancellationToken ct = default)
    {
        var workshops = db.Workshops.AsNoTracking().Where(w => w.IsListed);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            workshops = workshops.Where(w =>
                EF.Functions.Like(w.Name, $"%{term}%") ||
                EF.Functions.Like(w.Address, $"%{term}%"));
        }

        // Which of these the caller has already joined. Anonymous callers get an
        // empty set, and every card reads "not joined", which is correct.
        var myLinks = await MyLinksAsync(ct);

        var rows = await workshops
            .Select(w => new
            {
                w.CompanyCode,
                w.Name,
                w.Address,
                w.Phone,
                w.About,
                w.OpeningHours,
                w.Latitude,
                w.Longitude,
                ServiceCount = db.Services.Count(s => s.IsActive && s.IsBookable),
            })
            .ToListAsync(ct);

        var cards = rows
            .Select(w => new WorkshopCardDto
            {
                CompanyCode = w.CompanyCode,
                Name = w.Name,
                Address = w.Address,
                Phone = w.Phone,
                About = w.About,
                OpeningHours = w.OpeningHours,
                Latitude = w.Latitude,
                Longitude = w.Longitude,
                ServiceCount = w.ServiceCount,
                IsJoined = myLinks.ContainsKey(w.CompanyCode),
                IsPrimary = myLinks.TryGetValue(w.CompanyCode, out var primary) && primary,
                // Computed here rather than in SQL: haversine has no translation,
                // and a directory is tens of rows, not thousands.
                DistanceKm = lat is { } fromLat && lng is { } fromLng && w.Latitude is { } toLat && w.Longitude is { } toLng
                    ? Math.Round(Geo.DistanceKm(fromLat, fromLng, toLat, toLng), 1)
                    : null,
            })
            .OrderBy(c => c.DistanceKm ?? double.MaxValue)
            .ThenBy(c => c.Name)
            .ToList();

        var page = new PagedList<WorkshopCardDto>(
            cards.Skip(query.EffectiveSkip).Take(query.EffectiveTake ?? cards.Count).ToList(),
            cards.Count);

        return Ok(ApiResponse<PagedList<WorkshopCardDto>>.Ok(
            page,
            page.Count == 0
                ? "No garages are listed yet."
                : $"{page.Count} garage(s) near you."));
    }

    /// <summary>The garages this customer has joined.</summary>
    [HttpGet("mine")]
    [Authorize(Roles = Vocabulary.CustomerRole)]
    [ProducesResponseType<ApiResponse<IReadOnlyList<WorkshopCardDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<WorkshopCardDto>>>> Mine(CancellationToken ct)
    {
        var myLinks = await MyLinksAsync(ct);

        if (myLinks.Count == 0)
        {
            return Ok(ApiResponse<IReadOnlyList<WorkshopCardDto>>.Ok(
                [], "You have not joined a garage yet."));
        }

        var codes = myLinks.Keys.ToList();

        var cards = await db.Workshops.AsNoTracking()
            .Where(w => codes.Contains(w.CompanyCode))
            .Select(w => new WorkshopCardDto
            {
                CompanyCode = w.CompanyCode,
                Name = w.Name,
                Address = w.Address,
                Phone = w.Phone,
                About = w.About,
                OpeningHours = w.OpeningHours,
                Latitude = w.Latitude,
                Longitude = w.Longitude,
                ServiceCount = 0,
                IsJoined = true,
                IsPrimary = false,
                DistanceKm = null,
            })
            .ToListAsync(ct);

        // Flags applied after the projection: EF cannot see into the dictionary.
        var withFlags = cards
            .Select(c => c with { IsPrimary = myLinks[c.CompanyCode] })
            .OrderByDescending(c => c.IsPrimary)
            .ThenBy(c => c.Name)
            .ToList();

        return Ok(ApiResponse<IReadOnlyList<WorkshopCardDto>>.Ok(
            withFlags, $"{withFlags.Count} garage(s)."));
    }

    /// <summary>
    /// Joins a garage, creating the customer record inside it.
    /// </summary>
    /// <remarks>
    /// This is the moment a person becomes a customer of a business. A real
    /// record is created in that garage's own books — with the person's name,
    /// phone and email — because from the garage's side there is no difference
    /// between someone who walked in and someone who arrived through the app.
    ///
    /// Joining twice is joining once: the existing link is returned untouched, so
    /// a double tap does not leave a workshop with two records for one person.
    /// </remarks>
    [HttpPost("{companyCode}/join")]
    [Authorize(Roles = Vocabulary.CustomerRole)]
    [ProducesResponseType<ApiResponse<WorkshopCardDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<WorkshopCardDto>>> Join(
        string companyCode, CancellationToken ct)
    {
        var code = companyCode.Trim().ToUpperInvariant();

        var workshop = await db.Workshops.AsNoTracking()
            .FirstOrDefaultAsync(w => w.CompanyCode == code, ct);

        // A garage that has not listed itself cannot be joined, even by someone
        // who guessed its code — being unlisted is a choice, not an oversight.
        if (workshop is null || !workshop.IsListed)
            return NotFound(ApiResponse.Failure("That garage is not available to join."));

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

        var user = await db.Users
            .Include(u => u.WorkshopLinks)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return Unauthorized(ApiResponse.Failure("Please sign in again."));

        var existing = user.WorkshopLinks.FirstOrDefault(l => l.CompanyCode == code);

        if (existing is not null)
        {
            return Ok(ApiResponse<WorkshopCardDto>.Ok(
                Card(workshop, joined: true, primary: existing.IsPrimary),
                $"You are already with {workshop.Name}."));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);

        // Reuse a record the garage already holds for this person, matched on
        // email. A shop that served them last year should not end up with a
        // duplicate — and their history should be waiting for them.
        // Scoped to the garage being joined, not the one the token is bound to
        // — the whole point of joining a second garage is that it keeps its own
        // books. Before tenant isolation this matched across every company, so
        // a customer with a bike at one garage and a car at another would have
        // seen both vehicles at both.
        var customer = string.IsNullOrWhiteSpace(user.Email)
            ? null
            : await db.Customers
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    c => c.Email == user.Email && c.CompanyCode == code, ct);

        if (customer is null)
        {
            // Every customer id, across every company: CUS-012 must mean one row
            // globally, or two companies would mint the same id.
            var existingIds = await db.Customers
                .IgnoreQueryFilters()
                .Select(c => c.Id)
                .ToListAsync(ct);

            customer = new Customer
            {
                Id = Ids.Next(existingIds, "CUS"),
                // Set explicitly: the save-time stamp would otherwise write the
                // company the *caller* is in, which is the wrong one here.
                CompanyCode = code,
                Name = user.FullName,
                Phone = user.Phone ?? "",
                Email = user.Email,
                CreatedAt = today,
                AvatarColor = Vocabulary.AvatarColors[existingIds.Count % Vocabulary.AvatarColors.Length],
            };

            db.Customers.Add(customer);
        }

        var isFirst = user.WorkshopLinks.Count == 0;

        db.UserWorkshopLinks.Add(new UserWorkshopLink
        {
            UserId = user.Id,
            CompanyCode = code,
            CustomerId = customer.Id,
            IsPrimary = isFirst,
            JoinedAt = now,
        });

        // The first garage joined becomes the one the app opens on, so the very
        // next request already has somewhere to look.
        if (isFirst)
        {
            user.CompanyCode = code;
            user.CustomerId = customer.Id;
            user.Workshop = workshop.Name;
        }

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<WorkshopCardDto>.Ok(
            Card(workshop, joined: true, primary: isFirst),
            isFirst
                ? $"You are now with {workshop.Name}. Add a vehicle to book your first service."
                : $"Added {workshop.Name}. Switch between garages from your account."));
    }

    /// <summary>Company code → is it the primary, for the caller. Empty when anonymous.</summary>
    private async Task<Dictionary<string, bool>> MyLinksAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(userId)) return [];

        return await db.UserWorkshopLinks.AsNoTracking()
            .Where(l => l.UserId == userId)
            .ToDictionaryAsync(l => l.CompanyCode, l => l.IsPrimary, ct);
    }

    private static WorkshopCardDto Card(Workshop w, bool joined, bool primary) => new()
    {
        CompanyCode = w.CompanyCode,
        Name = w.Name,
        Address = w.Address,
        Phone = w.Phone,
        About = w.About,
        OpeningHours = w.OpeningHours,
        Latitude = w.Latitude,
        Longitude = w.Longitude,
        ServiceCount = 0,
        IsJoined = joined,
        IsPrimary = primary,
        DistanceKm = null,
    };
}
