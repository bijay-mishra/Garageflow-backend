using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Mapping;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>Billing — invoices raised against job cards, and payments against those invoices.</summary>
[Authorize]
[ApiController]
[Route("api/invoices")]
[Produces("application/json")]
public class InvoicesController(
    GarageFlowDbContext db,
    ActivityLog activity,
    WorkspaceService workspace,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Lists invoices, newest first.</summary>
    /// <remarks>
    /// Paged with <c>skip</c>/<c>take</c>; omit <c>take</c> for every row.
    /// Search matches invoice id, customer name or plate. <c>status</c> filters
    /// to Paid, Partial or Unpaid.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedList<InvoiceDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<InvoiceDto>>>> List(
        [FromQuery] TableQuery query, [FromQuery] string? status, CancellationToken ct)
    {
        var invoices = db.Invoices.AsNoTracking().AsQueryable();

        // Bills belong to the year they were issued in — the one number on this
        // screen an accountant would be asked to reconcile.
        if (await workspace.StaffYearAsync(User, ct) is { } year)
            invoices = invoices.Where(i => i.IssuedAt >= year.Start && i.IssuedAt <= year.End);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            invoices = invoices.Where(i =>
                EF.Functions.Like(i.Id, $"%{term}%") ||
                EF.Functions.Like(i.CustomerName, $"%{term}%") ||
                EF.Functions.Like(i.VehiclePlate, $"%{term}%"));
        }

        // Status is derived, so it can only be filtered after projection.
        var projected = invoices.ToDto();

        if (!string.IsNullOrWhiteSpace(status))
            projected = projected.Where(i => i.Status == status);

        projected = projected.OrderByProperty(query.SortBy, query.Descending);

        if (string.IsNullOrWhiteSpace(query.SortBy))
            projected = projected.OrderByDescending(i => i.IssuedAt).ThenByDescending(i => i.Id);

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<InvoiceDto>>.Ok(
            page,
            page.Count == 0 ? "No invoices found." : $"{page.Count} invoice(s) found."));
    }

    /// <summary>Billing totals across every invoice.</summary>
    /// <remarks>
    /// Separate from the list so the page can page its table without losing the
    /// all-time figures on the cards above it.
    /// </remarks>
    [HttpGet("summary")]
    [ProducesResponseType<ApiResponse<InvoiceSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<InvoiceSummaryDto>>> Summary(CancellationToken ct)
    {
        var invoices = db.Invoices.AsNoTracking().AsQueryable();

        // The same window as the table below these cards. Without it the totals
        // would describe all time while the rows describe one year, and the two
        // would visibly disagree on the same screen.
        if (await workspace.StaffYearAsync(User, ct) is { } year)
            invoices = invoices.Where(i => i.IssuedAt >= year.Start && i.IssuedAt <= year.End);

        var totals = await invoices
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Billed = g.Sum(i => i.Subtotal + Math.Round(i.Subtotal * i.TaxRate, 2)),
                Collected = g.Sum(i => i.Paid),
            })
            .FirstOrDefaultAsync(ct);

        var billed = totals?.Billed ?? 0m;
        var collected = totals?.Collected ?? 0m;

        var summary = new InvoiceSummaryDto
        {
            Billed = billed,
            Collected = collected,
            Outstanding = billed - collected,
        };

        return Ok(ApiResponse<InvoiceSummaryDto>.Ok(summary, "Billing totals loaded."));
    }

    /// <summary>Gets one invoice.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<ApiResponse<InvoiceDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<InvoiceDto>>> Get(string id, CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking().Where(i => i.Id == id).ToDto().FirstOrDefaultAsync(ct);

        if (invoice is null)
            return NotFound(ApiResponse.Failure($"Invoice '{id}' was not found."));

        return Ok(ApiResponse<InvoiceDto>.Ok(invoice, "Invoice loaded."));
    }

    /// <summary>
    /// Everything a printed bill needs, in one request.
    /// </summary>
    /// <remarks>
    /// The job card is looked up by id rather than joined: an invoice has no
    /// foreign key to one, on purpose, so that a bill outlives the work it was
    /// raised for. A deleted job means no itemised breakdown, not a failed
    /// print — the invoice's own totals are the financial record either way.
    /// </remarks>
    [HttpGet("{id}/print")]
    [ProducesResponseType<ApiResponse<InvoicePrintDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<InvoicePrintDto>>> Print(string id, CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking().Where(i => i.Id == id).ToDto().FirstOrDefaultAsync(ct);

        if (invoice is null)
            return NotFound(ApiResponse.Failure($"Invoice '{id}' was not found."));

        var customer = await db.Customers.AsNoTracking()
            .Where(c => c.Id == invoice.CustomerId)
            .Select(c => new { c.Address, c.Phone, c.Email })
            .FirstOrDefaultAsync(ct);

        var job = await db.JobCards.AsNoTracking()
            .Where(j => j.Id == invoice.JobCardId)
            .Select(j => new
            {
                j.Complaint,
                j.Mechanic,
                j.Odometer,
                j.CompletedAt,
                VehicleLabel = j.Vehicle!.Make + " " + j.Vehicle.Model + " " + j.Vehicle.Year,
                Lines = j.Lines
                    .OrderBy(l => l.SortOrder)
                    .Select(l => new JobLineDto
                    {
                        Description = l.Description,
                        Qty = l.Qty,
                        UnitPrice = l.UnitPrice,
                        Kind = l.Kind,
                        ServiceId = l.ServiceId,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.InvoiceId == id)
            .OrderBy(p => p.At)
            // Only money that actually arrived. A Pending attempt on a bill
            // being printed is not a receipt and must not read as one.
            .Where(p => p.Status == "Completed")
            .Select(p => new PaymentDto
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
            })
            .ToListAsync(ct);

        var document = new InvoicePrintDto
        {
            Invoice = invoice,
            Payments = payments,
            CustomerAddress = customer?.Address ?? "",
            CustomerPhone = customer?.Phone ?? "",
            CustomerEmail = customer?.Email ?? "",
            VehicleLabel = job?.VehicleLabel ?? "",
            Odometer = job?.Odometer ?? 0,
            Complaint = job?.Complaint ?? "",
            Mechanic = job?.Mechanic ?? "",
            CompletedAt = job?.CompletedAt,
            Lines = job?.Lines ?? [],
            HasJobCard = job is not null,
        };

        return Ok(ApiResponse<InvoicePrintDto>.Ok(document, $"Invoice {invoice.Id} ready to print."));
    }

    /// <summary>Lists the payments recorded against an invoice, oldest first.</summary>
    [HttpGet("{id}/payments")]
    [ProducesResponseType<ApiResponse<PagedList<PaymentDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PagedList<PaymentDto>>>> Payments(
        string id, [FromQuery] TableQuery query, CancellationToken ct)
    {
        if (!await db.Invoices.AnyAsync(i => i.Id == id, ct))
            return NotFound(ApiResponse.Failure($"Invoice '{id}' was not found."));

        var page = await db.Payments.AsNoTracking()
            .Where(p => p.InvoiceId == id)
            .OrderBy(p => p.At)
            .Select(p => new PaymentDto
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
            })
            .ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<PaymentDto>>.Ok(page, $"{page.Count} payment(s) found."));
    }

    /// <summary>
    /// How much money came in through each channel.
    /// </summary>
    /// <remarks>
    /// The end-of-day question: cash has to be counted in a drawer, online and
    /// bank have to be reconciled against somebody else's statement, and mixing
    /// the three is how a shop loses track of what it is owed.
    ///
    /// Only Completed payments are counted. Attempts still open are reported
    /// separately as a count, never as an amount — a customer halfway through
    /// eSewa has not paid anything.
    /// </remarks>
    [HttpGet("collections")]
    [ProducesResponseType<ApiResponse<CollectionsByChannelDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CollectionsByChannelDto>>> Collections(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var settled = db.Payments.AsNoTracking().Where(p => p.Status == "Completed");

        // The accounting year bounds the window; an explicit from/to narrows it
        // further. A caller cannot widen past the selected year by passing dates
        // outside it, which is the point of applying this first.
        if (await workspace.StaffYearAsync(User, ct) is { } year)
        {
            settled = settled.Where(p =>
                p.At >= year.Start.ToDateTime(TimeOnly.MinValue) &&
                p.At <= year.End.ToDateTime(TimeOnly.MaxValue));
        }

        if (from is { } start) settled = settled.Where(p => p.At >= start.ToDateTime(TimeOnly.MinValue));
        if (to is { } end) settled = settled.Where(p => p.At <= end.ToDateTime(TimeOnly.MaxValue));

        // One grouped round trip rather than three sums over the same rows.
        var byChannel = await settled
            .GroupBy(p => p.Channel)
            .Select(g => new { Channel = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(ct);

        decimal Of(string channel) =>
            byChannel.FirstOrDefault(x => x.Channel == channel)?.Amount ?? 0;

        var summary = new CollectionsByChannelDto
        {
            Cash = Of("cash"),
            Online = Of("online"),
            Bank = Of("bank"),
            Total = byChannel.Sum(x => x.Amount),
            PendingCount = await db.Payments.CountAsync(p => p.Status == "Pending", ct),
        };

        return Ok(ApiResponse<CollectionsByChannelDto>.Ok(summary, "Collections loaded."));
    }

    /// <summary>
    /// Raises an invoice. Tax, total and status are computed from the subtotal,
    /// rate and amount paid; the customer name and plate are snapshotted here so
    /// later edits to those records cannot rewrite a bill.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<InvoiceDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<InvoiceDto>>> Create(
        CreateInvoiceRequest request, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CustomerId, ct);

        if (customer is null)
            return BadRequest(ApiResponse.Failure($"Customer '{request.CustomerId}' does not exist."));

        // Prefer the plate the job card actually points at; fall back to whatever
        // the client sent for invoices raised outside a job.
        var plate = await db.JobCards.AsNoTracking()
            .Where(j => j.Id == request.JobCardId)
            .Select(j => j.Vehicle!.Plate)
            .FirstOrDefaultAsync(ct) ?? request.VehiclePlate ?? "";

        var now = clock.GetLocalNow().DateTime;

        var invoice = new Invoice
        {
            Id = Ids.Next(await db.Invoices.IgnoreQueryFilters().Select(i => i.Id).ToListAsync(ct), "INV", pad: 4),
            JobCardId = request.JobCardId,
            CustomerId = customer.Id,
            CustomerName = customer.Name,
            VehiclePlate = plate,
            IssuedAt = request.IssuedAt == default ? DateOnly.FromDateTime(now) : request.IssuedAt,
            Subtotal = request.Subtotal,
            TaxRate = request.TaxRate,
            Paid = 0,
            Method = null,
        };

        // Anything settled up front is booked as a real payment so the ledger
        // and the Paid column can never disagree.
        if (request.Paid > 0)
        {
            var amount = Math.Min(request.Paid, invoice.Total);
            invoice.Paid = amount;
            invoice.Method = request.Method ?? "Cash";
            invoice.Payments.Add(new Payment { Amount = amount, Method = invoice.Method, At = now });
        }

        db.Invoices.Add(invoice);
        activity.Add($"Invoice {invoice.Id} raised for {invoice.CustomerName}", "invoice");
        await db.SaveChangesAsync(ct);

        var dto = await db.Invoices.AsNoTracking().Where(i => i.Id == invoice.Id).ToDto().FirstAsync(ct);

        return CreatedAtAction(
            nameof(Get),
            new { id = invoice.Id },
            ApiResponse<InvoiceDto>.Ok(dto, $"Invoice {invoice.Id} raised for {invoice.CustomerName}."));
    }

    /// <summary>
    /// Records a payment against an invoice and recomputes its status.
    /// Overpayment is clamped to the outstanding balance.
    /// </summary>
    [HttpPost("{id}/payments")]
    [ProducesResponseType<ApiResponse<InvoiceDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<InvoiceDto>>> RecordPayment(
        string id, RecordPaymentRequest request, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct);

        if (invoice is null)
            return NotFound(ApiResponse.Failure($"Invoice '{id}' was not found."));

        var outstanding = invoice.Total - invoice.Paid;

        if (outstanding <= 0)
            return Conflict(ApiResponse.Failure($"Invoice {id} is already paid in full."));

        var amount = Math.Min(request.Amount, outstanding);
        var now = clock.GetLocalNow().DateTime;

        invoice.Paid += amount;
        invoice.Method = request.Method;
        invoice.Payments.Add(new Payment
        {
            Amount = amount,
            Method = request.Method,
            Channel = Vocabulary.ChannelFor(request.Method),
            // Recorded by a person who has the money in front of them, so it is
            // settled on arrival — unlike a gateway payment, which starts
            // Pending and waits for the provider to confirm.
            Status = "Completed",
            Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
            InitiatedAt = now,
            At = now,
        });

        activity.Add($"Payment received on {invoice.Id}", "invoice");
        await db.SaveChangesAsync(ct);

        var dto = await db.Invoices.AsNoTracking().Where(i => i.Id == id).ToDto().FirstAsync(ct);

        // Say what actually happened — the amount may have been clamped.
        var message = dto.Status == "Paid"
            ? $"Payment of {amount:N2} recorded. Invoice {id} is now settled in full."
            : $"Payment of {amount:N2} recorded. {dto.Total - dto.Paid:N2} still due on {id}.";

        return Ok(ApiResponse<InvoiceDto>.Ok(dto, message));
    }

    /// <summary>
    /// Updates an invoice. Only the fields present in the body are applied.
    /// </summary>
    /// <remarks>
    /// Setting <c>paid</c> here overwrites the running total without writing a
    /// payment row — use <c>POST /api/invoices/{id}/payments</c> for anything
    /// that is actually money changing hands.
    /// </remarks>
    [HttpPut("{id}")]
    [ProducesResponseType<ApiResponse<InvoiceDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<InvoiceDto>>> Update(
        string id, UpdateInvoiceRequest request, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct);

        if (invoice is null)
            return NotFound(ApiResponse.Failure($"Invoice '{id}' was not found."));

        if (request.IssuedAt is { } issuedAt) invoice.IssuedAt = issuedAt;
        if (request.Subtotal is { } subtotal) invoice.Subtotal = subtotal;
        if (request.TaxRate is { } taxRate) invoice.TaxRate = taxRate;
        if (request.Method is not null) invoice.Method = request.Method;
        if (request.Paid is { } paid) invoice.Paid = Math.Min(paid, invoice.Total);

        await db.SaveChangesAsync(ct);

        var dto = await db.Invoices.AsNoTracking().Where(i => i.Id == id).ToDto().FirstAsync(ct);

        return Ok(ApiResponse<InvoiceDto>.Ok(dto, $"Invoice {id} updated successfully."));
    }

    /// <summary>Deletes an invoice and its payment history.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Delete(string id, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct);

        if (invoice is null)
            return NotFound(ApiResponse.Failure($"Invoice '{id}' was not found."));

        db.Invoices.Remove(invoice); // payments cascade
        activity.Add($"Invoice {invoice.Id} deleted", "invoice");
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success($"Invoice {id} deleted successfully."));
    }
}
