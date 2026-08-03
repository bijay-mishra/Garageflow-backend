using GarageFlow.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GarageFlow.Api.Services;

/// <summary>
/// Marks an endpoint a half-signed-in account may still reach.
/// </summary>
/// <remarks>
/// Only three things qualify, and each is on the path out of that state: set
/// the password, find out who you are so the screen can greet you, and sign
/// out. Anything else added here should be justified the same way.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllowWhileSettingPasswordAttribute : Attribute;

/// <summary>
/// Stops an account that is still on somebody else's password from doing
/// anything but replacing it.
/// </summary>
/// <remarks>
/// <para>
/// Registered globally rather than applied per controller, and that is the
/// whole point: a rule written as "remember to add this attribute" is a rule
/// that holds until the next endpoint. This one is on by default and has to be
/// opted out of, so a new controller is covered the day it is written.
/// </para>
/// <para>
/// The client has a screen for this too, but the screen is a convenience. A
/// user who closes it, or a script that never opened it, still gets refused
/// here — which is the difference between a workflow and a control.
/// </para>
/// </remarks>
public class MustSetPasswordFilter : IAuthorizationFilter
{
    /// <summary>
    /// The distinguishing code, so the client can route to the right screen
    /// instead of showing its generic "no permission" message.
    /// </summary>
    public const string Code = "must_set_password";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;

        if (user.Identity?.IsAuthenticated != true) return;

        if (!user.HasClaim(c => c.Type == TokenService.MustSetPasswordClaim)) return;

        var allowed = context.ActionDescriptor.EndpointMetadata
            .OfType<AllowWhileSettingPasswordAttribute>()
            .Any();

        if (allowed) return;

        // 403 rather than 401: the token is perfectly valid and signing in again
        // changes nothing. 401 would send the client round the login loop it
        // just came out of.
        context.Result = new ObjectResult(
            ApiResponse.Failure(
                "Choose your own password before you carry on.",
                new Dictionary<string, string[]> { [Code] = ["A new password is required."] }))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
