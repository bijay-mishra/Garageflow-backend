using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Data;

/// <summary>
/// The database, scoped to one company.
/// </summary>
/// <remarks>
/// Every entity implementing <see cref="ITenantOwned"/> carries a global query
/// filter on the current company, and gets that company stamped on insert.
/// Both are applied here rather than in controllers on purpose: manual
/// filtering is correct until somebody adds a query and forgets, and this
/// codebase has already shipped that exact bug once.
///
/// A filter can be lifted with <c>IgnoreQueryFilters()</c>, which is what the
/// superadmin endpoints use to look across companies. Grep for it to find
/// every place that deliberately crosses the boundary.
/// </remarks>
public class GarageFlowDbContext(
    DbContextOptions<GarageFlowDbContext> options,
    TenantContext tenant) : DbContext(options)
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

    public DbSet<SupportThread> SupportThreads => Set<SupportThread>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<BookingService> BookingServices => Set<BookingService>();
    public DbSet<Workshop> Workshops => Set<Workshop>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryPoint> DeliveryPoints => Set<DeliveryPoint>();
    public DbSet<CustomerRegistration> CustomerRegistrations => Set<CustomerRegistration>();
    public DbSet<UserWorkshopLink> UserWorkshopLinks => Set<UserWorkshopLink>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<ImpersonationLog> ImpersonationLogs => Set<ImpersonationLog>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<RoleMenu> RoleMenus => Set<RoleMenu>();
    public DbSet<CompanyRole> CompanyRoles => Set<CompanyRole>();
    public DbSet<FiscalYearRecord> FiscalYearRecords => Set<FiscalYearRecord>();

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
            // Matches CompanyRole.Name, which is what it points at.
            e.Property(x => x.CompanyRoleName).HasMaxLength(40);
            e.Property(x => x.Workshop).HasMaxLength(160);
            // A SHA-256 hex digest — 64 characters, whatever length the code was.
            e.Property(x => x.PasswordResetCodeHash).HasMaxLength(200);
            e.Property(x => x.MechanicName).HasMaxLength(120);
            e.Property(x => x.CustomerId).HasMaxLength(20);
            e.Property(x => x.BranchId).HasMaxLength(20);
            e.Property(x => x.FiscalYear).HasMaxLength(20);
            e.Property(x => x.PhotoPath).HasMaxLength(300);

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

        b.Entity<UserWorkshopLink>(e =>
        {
            e.ToTable("UserWorkshopLinks");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(20).IsRequired();
            e.Property(x => x.CompanyCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.CustomerId).HasMaxLength(20).IsRequired();

            // One membership per person per garage. Joining twice is joining once.
            e.HasIndex(x => new { x.UserId, x.CompanyCode }).IsUnique();

            e.HasOne(x => x.User)
                .WithMany(u => u.WorkshopLinks)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict rather than Cascade: SQL Server refuses a second cascade
            // path into Customers, and deleting a customer record should not
            // silently delete the person's account with another garage.
            e.HasOne(x => x.Customer)
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Workshop>(e =>
        {
            e.ToTable("Workshops");
            e.Property(x => x.About).HasMaxLength(600);
            e.HasIndex(x => x.IsListed);
            e.HasKey(x => x.CompanyCode);
            e.Property(x => x.CompanyCode).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(160);
            e.Property(x => x.LegalName).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Email).HasMaxLength(160);
            e.Property(x => x.TaxNumber).HasMaxLength(40);
            e.Property(x => x.LogoPath).HasMaxLength(300);
            e.Property(x => x.InvoiceFooter).HasMaxLength(500);
            e.Property(x => x.OpeningHours).HasMaxLength(200);
            e.Property(x => x.BankName).HasMaxLength(120);
            e.Property(x => x.BankAccountName).HasMaxLength(160);
            e.Property(x => x.BankAccountNumber).HasMaxLength(60);
            e.Property(x => x.BankBranch).HasMaxLength(120);
            e.Property(x => x.DeliveryBaseFee).HasPrecision(18, 2);
            e.Property(x => x.DeliveryPerKm).HasPrecision(18, 2);
            e.Property(x => x.DeliveryFreeAbove).HasPrecision(18, 2);
            e.Ignore(x => x.HasLocation);
            e.Ignore(x => x.CanDeliver);
        });

        b.Entity<SupportThread>(e =>
        {
            e.ToTable("SupportThreads");
            e.HasKey(x => x.Id);
            e.Property(x => x.Audience).HasMaxLength(20);
            e.Property(x => x.OpenedByUserId).HasMaxLength(20).IsRequired();
            e.Property(x => x.CustomerId).HasMaxLength(20);
            e.Property(x => x.Subject).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(20);

            // The inbox is "this audience, newest first", and a customer's own
            // list is "my threads, newest first" — one index serves both.
            e.HasIndex(x => new { x.Audience, x.LastMessageAt });
            e.HasIndex(x => x.CustomerId);

            // Deleting a thread takes its messages. There is nothing a message
            // means without the conversation it sat in.
            e.HasMany(x => x.Messages)
                .WithOne(x => x.Thread!)
                .HasForeignKey(x => x.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SupportMessage>(e =>
        {
            e.ToTable("SupportMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Sender).HasMaxLength(20);
            e.Property(x => x.SenderUserId).HasMaxLength(20);
            e.Property(x => x.SenderName).HasMaxLength(160);

            // Long enough for somebody describing a fault in their own words,
            // and for an AI answer that walks them through a setting.
            e.Property(x => x.Body).HasMaxLength(4000);
            e.Property(x => x.Source).HasMaxLength(10);

            e.HasIndex(x => new { x.ThreadId, x.CreatedAt });
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

        b.Entity<ImpersonationLog>(e =>
        {
            e.ToTable("ImpersonationLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(20).IsRequired();
            e.Property(x => x.UserEmail).HasMaxLength(160);
            e.Property(x => x.CompanyCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(300);

            // Read as "who has been in this company, most recent first".
            e.HasIndex(x => new { x.CompanyCode, x.At });
        });

        b.Entity<Branch>(e =>
        {
            e.ToTable("Branches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(20);
            e.Property(x => x.CompanyCode).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.Phone).HasMaxLength(40);

            // Always read as "the selectable branches for this tenant".
            e.HasIndex(x => new { x.CompanyCode, x.IsActive });
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

        b.Entity<FiscalYearRecord>(e =>
        {
            e.ToTable("FiscalYears");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();

            // One row per code per company. Two years both called 2082/83 would
            // make "which books am I looking at?" unanswerable.
            e.HasIndex(x => new { x.CompanyCode, x.Code }).IsUnique();
        });

        b.Entity<MenuItem>(e =>
        {
            e.ToTable("MenuItems");
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(40).IsRequired();
            e.Property(x => x.Label).HasMaxLength(80);
            e.Property(x => x.LabelNe).HasMaxLength(80);
            e.Property(x => x.Route).HasMaxLength(120);
            e.Property(x => x.Icon).HasMaxLength(60);
            e.Property(x => x.ParentKey).HasMaxLength(40);
            e.Property(x => x.Module).HasMaxLength(40);

            // The key is the identity everything else points at, so a duplicate
            // would make "which row does this role mean?" unanswerable.
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<RoleMenu>(e =>
        {
            e.ToTable("RoleMenus");
            e.HasKey(x => x.Id);
            // Wide enough for a company's own role name, not just the four
            // built-in ones — this column holds CompanyRole.Name.
            e.Property(x => x.Role).HasMaxLength(40).IsRequired();
            e.Property(x => x.MenuKey).HasMaxLength(40).IsRequired();

            // One decision per company, per role, per row. Without this a save
            // that raced itself would leave two rows disagreeing, and which one
            // won would depend on read order.
            e.HasIndex(x => new { x.CompanyCode, x.Role, x.MenuKey }).IsUnique();
        });

        b.Entity<CompanyRole>(e =>
        {
            e.ToTable("CompanyRoles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(40).IsRequired();
            e.Property(x => x.BaseRole).HasMaxLength(20).IsRequired();
            e.Property(x => x.Description).HasMaxLength(200);

            // The name is what accounts and menu rows point at, so two roles
            // sharing one inside a company would make "which menu does this
            // person get?" unanswerable.
            e.HasIndex(x => new { x.CompanyCode, x.Name }).IsUnique();
        });

        // ── Tenancy ──────────────────────────────────────────────────────────
        // Every entity implementing ITenantOwned gets the column, an index and
        // a filter on the current company. Driven by reflection so a new
        // tenant-owned table is covered the moment it is declared.
        foreach (var entity in b.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entity.ClrType)) continue;

            b.Entity(entity.ClrType)
                .Property(nameof(ITenantOwned.CompanyCode))
                .HasMaxLength(40)
                .IsRequired();

            // Nearly every query filters on it, so it earns an index.
            b.Entity(entity.ClrType).HasIndex(nameof(ITenantOwned.CompanyCode));

            ApplyTenantFilterMethod
                .MakeGenericMethod(entity.ClrType)
                .Invoke(this, [b]);
        }
    }

    private static readonly System.Reflection.MethodInfo ApplyTenantFilterMethod =
        typeof(GarageFlowDbContext).GetMethod(
            nameof(ApplyTenantFilter),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    /// <summary>Filters one entity type to the current company.</summary>
    /// <remarks>
    /// A real lambda, not a hand-built expression tree, and that distinction is
    /// load-bearing. An earlier version used
    /// <c>Expression.Constant(tenant)</c>, which baked one TenantContext into
    /// the model — and because EF caches the model per context type, every
    /// later request reused the first request's company. Two companies saw each
    /// other's data and the filter looked correct in the source.
    ///
    /// Closing over <c>tenant</c> instead makes it a field on this context, which
    /// EF re-reads on every query.
    /// </remarks>
    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantOwned =>
        b.Entity<T>().HasQueryFilter(
            e => tenant.CompanyCode == null || e.CompanyCode == tenant.CompanyCode);
    /// <summary>Stamps the company on anything new before it is written.</summary>
    /// <remarks>
    /// Central for the same reason as the filter: a controller that forgets to
    /// set it would write a row belonging to nobody, invisible to every company
    /// including the one that created it.
    /// </remarks>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State != EntityState.Added) continue;

            // An explicit value wins. The seeder sets one directly, and the
            // superadmin creating a company writes into a tenant it is not
            // itself bound to.
            if (!string.IsNullOrEmpty(entry.Entity.CompanyCode)) continue;

            entry.Entity.CompanyCode = tenant.CompanyCode
                ?? throw new InvalidOperationException(
                    $"Cannot save {entry.Entity.GetType().Name} with no company. " +
                    "Either the request has no tenant or the row needs one set explicitly.");
        }
    }
}
