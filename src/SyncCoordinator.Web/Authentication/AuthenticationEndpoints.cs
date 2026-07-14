using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;

namespace SyncCoordinator.Web.Authentication;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapSyncCoordinatorAuthentication(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("admin-login");
        endpoints.MapPost("/auth/logout", LogoutAsync)
            .RequireAuthorization();
        endpoints.MapPost("/auth/setup", SetupAsync)
            .AllowAnonymous();
        endpoints.MapPost("/auth/reset", ResetAsync)
            .AllowAnonymous();
        endpoints.MapPost("/auth/change-password", ChangePasswordAsync)
            .RequireAuthorization();
        endpoints.MapPost("/culture", SetCultureAsync)
            .AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        AdminAccountService accounts,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var result = await accounts.VerifyAsync(
            form["userName"].ToString(),
            form["password"].ToString(),
            cancellationToken);
        var returnUrl = SafeReturnUrl(form["returnUrl"].ToString(), "/");
        if (!result.Succeeded)
        {
            return Results.LocalRedirect($"/login?error=invalid&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        await SignInAsync(
            context,
            result.UserName,
            result.SessionVersion,
            string.Equals(form["rememberMe"], "true", StringComparison.OrdinalIgnoreCase));
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.LocalRedirect("/login");
    }

    private static async Task<IResult> SetupAsync(
        HttpContext context,
        AdminAccountService accounts,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!LocalRequestGuard.IsLocal(context))
        {
            return Results.NotFound();
        }
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var result = await accounts.InitializeAsync(
            form["password"].ToString(),
            form["confirmation"].ToString(),
            cancellationToken);
        if (!result.Succeeded)
        {
            return Results.LocalRedirect($"/account/setup?error={Uri.EscapeDataString(result.ErrorCode!)}");
        }

        await SignInAsync(
            context,
            AdminAccountService.AdministratorUserName,
            result.SessionVersion,
            false);
        return Results.LocalRedirect("/");
    }

    private static async Task<IResult> ResetAsync(
        HttpContext context,
        AdminAccountService accounts,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!LocalRequestGuard.IsLocal(context))
        {
            return Results.NotFound();
        }
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var result = await accounts.ResetAsync(
            form["password"].ToString(),
            form["confirmation"].ToString(),
            cancellationToken);
        if (!result.Succeeded)
        {
            return Results.LocalRedirect($"/account/reset?error={Uri.EscapeDataString(result.ErrorCode!)}");
        }

        await SignInAsync(
            context,
            AdminAccountService.AdministratorUserName,
            result.SessionVersion,
            false);
        return Results.LocalRedirect("/");
    }

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext context,
        AdminAccountService accounts,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var result = await accounts.ChangeAsync(
            form["currentPassword"].ToString(),
            form["password"].ToString(),
            form["confirmation"].ToString(),
            cancellationToken);
        if (!result.Succeeded)
        {
            return Results.LocalRedirect($"/account/password?error={Uri.EscapeDataString(result.ErrorCode!)}");
        }

        await SignInAsync(
            context,
            AdminAccountService.AdministratorUserName,
            result.SessionVersion,
            false);
        return Results.LocalRedirect("/account/password?changed=true");
    }

    private static async Task<IResult> SetCultureAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        await antiforgery.ValidateRequestAsync(context);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var culture = form["culture"].ToString();
        if (culture is not ("ja-JP" or "en-US"))
        {
            return Results.BadRequest();
        }

        context.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(new CultureInfo(culture))),
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(365),
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });
        return Results.LocalRedirect(SafeReturnUrl(form["returnUrl"].ToString(), "/"));
    }

    private static Task SignInAsync(
        HttpContext context,
        string userName,
        int sessionVersion,
        bool persistent)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, userName),
            new Claim(AdminAccountService.SessionVersionClaim, sessionVersion.ToString(CultureInfo.InvariantCulture))
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = persistent,
                AllowRefresh = true
            });
    }

    private static string SafeReturnUrl(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value[0] != '/' ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.StartsWith("/\\", StringComparison.Ordinal))
        {
            return fallback;
        }
        return value;
    }
}
