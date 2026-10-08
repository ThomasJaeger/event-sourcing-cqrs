using System.Security.Claims;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Account;
using EventSourcingCqrs.Hosts.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EventSourcingCqrs.Hosts.AdminConsole.Authentication;

// Chapter 17: establish an operator identity without granting a role on the cookie. The host's
// existing permission gate resolves current roles for every protected HTTP request.
internal sealed class AdminOperatorAccount
{
    public const string AuthenticationScheme = "AdminOperatorPasswordV1";
    private readonly Guid _actorId;
    private readonly OperatorPassword _password;

    public AdminOperatorAccount(IConfiguration configuration)
    {
        _actorId = Guid.TryParse(configuration["BootstrapAdministrator:AdministratorUserId"], out var actor)
            && actor != Guid.Empty
                ? actor
                : throw new InvalidOperationException("BootstrapAdministrator:AdministratorUserId is not set.");
        _password = new OperatorPassword(configuration[OperatorPassword.ConfigurationKey]);
    }

    public void MapEndpoints(WebApplication app)
    {
        // Static HTTP rendering avoids requiring an anonymous interactive circuit. Only these
        // account surfaces opt out of the tool gate; the hub keeps the fallback policy.
        app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
            Page(context, antiforgery, context.Request.Query["ReturnUrl"].ToString()))
            .AllowAnonymous();
        app.MapPost("/account/login", SignInAsync)
            .AllowAnonymous().RequireRateLimiting("admin-operator-login");
        // Logout must work after permission revocation. It clears only this host's cookie and
        // still requires a valid antiforgery token for the requesting browser identity.
        app.MapPost("/account/logout", SignOutAsync).AllowAnonymous();
    }

    private async Task<IResult> SignInAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!await HasValidTokenAsync(context, antiforgery))
            return ExpiredForm(context, antiforgery);

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var returnUrl = form["returnUrl"].ToString();
        if (string.IsNullOrWhiteSpace(returnUrl)) returnUrl = "/";
        if (!RedirectHttpResult.IsLocalUrl(returnUrl))
            return Results.BadRequest("The return URL must be local.");
        if (!_password.Verify(form["password"].ToString()))
            return Page(context, antiforgery, returnUrl, "The password was not accepted.", StatusCodes.Status401Unauthorized);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _actorId.ToString())], AuthenticationScheme);
        await context.SignInAsync(AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!await HasValidTokenAsync(context, antiforgery))
            return ExpiredForm(context, antiforgery);
        await context.SignOutAsync(AuthenticationScheme);
        return Results.LocalRedirect("/login");
    }

    private static async Task<bool> HasValidTokenAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    // A form from another tab can belong to the previous identity. Reject the action and render
    // fresh tokens for the current principal; never replay the submitted credentials or return URL.
    private static RazorComponentResult<Login> ExpiredForm(HttpContext context, IAntiforgery antiforgery) =>
        Page(context, antiforgery, "/",
            "This form expired or could not be verified. Please use the current account form below.",
            StatusCodes.Status400BadRequest);

    private static RazorComponentResult<Login> Page(HttpContext context, IAntiforgery antiforgery,
        string returnUrl, string? error = null, int statusCode = StatusCodes.Status200OK)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        return new RazorComponentResult<Login>(new
        {
            ReturnUrl = returnUrl,
            Error = error,
            ActorId = context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value : null,
            TokenFieldName = tokens.FormFieldName,
            Token = tokens.RequestToken,
        }) { StatusCode = statusCode };
    }
}
