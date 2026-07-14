using Radzen;
using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure;
using SyncCoordinator.Infrastructure.Persistence;
using SyncCoordinator.Web.Authentication;
using SyncCoordinator.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSyncCoordinator(builder.Configuration);
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var defaultCulture = builder.Configuration["Localization:DefaultCulture"] ?? "ja-JP";
    var supportedCultures = builder.Configuration
        .GetSection("Localization:SupportedCultures")
        .Get<string[]>()?
        .Select(culture => new CultureInfo(culture))
        .ToArray() ?? [new CultureInfo(defaultCulture)];
    options.DefaultRequestCulture = new(defaultCulture);
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.ApplyCurrentCultureToResponseHeaders = true;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AdminAccountService>();
builder.Services.AddScoped<IPasswordHasher<AdminAccountEntity>, PasswordHasher<AdminAccountEntity>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SyncCoordinator.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var userName = context.Principal?.Identity?.Name;
            var versionValue = context.Principal?.FindFirst(AdminAccountService.SessionVersionClaim)?.Value;
            if (string.IsNullOrWhiteSpace(userName) ||
                !int.TryParse(versionValue, CultureInfo.InvariantCulture, out var version) ||
                !await context.HttpContext.RequestServices
                    .GetRequiredService<AdminAccountService>()
                    .IsSessionCurrentAsync(userName, version, context.HttpContext.RequestAborted))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, AdminRevalidatingAuthenticationStateProvider>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true
            }));
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

var app = builder.Build();
app.UseRequestLocalization();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapSyncCoordinatorAuthentication();
app.MapGet("/api/routes/{routeId:guid}/database-setup/{systemCode}/script", async (
    Guid routeId,
    string systemCode,
    IDatabaseDeploymentService deployments,
    CancellationToken cancellationToken) =>
{
    var plan = await deployments.GetPlanAsync(routeId, cancellationToken);
    var target = plan.Targets.Single(x =>
        string.Equals(x.SystemCode, systemCode, StringComparison.OrdinalIgnoreCase));
    var safeSystemCode = string.Concat(target.SystemCode.Where(char.IsLetterOrDigit));
    return Results.File(
        Encoding.UTF8.GetBytes(target.Script),
        "application/sql; charset=utf-8",
        $"SyncCoordinator_{safeSystemCode}_{routeId:N}.sql");
}).RequireAuthorization();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapDefaultEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<CoordinatorDatabaseInitializer>()
        .InitializeAsync(CancellationToken.None);
}

await app.RunAsync();
