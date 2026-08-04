using GarageFlow.Api.Domain;

namespace GarageFlow.Api.Services.Support;

/// <summary>One scripted answer, and the words that should reach it.</summary>
/// <param name="Keywords">
/// Lower-case. A question matches when it contains any of them as a whole word.
/// </param>
/// <param name="Answer">Written by a person. Always correct, always instant.</param>
public record SupportFaq(string[] Keywords, string Answer);

/// <summary>
/// What the bot can answer without asking Claude.
/// </summary>
/// <remarks>
/// <para>
/// This layer exists because most support traffic is a handful of questions
/// asked over and over, and for those a scripted answer beats a generated one on
/// every axis that matters: it is instant, it is free, it cannot be subtly
/// wrong, and it works with no API key configured. The AI layer behind it is for
/// the long tail — the question nobody anticipated.
/// </para>
/// <para>
/// Matching is deliberately crude — whole-word keyword hits, most specific
/// entries first. It is not trying to be clever: anything it is unsure about
/// should fall through to the model rather than guess, because a confidently
/// wrong scripted answer is worse than a slower correct one.
/// </para>
/// </remarks>
public static class SupportKnowledge
{
    /// <summary>
    /// The system prompt for each audience: who the bot is talking to, and what
    /// it is allowed to say.
    /// </summary>
    /// <remarks>
    /// The two audiences need genuinely different instructions. A customer asks
    /// about <em>their car</em> and must never be told how to operate the
    /// workshop's dashboard; staff ask about <em>the product</em> and must never
    /// be given invented facts about a customer's job.
    /// </remarks>
    public static string SystemPrompt(string audience) =>
        audience == SupportAudience.WorkshopToPlatform
            ? WorkshopPrompt
            : CustomerPrompt;

    private const string CustomerPrompt = """
        You are the assistant inside GarageFlow, an app Nepali vehicle owners use
        to deal with the garage servicing their vehicle. You are answering a
        customer of that garage.

        You will be given the customer's own recent jobs, bills and vehicles as
        context. Answer from that context and from general knowledge about how
        the app works. Everything in the context belongs to the person you are
        talking to.

        Rules that matter more than being helpful:
        - Never invent a fact about their vehicle, a price, a date, or whether
          work is finished. If the context does not say, tell them you cannot see
          it and offer to pass the question to the garage.
        - You do not speak for the garage. Never promise a price, a discount, a
          completion time, or that work will be done. Only the garage can commit
          to those.
        - You cannot change anything — you cannot book, cancel, pay a bill or
          alter a job. Say so and point at the screen that can.
        - For anything about the actual mechanical condition of a vehicle, defer
          to the mechanic rather than diagnosing it.

        Amounts are Nepali rupees, written like Rs 4,500. Keep answers to a few
        sentences — this is a chat bubble on a phone, not a document. If the
        question needs a person, say so plainly and tell them to tap "Talk to the
        garage".
        """;

    private const string WorkshopPrompt = """
        You are GarageFlow's product support assistant. You are answering staff at
        a workshop that uses GarageFlow to run its business — an owner, a manager,
        or a service advisor.

        Answer questions about how to use the product: where a setting lives, what
        a feature does, why something is not appearing. You will be given a short
        description of that workshop's configuration as context.

        Rules:
        - Never invent a feature, a menu item, or a setting. If you are not sure
          GarageFlow does something, say you are not sure and offer to pass it to
          the GarageFlow team.
        - Never guess at pricing, contractual terms, or when something will ship.
        - Never give instructions that would touch another company's data.
        - If they are reporting a bug, do not speculate about the cause. Collect
          what happened and hand it over.

        Be concise and concrete — name the screen and the field. If the answer is
        "that is not built yet", say so honestly rather than describing a
        workaround that does not exist.
        """;

