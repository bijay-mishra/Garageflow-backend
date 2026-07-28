using System.Text.Json;
using GarageFlow.Api.Contracts;
using Microsoft.AspNetCore.Diagnostics;

namespace GarageFlow.Api.Services;

/// <summary>
/// Turns an unhandled exception into the same <see cref="ApiResponse"/> envelope
/// every other endpoint returns, so the dashboard can always read
/// <c>res.data.message</c> — even when something has gone badly wrong.
/// </summary>
/// <remarks>
/// The exception message itself is logged, never returned: it can carry
/// connection strings and table names. The client gets a fixed sentence.
/// </remarks>
public class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        logger.LogError(exception, "Unhandled exception on {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/json";

        var response = ApiResponse.Failure("Something went wrong on the server. Please try again.");

        // Matches the camelCase the rest of the API serialises with.
        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ct);

        return true;
    }
}
