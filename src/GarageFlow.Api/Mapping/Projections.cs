using GarageFlow.Api.Contracts;
using GarageFlow.Api.Domain;

namespace GarageFlow.Api.Mapping;

/// <summary>
/// Entity → DTO projections written as expression trees so EF Core translates
/// them into SQL. Derived values (a customer's lifetime spend, an invoice's tax
/// and status, a vehicle's last service date) are computed by the database
/// rather than loaded and summed in memory.
/// </summary>
/// <remarks>
/// Object-initializer syntax matters here: it is what lets EF resolve a DTO
/// member back to its underlying column, so callers can still
/// <c>OrderBy</c> or <c>Where</c> on the projected shape and have it run in SQL.
/// </remarks>
public static class Projections
{
    public static IQueryable<CustomerDto> ToDto(this IQueryable<Customer> source) =>
        source.Select(c => new CustomerDto
        {
            Id = c.Id,
            Name = c.Name,
            Phone = c.Phone,
            Email = c.Email,
            Address = c.Address,
            VehicleCount = c.Vehicles.Count,
            // Lifetime billed, tax included — matches what the invoice list shows.
            TotalSpent = c.Invoices.Sum(i => i.Subtotal + Math.Round(i.Subtotal * i.TaxRate, 2)),
            CreatedAt = c.CreatedAt,
            AvatarColor = c.AvatarColor,
        });

    public static IQueryable<VehicleDto> ToDto(this IQueryable<Vehicle> source) =>
        source.Select(v => new VehicleDto
        {
            Id = v.Id,
            CustomerId = v.CustomerId,
            CustomerName = v.Customer!.Name,
            Make = v.Make,
            Model = v.Model,
            Year = v.Year,
            Plate = v.Plate,
            Vin = v.Vin,
            Type = v.Type,
            Fuel = v.Fuel,
            Odometer = v.Odometer,
            Color = v.Color,
            // Newest completion date across this vehicle's jobs; null if never serviced.
            LastServiceDate = v.JobCards.Where(j => j.CompletedAt != null).Max(j => j.CompletedAt),
        });

    public static IQueryable<JobCardDto> ToDto(this IQueryable<JobCard> source) =>
        source.Select(j => new JobCardDto
        {
            Id = j.Id,
            VehicleId = j.VehicleId,
            VehiclePlate = j.Vehicle!.Plate,
            VehicleLabel = j.Vehicle.Make + " " + j.Vehicle.Model + " " + j.Vehicle.Year,
            CustomerId = j.Vehicle.CustomerId,
            CustomerName = j.Vehicle.Customer!.Name,
            Complaint = j.Complaint,
            Status = j.Status,
            Priority = j.Priority,
            Mechanic = j.Mechanic,
            Odometer = j.Odometer,
            CreatedAt = j.CreatedAt,
            PromisedAt = j.PromisedAt,
            CompletedAt = j.CompletedAt,
            Lines = j.Lines
                .OrderBy(l => l.SortOrder)
                .Select(l => new JobLineDto
                {
                    Description = l.Description,
                    Qty = l.Qty,
                    UnitPrice = l.UnitPrice,
                    Kind = l.Kind,
                })
                .ToList(),
            Total = j.Lines.Sum(l => l.Qty * l.UnitPrice),
        });

    public static IQueryable<InvoiceDto> ToDto(this IQueryable<Invoice> source) =>
        source.Select(i => new InvoiceDto
        {
            Id = i.Id,
            JobCardId = i.JobCardId,
            CustomerId = i.CustomerId,
            CustomerName = i.CustomerName,
            VehiclePlate = i.VehiclePlate,
            IssuedAt = i.IssuedAt,
            Subtotal = i.Subtotal,
            TaxRate = i.TaxRate,
            Tax = Math.Round(i.Subtotal * i.TaxRate, 2),
            Total = i.Subtotal + Math.Round(i.Subtotal * i.TaxRate, 2),
            Paid = i.Paid,
            Due = i.Subtotal + Math.Round(i.Subtotal * i.TaxRate, 2) - i.Paid,
            Status = i.Paid <= 0
                ? "Unpaid"
                : i.Paid >= i.Subtotal + Math.Round(i.Subtotal * i.TaxRate, 2)
                    ? "Paid"
                    : "Partial",
            Method = i.Method,
        });

    public static IQueryable<ActivityDto> ToDto(this IQueryable<Activity> source) =>
        source.Select(a => new ActivityDto
        {
            Id = a.Id,
            At = a.At,
            Text = a.Text,
            Kind = a.Kind,
        });
}
