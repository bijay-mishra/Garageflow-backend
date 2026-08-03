using System.Globalization;
using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Mapping;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>Aggregates for the dashboard home page.</summary>
[Authorize]
[ApiController]
[Route("api/dashboard")]
[Produces("application/json")]
public class DashboardController(
    GarageFlowDbContext db,
    WorkspaceService workspace,
    TimeProvider clock) : ControllerBase
{
    private const int TrendMonths = 6;
    private const int ActivityFeedSize = 6;

    /// <summary>
    /// Every figure the dashboard's cards, charts and activity feed need, in one
    /// request.
    /// </summary>
    /// <remarks>
    /// Revenue is counted from payments on the date they were received — so a
    /// payment settling last month's invoice lands in this month — rather than
    /// from invoice issue dates.
    /// </remarks>
    [HttpGet("summary")]
    [ProducesResponseType<ApiResponse<DashboardSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DashboardSummaryDto>>> Summary(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);
        var prevMonthStart = monthStart.AddMonths(-1);
        var trendStart = monthStart.AddMonths(-(TrendMonths - 1));

        // Every figure below is bounded by the accounting year in the topbar.
        //
        // A consequence worth naming: viewing a closed year reports "revenue
        // today" as zero, because nothing was collected today *within that
        // year*. That is literally true rather than a bug, and the client is
        // told which year is selected (`isCurrentYear` on /api/workspace) so it
        // can say so on the cards instead of leaving a bare zero to be misread.
        var year = await workspace.StaffYearAsync(User, ct);

        var payments = db.Payments.AsNoTracking();

        if (year is not null)
        {
            payments = payments.Where(p =>
                p.At >= year.Start.ToDateTime(TimeOnly.MinValue) &&
                p.At <= year.End.ToDateTime(TimeOnly.MaxValue));
        }

        // Half-open [start, end) ranges over the raw timestamp keep these
        // sargable — no per-row date conversion for SQL Server to work around.
        var revenueToday = await SumPaymentsAsync(payments, today, today.AddDays(1), ct);
        var revenueThisMonth = await SumPaymentsAsync(payments, monthStart, nextMonthStart, ct);
        var revenueLastMonth = await SumPaymentsAsync(payments, prevMonthStart, monthStart, ct);

        // Against a zero baseline any revenue is an infinite gain, so report a
        // flat 0% rather than a meaningless number.
        var revenueDeltaPct = revenueLastMonth == 0
            ? 0m
            : Math.Round((revenueThisMonth - revenueLastMonth) / revenueLastMonth * 100m, 1);

        var jobs = db.JobCards.AsNoTracking();

        if (year is not null)
            jobs = jobs.Where(j => j.CreatedAt >= year.Start && j.CreatedAt <= year.End);

        var openJobs = await jobs.CountAsync(j => Vocabulary.OpenJobStatuses.Contains(j.Status), ct);

        var completedThisMonth = await jobs.CountAsync(
            j => Vocabulary.DoneJobStatuses.Contains(j.Status)
                 && j.CompletedAt != null
                 && j.CompletedAt >= monthStart
                 && j.CompletedAt < nextMonthStart,
            ct);

        // Anything not yet handed back or cancelled is still taking up a bay.
        var vehiclesInShop = await jobs
            .Where(j => j.Status != "Delivered" && j.Status != "Cancelled")
            .Select(j => j.VehicleId)
            .Distinct()
            .CountAsync(ct);

        var activeCustomers = await db.Customers.CountAsync(ct);

        var invoices = db.Invoices.AsNoTracking();

        if (year is not null)
            invoices = invoices.Where(i => i.IssuedAt >= year.Start && i.IssuedAt <= year.End);

        var unpaidTotal = await invoices
            .Select(i => i.Subtotal + Math.Round(i.Subtotal * i.TaxRate, 2) - i.Paid)
            .Where(outstanding => outstanding > 0)
            .SumAsync(outstanding => (decimal?)outstanding, ct) ?? 0m;

        var counts = await jobs
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Ordered by the vocabulary so the chart's legend never reshuffles;
        // empty statuses are dropped.
        var jobStatusBreakdown = Vocabulary.JobStatuses
            .Select(status => new JobStatusCount(status, counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0))
            .Where(s => s.Count > 0)
            .ToList();

        var revenueTrend = await BuildTrendAsync(trendStart, nextMonthStart, ct);

        var recentActivity = await db.Activities.AsNoTracking()
            .OrderByDescending(a => a.At)
            .Take(ActivityFeedSize)
            .ToDto()
            .ToListAsync(ct);

        var summary = new DashboardSummaryDto(
            revenueToday,
            revenueThisMonth,
            revenueDeltaPct,
            openJobs,
            completedThisMonth,
            vehiclesInShop,
            activeCustomers,
            unpaidTotal,
            jobStatusBreakdown,
            revenueTrend,
            recentActivity);

        return Ok(ApiResponse<DashboardSummaryDto>.Ok(
            summary,
            $"Dashboard loaded — {openJobs} open job(s), {vehiclesInShop} vehicle(s) in the shop."));
    }

    /// <summary>Total received in the half-open window [<paramref name="from"/>, <paramref name="to"/>).</summary>
    private static async Task<decimal> SumPaymentsAsync(
        IQueryable<Payment> payments, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.ToDateTime(TimeOnly.MinValue);
        return await payments
            .Where(p => p.At >= start && p.At < end)
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
    }

    /// <summary>
    /// Revenue and job counts for each of the last <see cref="TrendMonths"/>
    /// months. Months with no activity are emitted as zeroes so the chart keeps
    /// an even x-axis.
    /// </summary>
    private async Task<List<RevenuePoint>> BuildTrendAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var trendStart = from.ToDateTime(TimeOnly.MinValue);
        var trendEnd = to.ToDateTime(TimeOnly.MinValue);

        var revenueByMonth = (await db.Payments.AsNoTracking()
            .Where(p => p.At >= trendStart && p.At < trendEnd)
            .GroupBy(p => new { p.At.Year, p.At.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Revenue = g.Sum(p => p.Amount) })
            .ToListAsync(ct))
            .ToDictionary(x => (x.Year, x.Month), x => x.Revenue);

        var jobsByMonth = (await db.JobCards.AsNoTracking()
            .Where(j => j.CreatedAt >= from && j.CreatedAt < to)
            .GroupBy(j => new { j.CreatedAt.Year, j.CreatedAt.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Jobs = g.Count() })
            .ToListAsync(ct))
            .ToDictionary(x => (x.Year, x.Month), x => x.Jobs);

        var months = new List<RevenuePoint>(TrendMonths);
        for (var i = 0; i < TrendMonths; i++)
        {
            var month = from.AddMonths(i);
            var key = (month.Year, month.Month);
            months.Add(new RevenuePoint(
                CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month.Month),
                revenueByMonth.GetValueOrDefault(key),
                jobsByMonth.GetValueOrDefault(key)));
        }

        return months;
    }
}
