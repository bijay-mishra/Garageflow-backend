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

            // One account per email *per tenant* — the same person can belong to
            // two workshops, which is why the key is the pair.
            e.HasIndex(x => new { x.CompanyCode, x.Email }).IsUnique();
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

            e.HasOne(x => x.JobCard)
                .WithMany(j => j.Lines)
                .HasForeignKey(x => x.JobCardId)
                .OnDelete(DeleteBehavior.Cascade);
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
            e.Property(x => x.Amount).HasPrecision(18, 2);

            e.HasOne(x => x.Invoice)
                .WithMany(i => i.Payments)
                .HasForeignKey(x => x.InvoiceId)
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
