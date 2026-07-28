namespace GarageFlow.Api.Contracts;

// ── Response envelope ────────────────────────────────────────────────────────
// Every endpoint answers with this shape, success or failure. The client reads
// `res.data.data` for the payload and shows `res.data.message` directly — so
// the wording of every toast in the dashboard is decided here, not in the UI.

/// <summary>Status flag carried by every response. Mirrors the client's check on `status`.</summary>
public static class ApiStatus
{
    public const int Failure = 0;
    public const int Success = 1;
}

/// <summary>
/// Standard envelope: <c>{ data, status, message }</c>.
/// </summary>
/// <typeparam name="T">Payload type. Use <see cref="ApiResponse"/> when there is none.</typeparam>
public class ApiResponse<T>
{
    public T? Data { get; init; }

    /// <summary>1 on success, 0 on failure.</summary>
    public int Status { get; init; } = ApiStatus.Success;

    /// <summary>Human-readable sentence. The dashboard shows this verbatim.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Field name → validation messages. Null unless the request failed validation.</summary>
    public IDictionary<string, string[]>? Errors { get; init; }

    public static ApiResponse<T> Ok(T data, string message) =>
        new() { Data = data, Status = ApiStatus.Success, Message = message };

    public static ApiResponse<T> Fail(string message, IDictionary<string, string[]>? errors = null) =>
        new() { Data = default, Status = ApiStatus.Failure, Message = message, Errors = errors };
}

/// <summary>Envelope for endpoints with no payload — deletes, mostly.</summary>
public class ApiResponse : ApiResponse<object>
{
    public static ApiResponse Success(string message) =>
        new() { Data = null, Status = ApiStatus.Success, Message = message };

    public static ApiResponse Failure(string message, IDictionary<string, string[]>? errors = null) =>
        new() { Data = null, Status = ApiStatus.Failure, Message = message, Errors = errors };
}

/// <summary>
/// Payload of a list endpoint: the page of rows plus the total row count across
/// all pages, so the client can size its pager without a second request.
/// </summary>
public class PagedList<T>
{
    /// <summary>Total rows matching the filter, ignoring skip/take.</summary>
    public int Count { get; init; }

    /// <summary>The requested page of rows.</summary>
    public IReadOnlyList<T> List { get; init; } = [];

    public PagedList() { }

    public PagedList(IReadOnlyList<T> list, int count)
    {
        List = list;
        Count = count;
    }
}
