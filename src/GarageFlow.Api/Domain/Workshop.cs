namespace GarageFlow.Api.Domain;

/// <summary>
/// Who the workshop is and where it is. One row per tenant, keyed by the same
/// company code people type at sign-in.
/// </summary>
/// <remarks>
/// These values were constants in the dashboard's <c>src/data/seed.ts</c>,
/// compiled into the browser bundle. That was fine while there was one shop and
/// impossible the moment a second one signed up — no amount of configuration in
/// their browser could change a name baked into the JavaScript.
///
/// Deliberately narrow: identity, contact, tax number, and a map pin. Currency
/// and VAT rate are *not* here — they are still the hardcoded Nepali values, and
/// moving them is the multi-country job that has been put off on purpose.
/// </remarks>
public class Workshop
{
    /// <summary>The tenant. Matches <c>User.CompanyCode</c>.</summary>
    public string CompanyCode { get; set; } = default!;

    /// <summary>Trading name — what customers call the place.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Registered name, which is what a tax invoice has to carry. Often differs
    /// from the trading name: "Valley Auto Care Pvt. Ltd." trading as
    /// "GarageFlow".
    /// </summary>
    public string LegalName { get; set; } = "";

    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";

    /// <summary>PAN — the registration number printed on every bill.</summary>
    public string TaxNumber { get; set; } = "";

    /// <summary>
    /// Whether this garage appears in the public directory customers browse.
    /// </summary>
    /// <remarks>
    /// Opt-in, and off by default for a reason: a workshop that bought this to
    /// run its own books has not agreed to be listed anywhere, and quietly
    /// publishing its name and address the day the directory ships would be a
    /// decision made on its behalf. The Workshop screen turns it on.
    /// </remarks>
    public bool IsListed { get; set; }

    /// <summary>A sentence or two shown on the garage's public card.</summary>
    public string About { get; set; } = "";

    /// <summary>
    /// Where the workshop is, so a customer can get directions from the app.
    /// Null until somebody drops a pin.
    /// </summary>
    /// <remarks>
    /// The single most useful thing on this record for a customer: an address is
    /// what you write down, a pin is what you drive to. Same reasoning as
    /// <see cref="Customer.Latitude"/>, and the app renders both the same way.
    /// </remarks>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Printed under the totals — payment terms, a thank-you, a warranty note.</summary>
    public string InvoiceFooter { get; set; } = "";

    /// <summary>Opening hours as free text — "Sun–Fri 9:00–18:00, Sat closed".</summary>
    /// <remarks>
    /// Free text rather than a structured schedule on purpose. A workshop's real
    /// hours have exceptions no data model survives contact with, and this is
    /// shown to a customer, not used to compute anything.
    /// </remarks>
    public string OpeningHours { get; set; } = "";

    // ── Subscription ─────────────────────────────────────────────────────

    /// <summary>
    /// Modules this company may use, comma separated.
    /// </summary>
    /// <remarks>
    /// Set by the superadmin and read by the dashboard to decide which menu
    /// entries exist. It lives here rather than in the browser because the
    /// previous version was a localStorage value the customer could edit to
    /// unlock anything — a gate anyone can open is decoration.
    /// </remarks>
    public string EnabledModules { get; set; } = "";

    /// <summary>
    /// False suspends the company: nobody there can sign in.
    /// </summary>
    /// <remarks>
    /// Their data is untouched. Suspension is for non-payment or a dispute,
    /// and both are situations you want to be able to undo.
    /// </remarks>
    public bool IsActive { get; set; } = true;

    /// <summary>When the superadmin created it.</summary>
    public DateTime CreatedAt { get; set; }

    // ── Bank transfer ────────────────────────────────────────────────────────
    // Shown to a customer who wants to pay by transfer rather than a wallet.
    //
    // Not a gateway and deliberately not pretending to be one: the app displays
    // these details, the customer moves the money in their own banking app, and
    // a staff member confirms it against the statement. There is no API in that
    // loop and no fee, which is exactly why plenty of Nepali workshops prefer
    // it for larger bills.
    //
    // Blank means the workshop has not set it up, and the app hides the option
    // rather than showing an empty account number.

    public string BankName { get; set; } = "";

    /// <summary>The name on the account — what the payer must match.</summary>
    public string BankAccountName { get; set; } = "";

    public string BankAccountNumber { get; set; } = "";

    /// <summary>Optional. Some banks want it, most transfers do not.</summary>
    public string BankBranch { get; set; } = "";

    /// <summary>True once there is enough here to actually pay into.</summary>
    public bool CanBankTransfer =>
        !string.IsNullOrWhiteSpace(BankName) &&
        !string.IsNullOrWhiteSpace(BankAccountNumber);

    // ── Home delivery ────────────────────────────────────────────────────────
    // What the shop charges to bring a finished vehicle back. Priced from the
    // distance between this workshop's pin and the customer's, so a shop that
    // has not set its own pin cannot offer delivery at all — which is correct,
    // and the reason DeliveryEnabled is not the only gate.

    /// <summary>Whether customers are offered home delivery when a job is done.</summary>
    public bool DeliveryEnabled { get; set; } = true;

    /// <summary>Charged on every delivery before distance is counted.</summary>
    public decimal DeliveryBaseFee { get; set; } = 50m;

    /// <summary>Added per kilometre of straight-line distance.</summary>
    public decimal DeliveryPerKm { get; set; } = 20m;

    /// <summary>
    /// Bills at or above this are delivered free. Zero disables the waiver.
    /// </summary>
    /// <remarks>
    /// The one setting here that is marketing rather than cost: a customer who
    /// has just spent twenty thousand rupees does not want to be charged another
    /// hundred to get their own car back.
    /// </remarks>
    public decimal DeliveryFreeAbove { get; set; } = 5000m;

    /// <summary>Beyond this many kilometres the shop will not deliver. Zero means no limit.</summary>
    public double DeliveryMaxKm { get; set; } = 15;

    public DateTime UpdatedAt { get; set; }

    /// <summary>True when there is a pin to show on a map.</summary>
    public bool HasLocation => Latitude is not null && Longitude is not null;

    /// <summary>
    /// Delivery can only be offered from a workshop that knows where it is.
    /// </summary>
    public bool CanDeliver => DeliveryEnabled && HasLocation;

    /// <summary>
    /// What delivering to a point <paramref name="distanceKm"/> away costs on a
    /// bill of <paramref name="billTotal"/>.
    /// </summary>
    /// <remarks>
    /// Rounded to whole rupees. A delivery fee of Rs 172.43 invites an argument
    /// at the door for no gain.
    /// </remarks>
    public decimal QuoteDelivery(double distanceKm, decimal billTotal)
    {
        if (DeliveryFreeAbove > 0 && billTotal >= DeliveryFreeAbove) return 0m;

        var fee = DeliveryBaseFee + (DeliveryPerKm * (decimal)distanceKm);

        return Math.Round(fee, 0, MidpointRounding.AwayFromZero);
    }
}

/// <summary>Distance between two points on the globe.</summary>
public static class Geo
{
    private const double EarthRadiusKm = 6371.0088;

    /// <summary>
    /// Great-circle distance in kilometres.
    /// </summary>
    /// <remarks>
    /// Haversine. Not road distance — a car cannot fly from Kalanki to
    /// Baneshwor — so this always under-reads against what the driver actually
    /// covers, which errs in the customer's favour on a fee. Road routing needs
    /// a paid routing API and is not worth the dependency for this.
    /// </remarks>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;

        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
