using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Data;

public class GarageFlowDbContext(DbContextOptions<GarageFlowDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<JobCard> JobCards => Set<JobCard>();
    public DbSet<JobLine> JobLines => Set<JobLine>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<JobPhoto> JobPhotos => Set<JobPhoto>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<BookingService> BookingServices => Set<BookingService>();
    public DbSet<Workshop> Workshops => Set<Workshop>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryPoint> DeliveryPoints => Set<DeliveryPoint>();
    public DbSet<CustomerRegistration> CustomerRegistrations => Set<CustomerRegistration>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(20);
            e.Property(x => x.CompanyCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.Email).HasMaxLength(160).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(160);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.PasswordHash).HasMaxLength(400).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.Workshop).HasMaxLength(160);
            e.Property(x => x.PasswordResetTokenHash).HasMaxLength(200);
            e.Property(x => x.MechanicName).HasMaxLength(120);
            e.Property(x => x.CustomerId).HasMaxLength(20);

            // One account per email *per tenant* — the same person can belong to
            // two workshops, which is why the key is the pair.
            e.HasIndex(x => new { x.CompanyCode, x.Email }).IsUnique();

            // A mechanic account claims job cards by this name, so it is looked
            // up on every request the mechanic app makes.
            e.HasIndex(x => x.MechanicName);

            // Deleting a customer must not silently delete their login — the
            // account is blocked at sign-in instead, so the row survives with a
            // dangling-free null.
            e.HasOne(x => x.Customer)
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<JobPhoto>(e =>
        {
            e.ToTable("JobPhotos");
            e.HasKey(x => x.Id);
            e.Property(x => x.JobCardId).HasMaxLength(20).IsRequired();
            e.Property(x => x.Path).HasMaxLength(400).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Caption).HasMaxLength(300);
            e.Property(x => x.UploadedBy).HasMaxLength(120);
            e.HasIndex(x => x.JobCardId);

            // Deleting a job card takes its photos. The files on disk are
            // removed by the controller before the rows go.
            e.HasOne(x => x.JobCard)
                .WithMany(j => j.Photos)
                .HasForeignKey(x => x.JobCardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Booking>(e =>
        {
            e.ToTable("Bookings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(20);
            e.Property(x => x.CustomerId).HasMaxLength(20).IsRequired();
            e.Property(x => x.VehicleId).HasMaxLength(20).IsRequired();
            e.Property(x => x.Complaint).HasMaxLength(1000);
            e.Property(x => x.PreferredTime).HasMaxLength(60);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.StaffNote).HasMaxLength(500);
            e.Property(x => x.JobCardId).HasMaxLength(20);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CustomerId);

            e.HasOne(x => x.Customer)
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not Cascade: SQL Server refuses multiple cascade paths
            // to Customers, and deleting a vehicle should not quietly erase the
            // request history behind it.
            e.HasOne(x => x.Vehicle)
                .WithMany()
                .HasForeignKey(x => x.VehicleId)
                .OnDelete(DeleteBehavior.Restrict);

            // The job card a booking became. Losing the job leaves the booking
            // standing with a null link rather than deleting the customer's
            // record of having asked.
            e.HasOne(x => x.JobCard)
                .WithMany()
                .HasForeignKey(x => x.JobCardId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Service>(e =>
        {
            e.ToTable("Services");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Category).HasMaxLength(40);
            e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.VehicleTypes).HasMaxLength(120);

            // Derived from the column, so there is nothing to map.
            e.Ignore(x => x.AppliesTo);

            e.HasIndex(x => x.Category);

            // The price list is short and read on every job card form, so this
            // covers the one query that matters: what can I still sell today?
            e.HasIndex(x => new { x.IsActive, x.IsBookable });
        });

        b.Entity<BookingService>(e =>
        {
            e.ToTable("BookingServices");
            e.HasKey(x => x.Id);
            e.Property(x => x.BookingId).HasMaxLength(20).IsRequired();
            e.Property(x => x.ServiceId).HasMaxLength(20).IsRequired();
            e.Property(x => x.QuotedPrice).HasPrecision(18, 2);

            // Ticking the same box twice is one request for one wash.
            e.HasIndex(x => new { x.BookingId, x.ServiceId }).IsUnique();

            e.HasOne(x => x.Booking)
                .WithMany(bk => bk.Services)
                .HasForeignKey(x => x.BookingId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict: a service someone has actually booked cannot be deleted
            // out from under them. ServicesController offers deactivation
            // instead, which is what retiring a service really means.
            e.HasOne(x => x.Service)
                .WithMany()
                .HasForeignKey(x => x.ServiceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("Notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(20).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(600);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.EntityId).HasMaxLength(20);

            // Every read is "my unread notifications, newest first".
            e.HasIndex(x => new { x.UserId, x.ReadAt });

            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(20).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(200).IsRequired();
            e.Ignore(x => x.IsActive);
            e.HasIndex(x => x.TokenHash);

            e.HasOne(x => x.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Customer>(e =>
        {
            e.ToTable("Customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.AvatarColor).HasMaxLength(40);
            e.Ignore(x => x.HasLocation);
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.Phone);
        });

        b.Entity<Vehicle>(e =>
        {
            e.ToTable("Vehicles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(20);
            e.Property(x => x.CustomerId).HasMaxLength(20).IsRequired();
            e.Property(x => x.Make).HasMaxLength(80);
            e.Property(x => x.Model).HasMaxLength(80);
            e.Property(x => x.Plate).HasMaxLength(40);
            e.Property(x => x.Vin).HasMaxLength(40);
            e.Property(x => x.Type).HasMaxLength(20);
            e.Property(x => x.Fuel).HasMaxLength(20);
            e.Property(x => x.Color).HasMaxLength(40);
            e.Ignore(x => x.Label);
            e.HasIndex(x => x.Plate);

            // Deleting a customer takes their vehicles — and, through the next
            // rule, those vehicles' job cards — with them.
            e.HasOne(x => x.Customer)
                .WithMany(c => c.Vehicles)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JobCard>(e =>
        {
            e.ToTable("JobCards");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(20);
            e.Property(x => x.VehicleId).HasMaxLength(20).IsRequired();
            e.Property(x => x.Complaint).HasMaxLength(1000);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Priority).HasMaxLength(20);
            e.Property(x => x.Mechanic).HasMaxLength(120);
            e.Ignore(x => x.Total);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);

            // Cascading here as well would give SQL Server multiple delete paths
            // into JobCards (customer → vehicle → job), so this leg is manual:
            // VehiclesController deletes the job cards itself.
            e.HasOne(x => x.Vehicle)
                .WithMany(v => v.JobCards)
                .HasForeignKey(x => x.VehicleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<JobLine>(e =>
        {
            e.ToTable("JobLines");
            e.HasKey(x => x.Id);
            e.Property(x => x.JobCardId).HasMaxLength(20).IsRequired();
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Qty).HasPrecision(18, 2);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.Property(x => x.ServiceId).HasMaxLength(20);

            e.HasOne(x => x.JobCard)
                .WithMany(j => j.Lines)
                .HasForeignKey(x => x.JobCardId)
                .OnDelete(DeleteBehavior.Cascade);

            // SetNull, not Cascade: the line is the record of work done and
            // billed. Deleting a catalogue entry drops the link and leaves the
            // description and price exactly as they were charged.
            e.HasOne(x => x.Service)
                .WithMany(s => s.JobLines)
                .HasForeignKey(x => x.ServiceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Invoice>(e =>
        {
            e.ToTable("Invoices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(20);
            e.Property(x => x.JobCardId).HasMaxLength(20).IsRequired();
            e.Property(x => x.CustomerId).HasMaxLength(20).IsRequired();
            e.Property(x => x.CustomerName).HasMaxLength(160);
            e.Property(x => x.VehiclePlate).HasMaxLength(40);
            e.Property(x => x.Method).HasMaxLength(30);
            e.Property(x => x.Subtotal).HasPrecision(18, 2);
            e.Property(x => x.TaxRate).HasPrecision(9, 4);
            e.Property(x => x.Paid).HasPrecision(18, 2);
            e.Ignore(x => x.Tax);
            e.Ignore(x => x.Total);
            e.Ignore(x => x.Status);
            e.HasIndex(x => x.IssuedAt);

            // No FK to JobCards: an invoice must outlive the job it was raised
            // for, so JobCardId is kept as a plain reference.
            e.HasOne(x => x.Customer)
                .WithMany(c => c.Invoices)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("Payments");
            e.HasKey(x => x.Id);
            e.Property(x => x.InvoiceId).HasMaxLength(20).IsRequired();
            e.Property(x => x.Method).HasMaxLength(30);
            e.Property(x => x.Channel).HasMaxLength(10);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Reference).HasMaxLength(64);
            e.Property(x => x.ProviderRef).HasMaxLength(120);
            e.Property(x => x.FailureReason).HasMaxLength(300);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Ignore(x => x.IsSettled);

            // The gateway callback arrives knowing only our reference, and it is
            // the hot path of the whole payment flow.
            e.HasIndex(x => x.Reference).IsUnique().HasFilter("[Reference] IS NOT NULL");

            // "What did we take, by channel, this month" — the report this whole
            // feature exists to answer.
            e.HasIndex(x => new { x.Status, x.Channel });

            e.HasOne(x => x.Invoice)
                .WithMany(i => i.Payments)
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Workshop>(e =>
        {
            e.ToTable("Workshops");
            e.HasKey(x => x.CompanyCode);
            e.Property(x => x.CompanyCode).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(160);
            e.Property(x => x.LegalName).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.TaxNumber).HasMaxLength(40);
            e.Property(x => x.InvoiceFooter).HasMaxLength(500);
            e.Property(x => x.OpeningHours).HasMaxLength(200);
            e.Property(x => x.DeliveryBaseFee).HasPrecision(18, 2);
            e.Property(x => x.DeliveryPerKm).HasPrecision(18, 2);
            e.Property(x => x.DeliveryFreeAbove).HasPrecision(18, 2);
            e.Ignore(x => x.HasLocation);
            e.Ignore(x => x.CanDeliver);
        });

        b.Entity<Delivery>(e =>
        {
            e.ToTable("Deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(20);
            e.Property(x => x.JobCardId).HasMaxLength(20).IsRequired();
            e.Property(x => x.CustomerId).HasMaxLength(20).IsRequired();
            e.Property(x => x.Method).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.Driver).HasMaxLength(120);
            e.Property(x => x.Fee).HasPrecision(18, 2);
            e.Ignore(x => x.IsLive);
            e.Ignore(x => x.HasDriverPosition);

            // "What is out right now" is the query the dashboard map lives on.
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CustomerId);

            // One handover per job. A job that somehow completes twice must not
            // leave the customer choosing between two identical deliveries.
            e.HasIndex(x => x.JobCardId).IsUnique();

            // Cascade: losing the job card takes the handover with it — there is
            // nothing to deliver once the work is gone.
            e.HasOne(x => x.JobCard)
                .WithMany()
                .HasForeignKey(x => x.JobCardId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not Cascade: SQL Server refuses two cascade paths into
            // the same table, and job cards already cascade from customers.
            e.HasOne(x => x.Customer)
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DeliveryPoint>(e =>
        {
            e.ToTable("DeliveryPoints");
            e.HasKey(x => x.Id);
            e.Property(x => x.DeliveryId).HasMaxLength(20).IsRequired();

            // Always read as "this delivery's trail, in order".
            e.HasIndex(x => new { x.DeliveryId, x.At });

            e.HasOne(x => x.Delivery)
                .WithMany(d => d.Trail)
                .HasForeignKey(x => x.DeliveryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CustomerRegistration>(e =>
        {
            e.ToTable("CustomerRegistrations");
            e.HasKey(x => x.Id);
            e.Property(x => x.CustomerId).HasMaxLength(20).IsRequired();
            e.Property(x => x.CompanyCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.Contact).HasMaxLength(160).IsRequired();
            e.Property(x => x.CodeHash).HasMaxLength(100).IsRequired();

            // The second leg looks up by contact within a tenant.
            e.HasIndex(x => new { x.CompanyCode, x.Contact });

            e.HasOne(x => x.Customer)
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Activity>(e =>
        {
            e.ToTable("Activities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.Text).HasMaxLength(500);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.HasIndex(x => x.At);
        });
    }
}
