using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using POS.Application.Abstractions;
using POS.Core.Enums;
using POS.Infrastructure;
using POS.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var applyMigrationsOnStartup = ReadBooleanSetting(builder.Configuration, "Database:ApplyMigrationsOnStartup", builder.Environment.IsDevelopment());
var seedDemoDataOnStartup = ReadBooleanSetting(builder.Configuration, "Database:SeedDemoDataOnStartup", builder.Environment.IsDevelopment());
var useForwardedHeaders = ReadBooleanSetting(builder.Configuration, "ReverseProxy:UseForwardedHeaders", !builder.Environment.IsDevelopment());
var requireHttps = ReadBooleanSetting(builder.Configuration, "Http:RequireHttps", !builder.Environment.IsDevelopment());
var useHsts = ReadBooleanSetting(builder.Configuration, "Http:UseHsts", !builder.Environment.IsDevelopment());

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<ICurrentDevice, WebCurrentDevice>();
builder.Services.AddScoped<ICurrentSession, WebCurrentSession>();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);

if (useForwardedHeaders)
    builder.Services.Configure<ForwardedHeadersOptions>(options => ConfigureForwardedHeaders(options, builder.Configuration));

builder.Services
    .AddAuthentication(WebAuthenticationDefaults.Scheme)
    .AddCookie(WebAuthenticationDefaults.Scheme, options =>
    {
        options.LoginPath = "/account/login";
        options.AccessDeniedPath = "/account/access-denied";
        options.Cookie.Name = "pos.dashboard.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Events = new CookieAuthenticationEvents
        {
            // Closes a real gap (T6 matrix: "stale online token after ... access revocation"): the
            // active-store claim is otherwise only checked at Switch time (StoresController.Switch) and
            // then trusted for the rest of the cookie's up-to-8h sliding window. Re-checking on every
            // request means an admin's RevokeAccess takes effect on the very next request from the
            // revoked user, not merely their next store switch or re-login. Deliberately re-queries the
            // database every request rather than caching/throttling — correctness over latency for an
            // authorization decision; revisit if request volume ever makes this a real bottleneck.
            OnValidatePrincipal = async context =>
            {
                var principal = context.Principal;
                if (principal?.Identity?.IsAuthenticated != true)
                    return;

                var tenantId = TryParseGuid(principal.FindFirstValue(WebClaimTypes.TenantId));
                var userId = TryParseGuid(principal.FindFirstValue(ClaimTypes.NameIdentifier));
                var storeId = TryParseGuid(principal.FindFirstValue(WebClaimTypes.StoreId));
                if (tenantId is null || userId is null || storeId is null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(WebAuthenticationDefaults.Scheme);
                    return;
                }

                var storeAccess = context.HttpContext.RequestServices.GetRequiredService<IStoreAccessService>();
                var stillAllowed = await storeAccess.CanUserAccessStoreAsync(tenantId.Value, userId.Value, storeId.Value, context.HttpContext.RequestAborted);
                if (!stillAllowed)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(WebAuthenticationDefaults.Scheme);
                }
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Reachability gate: any granted permission, regardless of role name — a custom role with, say,
    // only ViewReports must not be locked out just because it isn't literally named Admin/Manager.
    options.AddPolicy(WebAuthorizationPolicies.DashboardAccess, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User) != Permission.None));
    options.AddPolicy(WebAuthorizationPolicies.ManageProducts, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ManageProducts)));
    options.AddPolicy(WebAuthorizationPolicies.ViewReports, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ViewReports)));
    options.AddPolicy(WebAuthorizationPolicies.ViewAudit, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ViewAudit)));
    options.AddPolicy(WebAuthorizationPolicies.ManageUsers, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ManageUsers)));
    options.AddPolicy(WebAuthorizationPolicies.ManageSettings, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ManageSettings)));
    options.AddPolicy(WebAuthorizationPolicies.ProcessRefunds, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ProcessRefunds)));
    options.AddPolicy(WebAuthorizationPolicies.ManageStores, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ManageStores)));
});
builder.Services.AddRateLimiter(options =>
{
    // Per tenant.md's identity section: throttle login attempts to slow down brute-force/enumeration.
    // Partitioned by client IP, not by username, so a failed guess never reveals whether the account exists.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(WebRateLimitPolicies.Login, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

if (applyMigrationsOnStartup)
    app.Services.ApplyPosDatabaseMigrations(seedDemoDataOnStartup);

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Home/Error");

if (useForwardedHeaders)
    app.UseForwardedHeaders();

if (useHsts)
    app.UseHsts();

if (requireHttps)
    app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.RequestServices.GetRequiredService<ICurrentSession>() is WebCurrentSession session)
    {
        session.Clear();

        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var tenantId = TryParseGuid(user.FindFirstValue(WebClaimTypes.TenantId));
            var userId = TryParseGuid(user.FindFirstValue(ClaimTypes.NameIdentifier));
            var storeId = TryParseGuid(user.FindFirstValue(WebClaimTypes.StoreId));
            var username = user.FindFirstValue(ClaimTypes.Name);
            var roleName = user.FindFirstValue(ClaimTypes.Role);
            var baseCurrencyCode = user.FindFirstValue(WebClaimTypes.CurrencyCode);
            var permissionsMask = (int)GetPermissions(user);

            if (tenantId.HasValue
                && userId.HasValue
                && storeId.HasValue
                && !string.IsNullOrWhiteSpace(username)
                && !string.IsNullOrWhiteSpace(roleName)
                && !string.IsNullOrWhiteSpace(baseCurrencyCode))
            {
                session.Set(
                    tenantId.Value,
                    userId.Value,
                    storeId.Value,
                    username,
                    roleName,
                    permissionsMask,
                    baseCurrencyCode,
                    user.FindFirstValue(WebClaimTypes.CurrencySymbol));
            }
        }
    }

    await next();
});
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();

static bool ReadBooleanSetting(IConfiguration configuration, string key, bool defaultValue)
{
    var raw = configuration[key];
    return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
}

static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = int.TryParse(configuration["ReverseProxy:ForwardLimit"], out var limit)
        ? limit
        : 1;

    foreach (var proxy in configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
    {
        if (IPAddress.TryParse(proxy, out var address))
            options.KnownProxies.Add(address);
    }

    foreach (var network in configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
    {
        var parts = network.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            continue;

        if (IPAddress.TryParse(parts[0], out var prefix)
            && int.TryParse(parts[1], out var prefixLength))
        {
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
        }
    }
}

static Guid? TryParseGuid(string? raw) => Guid.TryParse(raw, out var parsed) ? parsed : null;

static Permission GetPermissions(ClaimsPrincipal user) =>
    int.TryParse(user.FindFirstValue(WebClaimTypes.Permissions), out var mask) ? (Permission)mask : Permission.None;
