using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using POS.Application.Abstractions;
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
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(WebAuthorizationPolicies.DashboardAccess, policy =>
        policy.RequireRole("Admin", "Manager"));
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
                    0, // Permission enforcement is not wired up for the web dashboard's cookie auth yet — sync-only for now.
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
