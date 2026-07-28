using GarageFlow.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Data;

/// <summary>
/// Seeds the demo workshop. The rows are a straight port of the dashboard's
/// in-browser mock store (<c>src/data/seed.ts</c>) so the UI shows exactly the
/// same numbers once it is pointed at this API.
/// </summary>
/// <remarks>
/// Dates are fixed, anchored on late July 2026, rather than relative to today —
/// that is what keeps the figures identical to the mock. To start clean instead,
/// drop the database and let the app recreate it:
/// <c>dotnet ef database drop -f</c>.
/// </remarks>
public static class DbSeeder
{
    public static async Task SeedAsync(GarageFlowDbContext db, CancellationToken ct = default)
    {
        // Only seeds an empty database — safe to call on every startup.
        if (await db.Customers.AnyAsync(ct)) return;

        db.Customers.AddRange(Customers());
        db.Vehicles.AddRange(Vehicles());
        db.JobCards.AddRange(JobCards());
        db.Invoices.AddRange(Invoices());
        db.Activities.AddRange(Activities());

        await db.SaveChangesAsync(ct);
    }

    // ── Default sign-in ──────────────────────────────────────────────────────
    // The credentials the login screen is prefilled with. Fine for a local demo
    // and unacceptable anywhere else — change the password on first sign-in, or
    // delete the row and create your own account.

    public const string DemoCompanyCode = "DEMO";
    public const string DemoEmail = "bijaymishra276@gmail.com";
    public const string DemoPassword = "demo1234";

