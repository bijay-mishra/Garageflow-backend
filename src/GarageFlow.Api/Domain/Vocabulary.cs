namespace GarageFlow.Api.Domain;

/// <summary>
/// The closed value sets shared with the frontend. These mirror the TypeScript
/// union types in <c>src/types/index.ts</c> exactly — they are persisted as
/// strings so the database stays readable and the JSON needs no translation.
/// </summary>
public static class Vocabulary
{
    public static readonly string[] FuelTypes = ["Petrol", "Diesel", "Electric", "Hybrid", "CNG"];

    /// <summary>Display order used by the dashboard status breakdown.</summary>
    public static readonly string[] JobStatuses =
        ["Open", "In Progress", "Awaiting Parts", "Completed", "Delivered", "Cancelled"];

    public static readonly string[] JobPriorities = ["Low", "Normal", "High", "Urgent"];

    public static readonly string[] JobLineKinds = ["labour", "part"];

    public static readonly string[] InvoiceStatuses = ["Paid", "Partial", "Unpaid", "Refunded"];

    public static readonly string[] PaymentMethods = ["Cash", "Card", "eSewa", "Khalti", "Bank Transfer"];

    public static readonly string[] ActivityKinds = ["job", "invoice", "customer", "vehicle"];

    /// <summary>Sign-in roles. Matches the AuthUser role union in the dashboard.</summary>
    public static readonly string[] UserRoles = ["Owner", "Manager", "Advisor"];

    /// <summary>Statuses that mean the car is still occupying a bay.</summary>
    public static readonly string[] OpenJobStatuses = ["Open", "In Progress", "Awaiting Parts"];

    /// <summary>Statuses that mean the work is finished.</summary>
    public static readonly string[] DoneJobStatuses = ["Completed", "Delivered"];

    /// <summary>Avatar swatches, kept in step with <c>src/data/seed.ts</c>.</summary>
    public static readonly string[] AvatarColors =
    [
        "bg-brand-500", "bg-accent-500", "bg-emerald-500", "bg-rose-500",
        "bg-violet-500", "bg-cyan-500", "bg-orange-500", "bg-teal-500",
    ];
}
