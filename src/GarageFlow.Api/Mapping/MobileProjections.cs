using GarageFlow.Api.Contracts;
using GarageFlow.Api.Domain;

namespace GarageFlow.Api.Mapping;

/// <summary>
/// IQueryable projections for the mobile DTOs.
/// </summary>
/// <remarks>
/// Same rule as <see cref="Projections"/>: these stay expression trees so EF
/// composes them into SQL and the server never loads an entity it is only going
/// to reshape. That is why <c>baseUrl</c> and <c>today</c> arrive as parameters
/// rather than being read from ambient state — a captured constant translates,
/// a method call on <c>HttpContext</c> does not.
/// </remarks>
public static class MobileProjections
{
    /// <summary>
    /// How far along a job is, as a percentage, derived from its status.
    /// </summary>
    /// <remarks>
    /// Lives here so the app's progress bar and any future web view agree.
    /// "Awaiting Parts" deliberately reads *lower* than "In Progress": from the
    /// customer's side the car is sitting still, and showing it as further along
    /// than active work would be a lie.
    /// </remarks>
    public static int ProgressFor(string status) => status switch
    {
        "Open" => 10,
        "Awaiting Parts" => 30,
        "In Progress" => 55,
        "Completed" => 90,
        "Delivered" => 100,
        _ => 0,
    };

    public static IQueryable<JobPhotoDto> ToDto(this IQueryable<JobPhoto> source, string baseUrl) =>
        source.Select(p => new JobPhotoDto
        {
            Id = p.Id,
            JobCardId = p.JobCardId,
            Url = baseUrl + "/" + p.Path,
            FileName = p.FileName,
            SizeBytes = p.SizeBytes,
            Kind = p.Kind,
            Caption = p.Caption,
            UploadedBy = p.UploadedBy,
            UploadedAt = p.UploadedAt,
        });

    public static IQueryable<MechanicJobDto> ToMechanicDto(this IQueryable<JobCard> source, DateOnly today) =>
        source.Select(j => new MechanicJobDto
        {
            Id = j.Id,
            VehicleId = j.VehicleId,
            VehiclePlate = j.Vehicle!.Plate,
            VehicleLabel = j.Vehicle.Make + " " + j.Vehicle.Model + " " + j.Vehicle.Year,
            VehicleType = j.Vehicle.Type,
            CustomerName = j.Vehicle.Customer!.Name,
            CustomerPhone = j.Vehicle.Customer.Phone,
            CustomerAddress = j.Vehicle.Customer.Address,
            CustomerLatitude = j.Vehicle.Customer.Latitude,
            CustomerLongitude = j.Vehicle.Customer.Longitude,
            Complaint = j.Complaint,
            Status = j.Status,
            Priority = j.Priority,
            Odometer = j.Odometer,
            CreatedAt = j.CreatedAt,
            PromisedAt = j.PromisedAt,
            CompletedAt = j.CompletedAt,
            IsOverdue = j.PromisedAt < today
                        && j.Status != "Completed"
                        && j.Status != "Delivered"
                        && j.Status != "Cancelled",
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
            PhotoCount = j.Photos.Count,
        });

    public static IQueryable<CustomerJobDto> ToCustomerDto(this IQueryable<JobCard> source, string baseUrl) =>
        source.Select(j => new CustomerJobDto
        {
            Id = j.Id,
            VehicleId = j.VehicleId,
            VehiclePlate = j.Vehicle!.Plate,
            VehicleLabel = j.Vehicle.Make + " " + j.Vehicle.Model + " " + j.Vehicle.Year,
            Complaint = j.Complaint,
            Status = j.Status,
            CreatedAt = j.CreatedAt,
            PromisedAt = j.PromisedAt,
            CompletedAt = j.CompletedAt,
            Total = j.Lines.Sum(l => l.Qty * l.UnitPrice),
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
            Photos = j.Photos
                .OrderByDescending(p => p.UploadedAt)
                .Select(p => new JobPhotoDto
                {
                    Id = p.Id,
                    JobCardId = p.JobCardId,
                    Url = baseUrl + "/" + p.Path,
                    FileName = p.FileName,
                    SizeBytes = p.SizeBytes,
                    Kind = p.Kind,
                    Caption = p.Caption,
                    UploadedBy = p.UploadedBy,
                    UploadedAt = p.UploadedAt,
                })
                .ToList(),
            // Translated by EF into a CASE expression, so the list can be sorted
            // and filtered on progress in SQL if that is ever wanted.
            ProgressPct =
                j.Status == "Open" ? 10 :
                j.Status == "Awaiting Parts" ? 30 :
                j.Status == "In Progress" ? 55 :
                j.Status == "Completed" ? 90 :
                j.Status == "Delivered" ? 100 : 0,
        });

    public static IQueryable<BookingDto> ToDto(this IQueryable<Booking> source) =>
        source.Select(b => new BookingDto
        {
            Id = b.Id,
            CustomerId = b.CustomerId,
            CustomerName = b.Customer!.Name,
            VehicleId = b.VehicleId,
            VehiclePlate = b.Vehicle!.Plate,
            VehicleLabel = b.Vehicle.Make + " " + b.Vehicle.Model + " " + b.Vehicle.Year,
            Complaint = b.Complaint,
            PreferredDate = b.PreferredDate,
            PreferredTime = b.PreferredTime,
            // The name and category come from the catalogue, the price from the
            // booking — so a renamed service reads correctly while the number the
            // customer was shown stays put.
            Services = b.Services
                .OrderBy(s => s.Id)
                .Select(s => new BookedServiceDto
                {
                    ServiceId = s.ServiceId,
                    Name = s.Service!.Name,
                    Category = s.Service.Category,
                    Price = s.QuotedPrice,
                })
                .ToList(),
            EstimatedTotal = b.Services.Sum(s => s.QuotedPrice),
            Status = b.Status,
            StaffNote = b.StaffNote,
            JobCardId = b.JobCardId,
            CreatedAt = b.CreatedAt,
            RespondedAt = b.RespondedAt,
        });

    public static IQueryable<NotificationDto> ToDto(this IQueryable<Notification> source) =>
        source.Select(n => new NotificationDto
        {
            Id = n.Id,
            Title = n.Title,
            Body = n.Body,
            Kind = n.Kind,
            EntityId = n.EntityId,
            CreatedAt = n.CreatedAt,
            ReadAt = n.ReadAt,
            IsRead = n.ReadAt != null,
        });

    public static IQueryable<UserDto> ToDto(this IQueryable<User> source) =>
        source.Select(u => new UserDto
        {
            Id = u.Id,
            Email = u.Email,
            Name = u.FullName,
            Role = u.Role,
            Phone = u.Phone,
            IsActive = u.IsActive,
            MechanicName = u.MechanicName,
            CustomerId = u.CustomerId,
            CustomerName = u.Customer != null ? u.Customer.Name : null,
            CreatedAt = u.CreatedAt,
            LastLoginAt = u.LastLoginAt,
        });
}