    /// <summary>Seeds the demo owner account if no users exist yet.</summary>
    public static async Task SeedUsersAsync(
        GarageFlowDbContext db, IPasswordHasher<User> passwordHasher, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct)) return;

        var user = new User
        {
            Id = "USR-001",
            CompanyCode = DemoCompanyCode,
            Email = DemoEmail,
            FullName = "Bijay Mishra",
            Phone = "+977 9801234567",
            Role = "Owner",
            Workshop = "GarageFlow HQ",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            PasswordHash = string.Empty,
        };

        // Hashing needs the user instance, so the hash is set after construction.
        user.PasswordHash = passwordHasher.HashPassword(user, DemoPassword);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
    }

    private static Customer[] Customers() =>
    [
        New("CUS-001", "Ramesh Shrestha", "+977 9841012345", "ramesh.s@gmail.com", "Baneshwor, Kathmandu", "2025-03-11", "bg-brand-500"),
        New("CUS-002", "Sita Gurung", "+977 9802233445", "sita.gurung@outlook.com", "Lakeside, Pokhara", "2025-05-02", "bg-accent-500"),
        New("CUS-003", "Bikash Tamang", "+977 9851122334", "bikash.tmg@gmail.com", "Patan, Lalitpur", "2024-11-20", "bg-emerald-500"),
        New("CUS-004", "Anjali Rai", "+977 9818899776", "anjali.rai@gmail.com", "Dharan, Sunsari", "2026-01-14", "bg-rose-500"),
        New("CUS-005", "Deepak Karki", "+977 9843344556", "dkarki@yahoo.com", "Butwal, Rupandehi", "2025-08-09", "bg-violet-500"),
        New("CUS-006", "Nabin Maharjan", "+977 9860011223", "nabin.mhj@gmail.com", "Kirtipur, Kathmandu", "2025-06-30", "bg-cyan-500"),
        New("CUS-007", "Puja Thapa", "+977 9812344321", "puja.thapa@gmail.com", "Bhaktapur", "2026-04-18", "bg-orange-500"),
        New("CUS-008", "Hari Bahadur K.C.", "+977 9857766554", "haribdr.kc@gmail.com", "Hetauda, Makwanpur", "2025-09-25", "bg-teal-500"),
    ];

    private static Customer New(string id, string name, string phone, string email, string address, string createdAt, string color) =>
        new()
        {
            Id = id, Name = name, Phone = phone, Email = email, Address = address,
            CreatedAt = DateOnly.Parse(createdAt), AvatarColor = color,
        };

    private static Vehicle[] Vehicles() =>
    [
        New("VEH-001", "CUS-001", "Toyota", "Corolla", 2019, "BA 12 PA 3456", "JTDBR32E930012345", "Petrol", 68200, "Silver"),
        New("VEH-002", "CUS-001", "Honda", "CR-V", 2021, "BA 45 CHA 7788", "2HKRW2H85MH000111", "Petrol", 32100, "White"),
        New("VEH-003", "CUS-002", "Hyundai", "i20", 2020, "GA 3 PA 1122", "MALBB51BLEM000222", "Petrol", 45600, "Red"),
        New("VEH-004", "CUS-003", "Ford", "EcoSport", 2018, "BA 21 CHA 9090", "MAJ6S2RL3JH000333", "Diesel", 89400, "Blue"),
        New("VEH-005", "CUS-003", "Tata", "Nexon EV", 2023, "BA 90 PA 5544", "MAT625487PNA00444", "Electric", 18700, "Teal"),
        New("VEH-006", "CUS-004", "Suzuki", "Swift", 2017, "KO 5 PA 2323", "MA3EJKD1S00000555", "Petrol", 102300, "Grey"),
        New("VEH-007", "CUS-005", "Mahindra", "Scorpio", 2016, "LU 2 CHA 4545", "MA1TA2MHK00000666", "Diesel", 134500, "Black"),
        New("VEH-008", "CUS-006", "Kia", "Sonet", 2022, "BA 78 PA 8080", "MZBSSS1ULLA000777", "Petrol", 27800, "White"),
        New("VEH-009", "CUS-006", "Bajaj", "Pulsar 220", 2020, "BA 55 PA 1010", "MD2A36FZ0LWX00888", "Petrol", 41200, "Red", "Bike"),
        New("VEH-010", "CUS-007", "Toyota", "Yaris", 2019, "BP 1 PA 6767", "MBJB29BT200000999", "Petrol", 57300, "Silver"),
        New("VEH-011", "CUS-008", "Hyundai", "Creta", 2021, "NA 4 PA 3131", "MALC281CLMM01010", "Diesel", 61900, "Grey"),
        New("VEH-012", "CUS-002", "Honda", "Activa 6G", 2021, "GA 6 PA 2020", "ME4JF505KMT01111", "Petrol", 22400, "Blue", "Bike"),
    ];

    private static Vehicle New(string id, string customerId, string make, string model, int year,
        string plate, string vin, string fuel, int odometer, string color, string type = "Car") =>
        new()
        {
            Id = id, CustomerId = customerId, Make = make, Model = model, Year = year,
            Plate = plate, Vin = vin, Fuel = fuel, Odometer = odometer, Color = color, Type = type,
        };

    private static JobCard[] JobCards() =>
    [
        New("JOB-1042", "VEH-002", "Periodic service + brake noise from front left", "In Progress", "Normal",
            "Suresh Lama", 32100, "2026-07-24", "2026-07-27", null,
            [
                Line("Full synthetic engine oil (4L)", 1, 4200, "part"),
                Line("Oil filter", 1, 850, "part"),
                Line("Front brake pads", 1, 3600, "part"),
                Line("Labour — service + brakes", 3, 1200, "labour"),
            ]),
        New("JOB-1041", "VEH-005", "Battery health check + software update", "Awaiting Parts", "High",
            "Kiran Adhikari", 18700, "2026-07-23", "2026-07-28", null,
            [
                Line("Cabin air filter", 1, 1400, "part"),
                Line("Diagnostics — HV battery", 2, 1500, "labour"),
            ]),
        New("JOB-1040", "VEH-008", "AC not cooling", "Open", "Urgent",
            "Suresh Lama", 27800, "2026-07-26", "2026-07-27", null,
            [Line("AC diagnostics", 1, 1500, "labour")]),
        New("JOB-1039", "VEH-011", "Clutch slipping under load", "Completed", "High",
            "Kiran Adhikari", 61900, "2026-07-20", "2026-07-24", "2026-07-24",
            [
                Line("Clutch kit (plate + cover + bearing)", 1, 18500, "part"),
                Line("Clutch fluid", 1, 650, "part"),
                Line("Labour — clutch replacement", 6, 1200, "labour"),
            ]),
        New("JOB-1038", "VEH-001", "60,000 km major service", "Delivered", "Normal",
            "Suresh Lama", 68200, "2026-05-12", "2026-05-14", "2026-05-14",
            [
                Line("Engine oil (4L)", 1, 3600, "part"),
                Line("Air + cabin filters", 2, 1200, "part"),
                Line("Spark plugs (set)", 1, 2800, "part"),
                Line("Labour — major service", 4, 1200, "labour"),
            ]),
        New("JOB-1037", "VEH-007", "Suspension knocking + wheel alignment", "Delivered", "Normal",
            "Ramesh Bhandari", 134500, "2026-06-03", "2026-06-05", "2026-06-05",
            [
                Line("Front shock absorbers (pair)", 1, 9800, "part"),
                Line("Wheel alignment + balancing", 1, 1800, "labour"),
                Line("Labour — suspension", 3, 1200, "labour"),
            ]),
        New("JOB-1036", "VEH-003", "Regular service + wiper replacement", "Delivered", "Low",
            "Kiran Adhikari", 45600, "2026-06-18", "2026-06-20", "2026-06-20",
            [
                Line("Engine oil (3.5L)", 1, 3200, "part"),
                Line("Wiper blades (pair)", 1, 1100, "part"),
                Line("Labour — service", 2, 1200, "labour"),
            ]),
        New("JOB-1035", "VEH-006", "Overheating on highway", "Delivered", "High",
            "Ramesh Bhandari", 102300, "2026-03-26", "2026-03-28", "2026-03-28",
            [
                Line("Radiator coolant flush", 1, 1800, "labour"),
                Line("Thermostat", 1, 2400, "part"),
                Line("Labour — cooling system", 2, 1200, "labour"),
            ]),
        New("JOB-1034", "VEH-009", "Chain sprocket worn, engine tuning", "Cancelled", "Low",
            "Suresh Lama", 41200, "2026-05-28", "2026-05-30", null,
            [Line("Chain sprocket kit", 1, 2600, "part")]),
        New("JOB-1033", "VEH-004", "DPF warning + diesel service", "Completed", "Normal",
            "Kiran Adhikari", 89400, "2026-07-22", "2026-07-26", "2026-07-25",
            [
                Line("Diesel engine oil (5L)", 1, 5200, "part"),
                Line("Fuel filter", 1, 2100, "part"),
                Line("DPF regeneration", 2, 1500, "labour"),
            ]),
    ];

    private static JobCard New(string id, string vehicleId, string complaint, string status, string priority,
        string mechanic, int odometer, string createdAt, string promisedAt, string? completedAt, List<JobLine> lines)
    {
        for (var i = 0; i < lines.Count; i++) lines[i].SortOrder = i;

        return new JobCard
        {
            Id = id, VehicleId = vehicleId, Complaint = complaint, Status = status, Priority = priority,
            Mechanic = mechanic, Odometer = odometer,
            CreatedAt = DateOnly.Parse(createdAt),
            PromisedAt = DateOnly.Parse(promisedAt),
            CompletedAt = completedAt is null ? null : DateOnly.Parse(completedAt),
            Lines = lines,
        };
    }

    private static JobLine Line(string description, decimal qty, decimal unitPrice, string kind) =>
        new() { Description = description, Qty = qty, UnitPrice = unitPrice, Kind = kind };

    private static Invoice[] Invoices() =>
    [
        New("INV-2091", "JOB-1039", "CUS-008", "Hari Bahadur K.C.", "NA 4 PA 3131", "2026-07-24", 26350, 29775.5m, "Bank Transfer"),
        New("INV-2090", "JOB-1038", "CUS-001", "Ramesh Shrestha", "BA 12 PA 3456", "2026-05-14", 13800, 15594, "eSewa"),
        New("INV-2089", "JOB-1037", "CUS-005", "Deepak Karki", "LU 2 CHA 4545", "2026-06-05", 15200, 17176, "Card"),
        New("INV-2088", "JOB-1036", "CUS-002", "Sita Gurung", "GA 3 PA 1122", "2026-06-20", 6700, 7571, "Khalti"),
        New("INV-2087", "JOB-1035", "CUS-004", "Anjali Rai", "KO 5 PA 2323", "2026-03-28", 6600, 7458, "Cash"),
        New("INV-2086", "JOB-1033", "CUS-003", "Bikash Tamang", "BA 21 CHA 9090", "2026-07-25", 10300, 5000, "Cash"),
        New("INV-2085", "JOB-1042", "CUS-001", "Ramesh Shrestha", "BA 45 CHA 7788", "2026-07-26", 12250, 0, null),
    ];

    /// <summary>
    /// Builds an invoice at the standard 13% VAT rate. Anything already settled
    /// is written as a payment dated to the issue date, so the dashboard's
    /// revenue figures — which count payments, not invoices — have something to
    /// add up.
    /// </summary>
    private static Invoice New(string id, string jobCardId, string customerId, string customerName,
        string plate, string issuedAt, decimal subtotal, decimal paid, string? method)
    {
        var issued = DateOnly.Parse(issuedAt);
        var invoice = new Invoice
        {
            Id = id, JobCardId = jobCardId, CustomerId = customerId, CustomerName = customerName,
            VehiclePlate = plate, IssuedAt = issued, Subtotal = subtotal, TaxRate = 0.13m,
            Paid = paid, Method = method,
        };

        if (paid > 0 && method is not null)
        {
            invoice.Payments.Add(new Payment
            {
                Amount = paid,
                Method = method,
                At = issued.ToDateTime(new TimeOnly(11, 0)),
            });
        }

        return invoice;
    }

    private static Activity[] Activities() =>
    [
        New("ACT-1", "2026-07-26T09:14:00", "Job JOB-1040 opened for Kia Sonet (AC not cooling)", "job"),
        New("ACT-2", "2026-07-26T08:40:00", "Invoice INV-2085 raised for Ramesh Shrestha", "invoice"),
        New("ACT-3", "2026-07-25T16:22:00", "JOB-1033 marked Completed by Kiran Adhikari", "job"),
        New("ACT-4", "2026-07-25T14:05:00", "Partial payment Rs 5,000 received on INV-2086", "invoice"),
        New("ACT-5", "2026-07-24T11:30:00", "New customer Puja Thapa added", "customer"),
        New("ACT-6", "2026-07-24T10:10:00", "Vehicle BA 78 PA 8080 checked in", "vehicle"),
    ];

    private static Activity New(string id, string at, string text, string kind) =>
        new() { Id = id, At = DateTime.Parse(at), Text = text, Kind = kind };
}
