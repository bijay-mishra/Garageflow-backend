using System.Security.Claims;
using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using GarageFlow.Api.Services.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// Chat with us — for customers, and for the workshops themselves.
/// </summary>
/// <remarks>
/// <para>
/// Two conversations, one implementation. A customer talks to their garage; a
/// workshop talks to GarageFlow. Which one a caller is having is derived from
/// their role rather than taken as a parameter, because a customer must not be
/// able to open a thread into the operator's inbox by passing a different
/// string.
/// </para>
/// <para>
/// Roles here wear two hats. Workshop staff are the <em>asker</em> on a
/// workshop-to-platform thread and the <em>answerer</em> on their customers'
/// threads; the superadmin answers workshop threads and asks nothing. Every
/// endpoint below is explicit about which hat it needs.
/// </para>
/// </remarks>
[Authorize]
[ApiController]
[Route("api/support")]
[Produces("application/json")]
public class SupportController(
    GarageFlowDbContext db,
    CurrentUserService currentUser,
    SupportBot bot,
    NotificationService notifications,
    TenantContext tenant,
    TimeProvider clock,
    ILogger<SupportController> logger) : ControllerBase
{
    /// <summary>The conversations this caller started.</summary>
    [HttpGet("threads")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<SupportThreadDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SupportThreadDto>>>> MyThreads(
        CancellationToken ct)
    {
        var userId = UserId;

        // Keyed on the account that opened it rather than on the customer
        // record, so a customer with no customer row still sees their own
        // threads and never anybody else's.
        var threads = await db.SupportThreads.AsNoTracking()
            .Where(t => t.OpenedByUserId == userId)
            .OrderByDescending(t => t.LastMessageAt)
            .Include(t => t.Messages)
            .ToListAsync(ct);

        return Ok(ApiResponse<IReadOnlyList<SupportThreadDto>>.Ok(
            threads.Select(t => ToDto(t, forInbox: false)).ToList(),
            threads.Count == 0 ? "No conversations yet." : $"{threads.Count} conversation(s)."));
    }

    /// <summary>
    /// The conversations this caller is expected to answer.
    /// </summary>
    /// <remarks>
    /// Workshop staff get their customers' threads. The superadmin gets every
    /// workshop's threads, crossing the tenant boundary explicitly — the same
    /// <c>IgnoreQueryFilters</c> convention the rest of the operator console
    /// follows, so the crossing is visible in the source rather than implied.
    /// </remarks>
    [HttpGet("inbox")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<SupportThreadDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SupportThreadDto>>>> Inbox(
        CancellationToken ct)
    {
        if (IsOperator)
        {
            var platformThreads = await db.SupportThreads.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Audience == SupportAudience.WorkshopToPlatform
                            && t.EscalatedAt != null)
                .OrderByDescending(t => t.LastMessageAt)
                .Include(t => t.Messages)
                .ToListAsync(ct);

            return Ok(ApiResponse<IReadOnlyList<SupportThreadDto>>.Ok(
                platformThreads.Select(t => ToDto(t, forInbox: true)).ToList(),
                Summarise(platformThreads)));
        }

        if (!IsWorkshopStaff)
            return Forbid();

        var threads = await db.SupportThreads.AsNoTracking()
            .Where(t => t.Audience == SupportAudience.CustomerToWorkshop
                        && t.EscalatedAt != null)
            .OrderByDescending(t => t.LastMessageAt)
            .Include(t => t.Messages)
            .ToListAsync(ct);

        return Ok(ApiResponse<IReadOnlyList<SupportThreadDto>>.Ok(
            threads.Select(t => ToDto(t, forInbox: true)).ToList(),
            Summarise(threads)));
    }

    /// <summary>Opens a conversation and answers the first message.</summary>
    [HttpPost("threads")]
    [ProducesResponseType<ApiResponse<SupportConversationDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<SupportConversationDto>>> Start(
        StartSupportThreadRequest request, CancellationToken ct)
    {
        var user = await currentUser.GetAsync(User, ct);

        if (user is null) return Unauthorized(ApiResponse.Failure("Please sign in again."));

        if (IsOperator)
        {
            // The operator answers threads; there is nobody above them to ask.
            return BadRequest(ApiResponse.Failure(
                "The operator console answers support threads rather than opening them."));
        }

        var message = request.Message.Trim();
        var now = clock.GetUtcNow().UtcDateTime;

        var audience = user.Role == Vocabulary.CustomerRole
            ? SupportAudience.CustomerToWorkshop
            : SupportAudience.WorkshopToPlatform;

        var thread = new SupportThread
        {
            Audience = audience,
            OpenedByUserId = user.Id,

            // Null for staff threads, and null for a customer who has joined no
            // garage — which is the honest answer either way. The bot builds an
            // empty context from it rather than an unfiltered one.
            CustomerId = audience == SupportAudience.CustomerToWorkshop ? user.CustomerId : null,

            Subject = Subject(message),
            Status = SupportStatus.WithBot,
            CreatedAt = now,
            LastMessageAt = now,
        };

        db.SupportThreads.Add(thread);

        // Saved before the bot runs: the thread has to exist for the answer to
        // hang off, and if the model times out the question is still recorded
        // rather than lost.
        await db.SaveChangesAsync(ct);

        Add(thread, SenderFor(user.Role), user.Id, user.FullName, message, source: null, now);
        await db.SaveChangesAsync(ct);

        await AnswerAsync(thread, message, ct);

        return Ok(ApiResponse<SupportConversationDto>.Ok(
            await ConversationAsync(thread, ct), "Conversation started."));
    }

    /// <summary>The messages in one conversation.</summary>
    [HttpGet("threads/{id:int}")]
    [ProducesResponseType<ApiResponse<SupportConversationDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<SupportConversationDto>>> Conversation(
        int id, CancellationToken ct)
    {
        var thread = await FindAsync(id, ct);

        if (thread is null) return NotFound(ApiResponse.Failure("No such conversation."));

        // Opening an inbox thread marks it read. Done here rather than on a
        // separate endpoint because "I looked at it" is exactly this request.
        if (await CanAnswerAsync(thread, ct) && thread.ReadByAgentAt < thread.LastMessageAt)
        {
            thread.ReadByAgentAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        return Ok(ApiResponse<SupportConversationDto>.Ok(
            await ConversationAsync(thread, ct), "Conversation loaded."));
    }

    /// <summary>Adds a message, and answers it if the bot is still handling it.</summary>
    [HttpPost("threads/{id:int}/messages")]
    [ProducesResponseType<ApiResponse<SupportConversationDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<SupportConversationDto>>> Send(
        int id, SupportReplyRequest request, CancellationToken ct)
    {
        var thread = await FindAsync(id, ct);

        if (thread is null) return NotFound(ApiResponse.Failure("No such conversation."));

        var user = await currentUser.GetAsync(User, ct);

        if (user is null) return Unauthorized(ApiResponse.Failure("Please sign in again."));

        var answering = await CanAnswerAsync(thread, ct);
        var asking = thread.OpenedByUserId == user.Id;

        if (!answering && !asking)
            return NotFound(ApiResponse.Failure("No such conversation."));

        var message = request.Message.Trim();
        var now = clock.GetUtcNow().UtcDateTime;

        Add(thread, SenderFor(user.Role), user.Id, user.FullName, message, source: null, now);

        if (answering && !asking)
        {
            // A human on the answering side has replied. The thread belongs to
            // the asker again, and the bot stays out of it from here.
            thread.Status = SupportStatus.Answered;
            thread.ReadByAgentAt = now;
            await db.SaveChangesAsync(ct);

            await NotifyAskerAsync(thread, user.FullName, ct);

            return Ok(ApiResponse<SupportConversationDto>.Ok(
                await ConversationAsync(thread, ct), "Reply sent."));
        }

        // The asker wrote. If a human already owns the thread, it goes back in
        // their queue rather than to the bot.
        if (thread.EscalatedAt is not null)
        {
            thread.Status = SupportStatus.Waiting;
            await db.SaveChangesAsync(ct);

            await NotifyAgentsAsync(thread, message, ct);

            return Ok(ApiResponse<SupportConversationDto>.Ok(
                await ConversationAsync(thread, ct), "Message sent."));
        }

        await db.SaveChangesAsync(ct);
        await AnswerAsync(thread, message, ct);

        return Ok(ApiResponse<SupportConversationDto>.Ok(
            await ConversationAsync(thread, ct), "Message sent."));
    }

    /// <summary>Asks for a person.</summary>
    /// <remarks>
    /// Always available, even when the bot is answering perfectly well. A
    /// support widget that makes you argue with a robot before it will fetch a
    /// human is worse than no widget.
    /// </remarks>
    [HttpPost("threads/{id:int}/escalate")]
    [ProducesResponseType<ApiResponse<SupportConversationDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<SupportConversationDto>>> Escalate(
        int id, CancellationToken ct)
    {
        var thread = await FindAsync(id, ct);

        if (thread is null || thread.OpenedByUserId != UserId)
            return NotFound(ApiResponse.Failure("No such conversation."));

        var now = clock.GetUtcNow().UtcDateTime;

        if (thread.EscalatedAt is null)
        {
            thread.EscalatedAt = now;

            Add(thread, SupportSender.Bot, null, "GarageFlow assistant",
                thread.Audience == SupportAudience.WorkshopToPlatform
                    ? "Passed to the GarageFlow team. Someone will reply here."
                    : "Passed to the garage. They will reply here.",
                SupportAnswerSource.Faq, now);
        }

        thread.Status = SupportStatus.Waiting;
        await db.SaveChangesAsync(ct);

        await NotifyAgentsAsync(thread, thread.Subject, ct);

        return Ok(ApiResponse<SupportConversationDto>.Ok(
            await ConversationAsync(thread, ct), "A person will reply here."));
    }

    // ── The bot ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Answers <paramref name="question"/> in the thread, or escalates.
    /// </summary>
    /// <remarks>
    /// The unanswered path is not a failure — it is the product working. The
    /// bot says it cannot help, the thread moves to a human's queue, and the
    /// asker never has to notice which layer gave up.
    /// </remarks>
    private async Task AnswerAsync(SupportThread thread, string question, CancellationToken ct)
    {
        var answer = await bot.AnswerAsync(thread, question, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        if (answer.Answered)
        {
            Add(thread, SupportSender.Bot, null, "GarageFlow assistant",
                answer.Answer!, answer.Source, now);

            await db.SaveChangesAsync(ct);
            return;
        }

        // Nothing scripted matched and either the model declined or is not
        // configured. Hand over rather than saying nothing.
        thread.EscalatedAt ??= now;
        thread.Status = SupportStatus.Waiting;

        Add(thread, SupportSender.Bot, null, "GarageFlow assistant",
            thread.Audience == SupportAudience.WorkshopToPlatform
                ? "I do not have an answer for that one. I have passed it to the "
                  + "GarageFlow team and they will reply here."
                : "I do not have an answer for that one. I have passed it to the "
                  + "garage and they will reply here.",
            SupportAnswerSource.Unanswered, now);

        await db.SaveChangesAsync(ct);
        await NotifyAgentsAsync(thread, question, ct);
    }

    // ── Notifications ────────────────────────────────────────────────────────

    /// <summary>Tells whoever answers this thread that it is waiting.</summary>
    private async Task NotifyAgentsAsync(
        SupportThread thread, string preview, CancellationToken ct)
    {
        if (thread.Audience == SupportAudience.WorkshopToPlatform)
        {
            // The operator belongs to no company, so the tenant-scoped helpers
            // do not reach them. Written directly, and deliberately not via a
            // Notification row: those are tenant-owned, and a row with the
            // operator's blank company code is a row no query returns.
            logger.LogInformation(
                "Support thread {ThreadId} from {Company} is waiting for the GarageFlow team",
                thread.Id, thread.CompanyCode);

            return;
        }

        await notifications.NotifyStaffAsync(
            thread.CompanyCode,
            "A customer needs help",
            Trim(preview, 140),
            "system",
            thread.Id.ToString(),
            ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Tells the asker that a person has replied.</summary>
    private async Task NotifyAskerAsync(
        SupportThread thread, string agentName, CancellationToken ct)
    {
        if (thread.CustomerId is not { } customerId) return;

        await notifications.NotifyCustomerAsync(
            customerId,
            $"{agentName} replied",
            "Your garage has answered your question.",
            "system",
            thread.Id.ToString(),
            ct);

        await db.SaveChangesAsync(ct);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private string? UserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private bool IsOperator => User.IsInRole(Vocabulary.SuperAdminRole);

    private bool IsWorkshopStaff =>
        Vocabulary.StaffRoles.Any(User.IsInRole) || User.IsInRole(Vocabulary.MechanicRole);

    /// <summary>
    /// The thread, if this caller may see it at all.
    /// </summary>
    /// <remarks>
    /// The tenant filter has already proved the row belongs to this company —
    /// except for the operator, who reads across companies on purpose. What is
    /// left is the check inside the company: an asker sees their own threads, an
    /// answerer sees the ones they answer, and nobody sees another customer's
    /// conversation with the same garage.
    /// </remarks>
    private async Task<SupportThread?> FindAsync(int id, CancellationToken ct)
    {
        var query = IsOperator
            ? db.SupportThreads.IgnoreQueryFilters()
            : db.SupportThreads;

        var thread = await query.FirstOrDefaultAsync(t => t.Id == id, ct);

        if (thread is null) return null;

        if (thread.OpenedByUserId == UserId) return thread;

        return await CanAnswerAsync(thread, ct) ? thread : null;
    }

    /// <summary>Whether this caller is on the answering side of the thread.</summary>
    private Task<bool> CanAnswerAsync(SupportThread thread, CancellationToken ct) =>
        Task.FromResult(thread.Audience switch
        {
            SupportAudience.WorkshopToPlatform => IsOperator,

            // Staff answer their own company's customers. The tenant filter has
            // already established that this thread is their company's.
            SupportAudience.CustomerToWorkshop => IsWorkshopStaff,

            _ => false,
        });

    private static string SenderFor(string role) => role switch
    {
        Vocabulary.SuperAdminRole => SupportSender.Operator,
        Vocabulary.CustomerRole => SupportSender.Customer,
        _ => SupportSender.Staff,
    };

    private void Add(
        SupportThread thread, string sender, string? senderUserId, string senderName,
        string body, string? source, DateTime now)
    {
        db.SupportMessages.Add(new SupportMessage
        {
            // Set explicitly because the operator's own company code is blank,
            // and a message stamped with it would vanish behind the tenant
            // filter the moment the workshop tried to read its own thread.
            CompanyCode = thread.CompanyCode,
            ThreadId = thread.Id,
            Sender = sender,
            SenderUserId = senderUserId,
            SenderName = senderName,
            Body = body,
            Source = source,
            CreatedAt = now,
        });

        thread.LastMessageAt = now;
    }

    private async Task<SupportConversationDto> ConversationAsync(
        SupportThread thread, CancellationToken ct)
    {
        var messages = await db.SupportMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ThreadId == thread.Id)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        return new SupportConversationDto
        {
            Thread = ToDto(thread, forInbox: false, messages),
            Messages = messages.Select(m => new SupportMessageDto
            {
                Id = m.Id,
                Sender = m.Sender,
                SenderName = m.SenderName,
                Body = m.Body,
                Source = m.Source,
                CreatedAt = m.CreatedAt,
            }).ToList(),

            // Once a person owns the thread, the bot is done talking.
            BotActive = thread.EscalatedAt is null,
        };
    }

    private static SupportThreadDto ToDto(
        SupportThread thread, bool forInbox, List<SupportMessage>? messages = null)
    {
        var all = messages ?? thread.Messages;
        var newest = all.OrderByDescending(m => m.Id).FirstOrDefault();

        return new SupportThreadDto
        {
            Id = thread.Id,
            Audience = thread.Audience,
            Subject = thread.Subject,
            Status = thread.Status,
            OpenedBy = all.OrderBy(m => m.Id).FirstOrDefault()?.SenderName ?? "",
            CompanyCode = thread.CompanyCode,
            CreatedAt = thread.CreatedAt,
            LastMessageAt = thread.LastMessageAt,
            EscalatedAt = thread.EscalatedAt,
            Preview = Trim(newest?.Body ?? "", 140),
            MessageCount = all.Count,
            NeedsAttention = forInbox
                             && thread.Status == SupportStatus.Waiting
                             && (thread.ReadByAgentAt is null
                                 || thread.ReadByAgentAt < thread.LastMessageAt),
        };
    }

    private static string Summarise(List<SupportThread> threads)
    {
        var waiting = threads.Count(t => t.Status == SupportStatus.Waiting);

        return threads.Count == 0
            ? "Nothing waiting."
            : waiting == 0
                ? $"{threads.Count} conversation(s), none waiting."
                : $"{waiting} waiting for a reply.";
    }

    /// <summary>The opening message, shortened into something a list can show.</summary>
    private static string Subject(string message)
    {
        var firstLine = message.Split('\n')[0].Trim();

        return Trim(firstLine.Length == 0 ? message.Trim() : firstLine, 120);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";
}
