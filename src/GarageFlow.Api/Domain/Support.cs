namespace GarageFlow.Api.Domain;

/// <summary>
/// Who a support conversation is between.
/// </summary>
/// <remarks>
/// One table, two products. A customer asking their garage "is my car ready?"
/// and a workshop owner asking GarageFlow "how do I add a mechanic?" are the
/// same shape — a thread, some messages, a bot that answers first and a human
/// behind it — and differ only in who the other end is and what the bot knows.
///
/// Splitting them into two tables would duplicate the thread, the message, the
/// escalation state and every query over them, to encode a distinction that one
/// column already carries.
/// </remarks>
public static class SupportAudience
{
    /// <summary>A customer and the garage they have joined.</summary>
    public const string CustomerToWorkshop = "customer";

    /// <summary>Workshop staff and the GarageFlow operator.</summary>
    public const string WorkshopToPlatform = "workshop";

    public static readonly string[] All = [CustomerToWorkshop, WorkshopToPlatform];

    public static bool IsKnown(string? value) => All.Contains(value);
}

/// <summary>Who wrote a message.</summary>
public static class SupportSender
{
    public const string Customer = "customer";
    public const string Staff = "staff";
    public const string Operator = "operator";
    public const string Bot = "bot";
}

/// <summary>
/// Where a thread has got to.
/// </summary>
/// <remarks>
/// Deliberately about <em>who owes a reply</em> rather than about sentiment.
/// "Waiting" is the only state that should put a row in front of a human, and
/// it is the one an inbox sorts on.
/// </remarks>
public static class SupportStatus
{
    /// <summary>The bot is handling it; no human has been asked for.</summary>
    public const string WithBot = "bot";

    /// <summary>Escalated. A human owes a reply.</summary>
    public const string Waiting = "waiting";

    /// <summary>A human has replied and the thread is with the asker.</summary>
    public const string Answered = "answered";

    /// <summary>Done. Reopens if anybody writes again.</summary>
    public const string Closed = "closed";
}

/// <summary>How a bot message was produced.</summary>
/// <remarks>
/// Stored because the two have very different trust profiles: a scripted answer
/// was written by a person and is always right, an AI answer was generated and
/// might not be. The clients label them differently for exactly that reason, and
/// an operator reviewing a thread needs to know which they are reading.
/// </remarks>
public static class SupportAnswerSource
{
    public const string Faq = "faq";
    public const string Ai = "ai";

    /// <summary>The bot had nothing and said so.</summary>
    public const string Unanswered = "none";
}

/// <summary>
/// One support conversation.
/// </summary>
/// <remarks>
/// Tenant-owned, so the global query filter keeps one workshop's threads away
/// from another's without a single <c>Where(CompanyCode == …)</c> in a
/// controller. The operator's console crosses that boundary deliberately with
/// <c>IgnoreQueryFilters()</c>, the same way every other cross-company read in
/// this codebase does.
///
/// A customer's own threads are a second, narrower scope <em>inside</em> the
/// tenant filter — the filter proves the thread belongs to this garage, and
/// <see cref="CustomerId"/> proves it belongs to this customer. Both are needed:
/// one customer must not read another's conversation with the same workshop.
/// </remarks>
public class SupportThread : ITenantOwned
{
    public string CompanyCode { get; set; } = default!;

    public int Id { get; set; }

    /// <summary>One of <see cref="SupportAudience"/>.</summary>
    public string Audience { get; set; } = SupportAudience.CustomerToWorkshop;

    /// <summary>The account that started it.</summary>
    public string OpenedByUserId { get; set; } = default!;

    /// <summary>
    /// The customer this thread belongs to, for customer threads.
    /// </summary>
    /// <remarks>
    /// Null on workshop-to-platform threads, which belong to the company rather
    /// than to a person. Read paths must therefore never treat "null customer"
    /// as "no filter" — see CurrentUserService.CustomerScopeAsync, which exists
    /// because that exact conflation once leaked a whole workshop.
    /// </remarks>
    public string? CustomerId { get; set; }

    /// <summary>First line of the opening message, for the inbox list.</summary>
    public string Subject { get; set; } = "";

    /// <summary>One of <see cref="SupportStatus"/>.</summary>
    public string Status { get; set; } = SupportStatus.WithBot;

    public DateTime CreatedAt { get; set; }

    /// <summary>Sorts the inbox. Updated on every message, from either side.</summary>
    public DateTime LastMessageAt { get; set; }

    /// <summary>When a human was first asked for. Null while the bot is coping.</summary>
    public DateTime? EscalatedAt { get; set; }

    /// <summary>Set when a human on the answering side has read the thread.</summary>
    public DateTime? ReadByAgentAt { get; set; }

    public List<SupportMessage> Messages { get; set; } = [];
}

/// <summary>One message in a thread.</summary>
public class SupportMessage : ITenantOwned
{
    public string CompanyCode { get; set; } = default!;

    public int Id { get; set; }

    public int ThreadId { get; set; }
    public SupportThread? Thread { get; set; }

    /// <summary>One of <see cref="SupportSender"/>.</summary>
    public string Sender { get; set; } = SupportSender.Customer;

    /// <summary>The person who wrote it. Null for the bot, which is nobody.</summary>
    public string? SenderUserId { get; set; }

    /// <summary>Display name, captured at write time.</summary>
    /// <remarks>
    /// Denormalised on purpose: a thread has to still read correctly after the
    /// staff member who answered it has left and their account is gone. Joining
    /// to Users would turn their departure into a blank name on an old
    /// conversation, or a foreign key that blocks the delete.
    /// </remarks>
    public string SenderName { get; set; } = "";

    public string Body { get; set; } = "";

    /// <summary>
    /// One of <see cref="SupportAnswerSource"/> for bot messages, null for
    /// people.
    /// </summary>
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; }
}
