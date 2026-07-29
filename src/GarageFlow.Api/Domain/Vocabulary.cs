namespace GarageFlow.Api.Domain;

/// <summary>
/// The closed value sets shared with the frontend. These mirror the TypeScript
/// union types in <c>src/types/index.ts</c> exactly — they are persisted as
/// strings so the database stays readable and the JSON needs no translation.
/// </summary>
public static class Vocabulary
{
    public static readonly string[] FuelTypes = ["Petrol", "Diesel", "Electric", "Hybrid", "CNG"];

    /// <summary>
    /// Body class, used to tell a two-wheeler from a bus at a glance. SUVs and
    /// jeeps are filed under <c>Car</c> — the split that matters in the workshop
    /// is light-vs-heavy, and a separate SUV category only blurs it.
    /// </summary>
    public static readonly string[] VehicleTypes = ["Bike", "Car", "Van", "Bus", "Truck", "Tractor"];

    /// <summary>Display order used by the dashboard status breakdown.</summary>
    public static readonly string[] JobStatuses =
        ["Open", "In Progress", "Awaiting Parts", "Completed", "Delivered", "Cancelled"];

    public static readonly string[] JobPriorities = ["Low", "Normal", "High", "Urgent"];

    /// <summary>
    /// What a line on a job card is. <c>service</c> is a priced item taken from
    /// the workshop's menu — a wash, a polish, an AC regas — as opposed to
    /// <c>labour</c> billed by the hour or a <c>part</c> fitted to the car.
    /// </summary>
    /// <remarks>
    /// Kept separate from <c>labour</c> so the shop can answer "how much did
    /// washing earn us this month?" A wash billed as labour is invisible.
    /// </remarks>
    public static readonly string[] JobLineKinds = ["labour", "part", "service"];

    /// <summary>How the price list is grouped. Drives the filter on the Services screen.</summary>
    public static readonly string[] ServiceCategories =
        ["Washing", "Detailing", "Maintenance", "Repair", "Inspection", "Convenience", "Other"];

    public static readonly string[] InvoiceStatuses = ["Paid", "Partial", "Unpaid", "Refunded"];

    public static readonly string[] PaymentMethods = ["Cash", "Card", "eSewa", "Khalti", "Bank Transfer"];

    /// <summary>
    /// How the money moved, as opposed to which brand carried it.
    /// </summary>
    /// <remarks>
    /// The distinction the shop cares about at close of day: <c>cash</c> is in
    /// the drawer and has to be counted, <c>online</c> and <c>bank</c> are
    /// somebody else's ledger and have to be reconciled. Three buckets survive a
    /// new wallet appearing; a list of brand names does not.
    /// </remarks>
    public static readonly string[] PaymentChannels = ["cash", "online", "bank"];

    /// <summary>
    /// Where a payment attempt stands.
    /// </summary>
    /// <remarks>
    /// Only <c>Completed</c> counts as money. <c>Pending</c> exists because an
    /// online payment is a conversation with a third party that can be abandoned
    /// halfway — the customer opens the wallet page and closes it — and the row
    /// has to exist before that is known, so the callback has something to find.
    /// </remarks>
    public static readonly string[] PaymentStatuses = ["Pending", "Completed", "Failed", "Cancelled"];

    /// <summary>Gateways the customer app can actually pay through.</summary>
    /// <remarks>
    /// Cash, Card and Bank Transfer are absent on purpose: those are recorded by
    /// staff after the fact and have no online flow to run.
    /// </remarks>
    public static readonly string[] OnlineProviders = ["eSewa", "Khalti"];

    /// <summary>
    /// The channel a payment method belongs to. One place, so the dashboard, the
    /// gateway callback and the reports cannot disagree.
    /// </summary>
    public static string ChannelFor(string method) => method switch
    {
        "Cash" => "cash",
        "Bank Transfer" => "bank",
        // Card terminals settle through the bank, not through a wallet.
        "Card" => "bank",
        _ => "online",
    };

    public static readonly string[] ActivityKinds = ["job", "invoice", "customer", "vehicle"];

    /// <summary>
    /// Sign-in roles. Matches the AuthUser role union in the dashboard.
    /// </summary>
    /// <remarks>
    /// The first three are workshop staff and see the dashboard. The last two
    /// sign in on the mobile app instead and each see a slice of one workshop:
    /// a Mechanic sees the jobs assigned to them, a Customer sees only their own
    /// vehicles, jobs and bookings.
    /// </remarks>
    public static readonly string[] UserRoles = ["Owner", "Manager", "Advisor", "Mechanic", "Customer"];

    /// <summary>Roles that may open the web dashboard.</summary>
    public static readonly string[] StaffRoles = ["Owner", "Manager", "Advisor"];

    public const string MechanicRole = "Mechanic";
    public const string CustomerRole = "Customer";

    /// <summary>Where a booking sits in its short life.</summary>
    public static readonly string[] BookingStatuses =
        ["Requested", "Confirmed", "Rejected", "Converted", "Cancelled"];

    /// <summary>What a photo on a job card is showing.</summary>
    public static readonly string[] JobPhotoKinds = ["before", "during", "after", "damage", "part"];

    /// <summary>Drives the icon on a notification row in the app.</summary>
    public static readonly string[] NotificationKinds =
        ["job", "booking", "invoice", "system"];

    // ── Handing the car back ─────────────────────────────────────────────────

    /// <summary>How a finished vehicle gets back to its owner.</summary>
    public static readonly string[] DeliveryMethods = ["Pickup", "HomeDelivery"];

    /// <summary>
    /// Where a handover has got to.
    /// </summary>
    /// <remarks>
    /// <c>AwaitingChoice</c> is the state that makes this worth modelling: the
    /// car is ready, the customer has been told, and nobody knows yet whether
    /// they are coming for it. Everything after that depends on their answer.
    /// </remarks>
    public static readonly string[] DeliveryStatuses =
        ["AwaitingChoice", "Scheduled", "OutForDelivery", "Delivered", "Cancelled"];

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