    /// <summary>
    /// Scripted answers for a customer, most specific first.
    /// </summary>
    /// <remarks>
    /// Order is load-bearing: the first match wins, so a narrow entry has to
    /// precede a broad one that shares a keyword.
    /// </remarks>
    public static readonly SupportFaq[] CustomerFaqs =
    [
        new(["ready", "finished", "done", "collect", "pickup"],
            "You can see exactly where your vehicle has got to on the Jobs "
            + "screen — each job shows its current stage, and you get a "
            + "notification the moment it is marked ready. If it still says "
            + "In progress, the garage has not finished it yet."),

        new(["pay", "payment", "esewa", "khalti", "online"],
            "Open the bill from the Bills screen and tap Pay. You can pay with "
            + "eSewa or Khalti if your garage has them switched on, or by bank "
            + "transfer using the details shown there. Cash is settled at the "
            + "counter and your garage marks it paid."),

        new(["bill", "invoice", "cost", "price", "charge"],
            "Every bill is on the Bills screen, with the parts and labour listed "
            + "separately and VAT shown at the bottom. Tap one to see the full "
            + "breakdown. If a charge on it does not look right, ask the garage "
            + "— they can explain or correct it."),

        new(["book", "booking", "appointment", "slot", "service"],
            "Tap Book a service on the home screen, choose your vehicle and the "
            + "service you want, and pick a date. The garage confirms it, and "
            + "you will get a notification when they do."),

        new(["delivery", "deliver", "drop", "home"],
            "If your garage offers home delivery you will be given the option — "
            + "with the price — when the job is finished. You can watch the "
            + "driver on the map from the Deliveries screen once it is on its "
            + "way."),

        new(["password", "login", "signin", "forgot"],
            "Tap Forgot password on the sign-in screen and we will email you a "
            + "six-digit code. The code lasts fifteen minutes. If it does not "
            + "arrive, check your spam folder before asking for another."),

        new(["garage", "join", "switch", "another"],
            "You can belong to more than one garage. Open your profile and tap "
            + "Find a garage to join another, or switch between the ones you "
            + "have already joined."),
    ];

    /// <summary>Scripted answers for workshop staff, most specific first.</summary>
    public static readonly SupportFaq[] WorkshopFaqs =
    [
        new(["mechanic", "staff", "employee", "advisor", "user"],
            "Staff → Add staff. Give them a name, an email and a role, and the "
            + "screen shows a one-time password to hand over. They are made to "
            + "replace it the first time they sign in, so it stops working and "
            + "you stop knowing their password."),

        new(["logo", "letterhead", "branding"],
            "Workshop → Identity → Logo. It is saved as soon as you choose it "
            + "and appears at the top of every invoice you print. PNG, JPG, "
            + "WebP or SVG, under 1 MB."),

        new(["pan", "vat", "tax", "legal", "registered"],
            "Workshop → Identity. The Registered name and PAN are what get "
            + "printed on a tax invoice, and they must match your registration "
            + "rather than your signage — that is why they are separate from the "
            + "trading name."),

        new(["invoice", "print", "bill", "pdf"],
            "Open the bill from Billing and use Print. To send it as a PDF "
            + "instead of printing, choose Save as PDF as the destination in "
            + "your browser's print dialog."),

        new(["branch", "fiscal", "year", "workspace"],
            "The branch and fiscal-year pickers are in the top bar. Switching "
            + "either one changes the session, so every screen reloads against "
            + "the branch and year you picked."),

        new(["role", "permission", "menu", "hide", "access"],
            "Configuration → Roles. A role owns the menu entries its holders "
            + "see, so you can give the front desk customers and job cards "
            + "without giving them the takings."),

        new(["module", "feature", "missing", "enable"],
            "Which modules your company has is set by GarageFlow, not from "
            + "inside the dashboard — a menu you cannot see is one your plan "
            + "does not include. Ask us and we will tell you what turning it on "
            + "involves."),

        new(["customer", "vehicle", "add"],
            "Customers → Add customer, then add their vehicles from the "
            + "customer's own page. A vehicle always belongs to a customer, "
            + "which is why there is no separate Add vehicle button on the "
            + "Vehicles list."),
    ];

    /// <summary>
    /// The scripted answer for a question, or null when nothing matches.
    /// </summary>
    /// <remarks>
    /// Whole-word matching, not <c>Contains</c>. A substring check would fire
    /// "pay" on "repayment" and, worse, "done" on "abandoned" — and a confident
    /// answer to a question nobody asked is the failure mode this whole layer
    /// has to avoid.
    /// </remarks>
    public static SupportFaq? Match(string audience, string question)
    {
        var words = Tokenise(question);

        if (words.Count == 0) return null;

        var faqs = audience == SupportAudience.WorkshopToPlatform
            ? WorkshopFaqs
            : CustomerFaqs;

        return faqs.FirstOrDefault(faq => faq.Keywords.Any(words.Contains));
    }

    /// <summary>The question as a set of lower-case words.</summary>
    private static HashSet<string> Tokenise(string question)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var current = new System.Text.StringBuilder();

        foreach (var character in question)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) words.Add(current.ToString());

        return words;
    }
}
