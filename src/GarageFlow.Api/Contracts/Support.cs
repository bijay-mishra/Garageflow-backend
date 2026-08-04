using System.ComponentModel.DataAnnotations;

namespace GarageFlow.Api.Contracts;

/// <summary>A support conversation, as a list shows it.</summary>
public record SupportThreadDto
{
    public required int Id { get; init; }
    public required string Audience { get; init; }
    public required string Subject { get; init; }
    public required string Status { get; init; }

    /// <summary>Who opened it, for an inbox. Their own name for the asker.</summary>
    public required string OpenedBy { get; init; }

    /// <summary>The company, so the operator's inbox can say whose it is.</summary>
    public required string CompanyCode { get; init; }

    public required DateTime CreatedAt { get; init; }
    public required DateTime LastMessageAt { get; init; }

    /// <summary>Null while the bot is still handling it.</summary>
    public required DateTime? EscalatedAt { get; init; }

    /// <summary>First line of the newest message, for the list.</summary>
    public required string Preview { get; init; }

    public required int MessageCount { get; init; }

    /// <summary>
    /// True when the answering side has not read it since the last message.
    /// </summary>
    /// <remarks>
    /// Only meaningful in an inbox — it is what makes a row bold. On the
    /// asker's own list it is always false, because "unread by an agent" is not
    /// a fact about them.
    /// </remarks>
    public required bool NeedsAttention { get; init; }
}

/// <summary>One message.</summary>
public record SupportMessageDto
{
    public required int Id { get; init; }
    public required string Sender { get; init; }
    public required string SenderName { get; init; }
    public required string Body { get; init; }

    /// <summary>
    /// For a bot message, whether it was scripted or generated. Null for people.
    /// </summary>
    /// <remarks>
    /// Sent to the clients so they can label a generated answer as one. A
    /// scripted answer was written by a person and can be shown plainly; an AI
    /// answer is a guess with a good hit rate, and saying so is the difference
    /// between a helpful bot and a misleading one.
    /// </remarks>
    public required string? Source { get; init; }

    public required DateTime CreatedAt { get; init; }
}

/// <summary>A thread and everything said in it.</summary>
public record SupportConversationDto
{
    public required SupportThreadDto Thread { get; init; }
    public required IReadOnlyList<SupportMessageDto> Messages { get; init; }

    /// <summary>
    /// Whether this caller may still talk to the bot in this thread.
    /// </summary>
    /// <remarks>
    /// False once a human is involved: a bot that keeps interjecting after
    /// somebody asked for a person is the single most irritating thing a
    /// support widget can do.
    /// </remarks>
    public required bool BotActive { get; init; }
}

public class StartSupportThreadRequest
{
    [Required, StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = "";
}

public class SupportReplyRequest
{
    [Required, StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = "";
}
