using System.Security.Claims;
using EventSourcingCqrs.Hosts.Authentication;
using EventSourcingCqrs.Hosts.Web.Components.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EventSourcingCqrs.Hosts.Web.Authentication;

// Account forms use current HTTP identity, outside the interactive circuit. A rejected submission
// renders a new form; it never retries the posted credentials or changes authentication state.
internal sealed class WebOperatorAccount
{
    private const string DefaultReturnUrl = "/orders";
    private const string ExpiredFormMessage =
        "This form expired or could not be verified. Please use the current account form below.";
    private readonly Guid _actorId;
    private readonly OperatorPassword _password;

    public WebOperatorAccount(IConfiguration configuration)
    {
        _actorId = Guid.TryParse(configuration["BootstrapAdministrator:AdministratorUserId"], out var actor)
            && actor != Guid.Empty
                ? actor
                : throw new InvalidOperationException("BootstrapAdministrator:AdministratorUserId is not set.");
        _password = new OperatorPassword(configuration[OperatorPassword.ConfigurationKey]);
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
            Page(context, antiforgery, context.Request.Query["ReturnUrl"].ToString()))
            .AllowAnonymous();
        app.MapPost("/account/login", SignInAsync).AllowAnonymous().RequireRateLimiting("operator-login");
        app.MapPost("/account/logout", SignOutAsync).AllowAnonymous();
    }

    private async Task<IResult> SignInAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!await HasValidTokenAsync(context, antiforgery))
            return Page(context, antiforgery, DefaultReturnUrl, ExpiredFormMessage, StatusCodes.Status400BadRequest);

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var returnUrl = form["returnUrl"].ToString();
        if (string.IsNullOrWhiteSpace(returnUrl)) returnUrl = DefaultReturnUrl;
        if (!RedirectHttpResult.IsLocalUrl(returnUrl))
            return Results.BadRequest("The return URL must be local.");
        if (!_password.Verify(form["password"].ToString()))
            return Results.Unauthorized();

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _actorId.ToString())], OperatorPassword.AuthenticationScheme);
        await context.SignInAsync(OperatorPassword.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!await HasValidTokenAsync(context, antiforgery))
            return Page(context, antiforgery, DefaultReturnUrl, ExpiredFormMessage, StatusCodes.Status400BadRequest);
        await context.SignOutAsync(OperatorPassword.AuthenticationScheme);
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

    private static RazorComponentResult<Login> Page(HttpContext context, IAntiforgery antiforgery,
        string returnUrl, string? error = null, int statusCode = StatusCodes.Status200OK)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        return new RazorComponentResult<Login>(new
        {
            ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? DefaultReturnUrl : returnUrl,
            Error = error,
            ActorId = context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value : null,
            TokenFieldName = tokens.FormFieldName,
            Token = tokens.RequestToken,
        }) { StatusCode = statusCode };
    }
}
