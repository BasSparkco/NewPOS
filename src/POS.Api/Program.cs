using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using POS.Api;
using POS.Application.Abstractions;
using POS.Application.Models;
using POS.Core;
using POS.Core.Entities;
using POS.Core.Enums;
using POS.Infrastructure;
using POS.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

const string TenantIdClaim = "tenant_id";
const string StoreIdClaim = "store_id";
const string CurrencyCodeClaim = "currency_code";
const string CurrencySymbolClaim = "currency_symbol";
const string PermissionsClaim = "permissions";
const string TokenTypeClaim = "token_type";
const string DeviceIdClaim = "device_id";
const string DeviceTokenType = "device";
const string ProcessRefundsPolicy = "Permission:ProcessRefunds";
const string ManageUsersPolicy = "Permission:ManageUsers";
const string ManageSettingsPolicy = "Permission:ManageSettings";
const string ManageProductsPolicy = "Permission:ManageProducts";
// Device credentials (tenant.md Stage 4T) authenticate a terminal for background synchronization
// only — never a business operation. The default policy therefore rejects a device token outright;
// this policy is applied explicitly to the handful of sync endpoints that must keep working after an
// employee logs out (invoice push/pull and read-only catalog/settings/user/device/audit pulls).
const string SyncPolicy = "Sync:DeviceOrUser";
const string LoginRateLimitPolicy = "login";
const string DevelopmentSigningKey = "local-development-signing-key-1234567890";

var applyMigrationsOnStartup = ReadBooleanSetting(builder.Configuration, "Database:ApplyMigrationsOnStartup", builder.Environment.IsDevelopment());
var seedDemoDataOnStartup = ReadBooleanSetting(builder.Configuration, "Database:SeedDemoDataOnStartup", builder.Environment.IsDevelopment());
var useForwardedHeaders = ReadBooleanSetting(builder.Configuration, "ReverseProxy:UseForwardedHeaders", !builder.Environment.IsDevelopment());
var requireHttps = ReadBooleanSetting(builder.Configuration, "Http:RequireHttps", !builder.Environment.IsDevelopment());
var useHsts = ReadBooleanSetting(builder.Configuration, "Http:UseHsts", !builder.Environment.IsDevelopment());

var jwtSection = builder.Configuration.GetSection("Jwt");
var issuer = jwtSection["Issuer"] ?? "POS.Api";
var audience = jwtSection["Audience"] ?? "POS.Clients";
var signingKey = jwtSection["SigningKey"];
var expiryMinutes = int.TryParse(jwtSection["AccessTokenMinutes"], out var configuredExpiry)
    ? configuredExpiry
    : 120;
// Device tokens live longer than a user login: they authenticate unattended background sync, which
// must keep working between a store's opening and closing without needing a human present to re-auth.
var deviceTokenExpiryMinutes = int.TryParse(jwtSection["DeviceTokenMinutes"], out var configuredDeviceExpiry)
    ? configuredDeviceExpiry
    : 1440;

if (string.IsNullOrWhiteSpace(signingKey))
    throw new InvalidOperationException("Jwt:SigningKey is required. Configure it via appsettings or environment variables.");

if (signingKey.Length < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters long.");

if (!builder.Environment.IsDevelopment() && IsUnsafeSigningKey(signingKey))
    throw new InvalidOperationException("Jwt:SigningKey must be replaced with a production secret outside Development.");

var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new()
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Paste the JWT access token here."
    });

    options.AddSecurityRequirement(new()
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddSingleton<ICurrentDevice, ApiCurrentDevice>();
builder.Services.AddScoped<ICurrentSession, ApiCurrentSession>();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);
if (useForwardedHeaders)
    builder.Services.Configure<ForwardedHeadersOptions>(options => ConfigureForwardedHeaders(options, builder.Configuration));
builder.Services.AddAuthorization(options =>
{
    // Cash-register endpoints (catalog browsing, cart/sale lifecycle) intentionally stay at the
    // "any authenticated user" RequireAuthorization() default, matching WPF where any signed-in
    // cashier can sell. The endpoints below mirror a WPF/Web permission gate on the matching action —
    // most importantly the sync push endpoints, which re-authenticate as the currently signed-in
    // user's own verified identity (see InvoiceSyncService.TryCreateAuthorizedClientAsync), so gating
    // them here closes the same "any authenticated user can push a fabricated Role.PermissionsMask,
    // product price, or store setting" escalation path that tenant.md's T2 milestone calls out.
    options.AddPolicy(ProcessRefundsPolicy, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ProcessRefunds)));
    options.AddPolicy(ManageUsersPolicy, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ManageUsers)));
    options.AddPolicy(ManageSettingsPolicy, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ManageSettings)));
    options.AddPolicy(ManageProductsPolicy, policy =>
        policy.RequireAssertion(ctx => GetPermissions(ctx.User).HasFlag(Permission.ManageProducts)));

    // A device token authenticates the terminal for background sync only (tenant.md Stage 4T): it must
    // never substitute for an employee login on a business operation (sales, refunds, administration).
    // Every endpoint using the bare RequireAuthorization() default therefore rejects a device token;
    // SyncPolicy is opted into explicitly by the handful of endpoints that should keep working with one.
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => ctx.User.FindFirstValue(TokenTypeClaim) != DeviceTokenType)
        .Build();
    options.AddPolicy(SyncPolicy, policy => policy.RequireAuthenticatedUser());
});
builder.Services.AddRateLimiter(options =>
{
    // Per tenant.md's identity section: throttle login attempts to slow down brute-force/enumeration.
    // Partitioned by client IP, not by username, so a failed guess never reveals whether the account exists.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(LoginRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = securityKey,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

var app = builder.Build();

// Opt-in: minimal authenticated tenant/store/first-admin provisioning (tenant.md T2). Unset (the
// default) disables the endpoint entirely — no public self-service signup. When set, a caller proves
// authorization with this shared secret rather than a normal user/JWT identity, since the whole point
// is bootstrapping a tenant's very first account before any of its users can log in.
// Read from app.Configuration (not builder.Configuration) — test-host configuration overrides aren't
// guaranteed to be merged into builder.Configuration until Build() runs.
var provisioningSecret = app.Configuration["Platform:ProvisioningSecret"];

if (applyMigrationsOnStartup)
    app.Services.ApplyPosDatabaseMigrations(seedDemoDataOnStartup);

if (useForwardedHeaders)
    app.UseForwardedHeaders();

if (useHsts)
    app.UseHsts();

if (requireHttps)
    app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.RequestServices.GetRequiredService<ICurrentSession>() is ApiCurrentSession session)
    {
        session.Clear();

        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var tenantId = TryParseGuid(user.FindFirstValue(TenantIdClaim));
            var userId = TryParseGuid(user.FindFirstValue(ClaimTypes.NameIdentifier));
            var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
            var username = user.FindFirstValue(ClaimTypes.Name);
            var roleName = user.FindFirstValue(ClaimTypes.Role);
            var baseCurrencyCode = user.FindFirstValue(CurrencyCodeClaim);
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
                    user.FindFirstValue(CurrencySymbolClaim));
            }
        }
    }

    await next();
});
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/swagger"));

app.MapGet("/health", async (IDbContextFactory<PosDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var canConnect = await db.Database.CanConnectAsync(cancellationToken);

    return Results.Ok(new
    {
        status = canConnect ? "ok" : "degraded",
        database = canConnect ? "reachable" : "unreachable"
    });
});

app.MapGet("/api/meta/ping", (IConfiguration configuration) =>
{
    var provider = configuration["Database:Provider"] ?? "Sqlite";
    return Results.Ok(new
    {
        name = "POS API",
        version = "v1",
        databaseProvider = provider,
        utc = DateTime.UtcNow
    });
});

app.MapPost("/api/platform/tenants", async (
    HttpRequest request,
    ProvisionTenantRequest body,
    ITenantProvisioningService provisioning,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(provisioningSecret))
        return Results.NotFound();

    var suppliedSecret = request.Headers["X-Provisioning-Secret"].ToString();
    if (!FixedTimeEquals(suppliedSecret, provisioningSecret))
        return Results.Unauthorized();

    var (success, error, result) = await provisioning.ProvisionTenantAsync(
        body.TenantName, body.TenantSlug, body.StoreName, body.AdminUsername, body.AdminPassword, body.BaseCurrencyCode, cancellationToken);

    if (!success || result is null)
        return Results.BadRequest(new ApiErrorResponse(error ?? "Could not provision tenant."));

    return Results.Ok(new ProvisionTenantResponse(result.TenantId, result.StoreId, result.AdminUserId));
});

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    IAuthService authService,
    ICurrentSession session,
    CancellationToken cancellationToken) =>
{
    var result = await authService.LoginAsync(request.Username, request.Password, request.TenantSlug, cancellationToken);
    if (!result.Success || !session.IsAuthenticated)
        return Results.Unauthorized();

    var now = DateTime.UtcNow;
    var expiresAt = now.AddMinutes(expiryMinutes);

    var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, session.UserId.ToString()),
        new(JwtRegisteredClaimNames.UniqueName, session.Username),
        new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
        new(ClaimTypes.Name, session.Username),
        new(ClaimTypes.Role, session.RoleName),
        new(TenantIdClaim, session.TenantId.ToString()),
        new(StoreIdClaim, session.StoreId.ToString()),
        new(CurrencyCodeClaim, session.BaseCurrencyCode),
        new(PermissionsClaim, session.PermissionsMask.ToString())
    };

    if (!string.IsNullOrWhiteSpace(session.CurrencySymbol))
        claims.Add(new Claim(CurrencySymbolClaim, session.CurrencySymbol));

    var tokenDescriptor = new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(claims),
        Issuer = issuer,
        Audience = audience,
        Expires = expiresAt,
        SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256)
    };

    var tokenHandler = new JwtSecurityTokenHandler();
    var token = tokenHandler.CreateToken(tokenDescriptor);

    return Results.Ok(new LoginResponse(
        tokenHandler.WriteToken(token),
        expiresAt,
        session.UserId,
        session.StoreId,
        session.Username,
        session.RoleName,
        session.BaseCurrencyCode,
        session.CurrencySymbol));
}).RequireRateLimiting(LoginRateLimitPolicy);

app.MapPost("/api/devices/token", async (
    DeviceTokenRequest request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Secret))
        return Results.Unauthorized();

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var device = await db.Devices
        .AsNoTracking()
        .FirstOrDefaultAsync(d => d.Id == request.DeviceId && !d.IsDeleted, cancellationToken);

    // Same generic-failure shape for "no such device", "not enrolled", "revoked" and "wrong secret" —
    // this is a credential check, so it must not let a caller distinguish those cases (tenant.md §5).
    if (device is null || device.IsRevoked || string.IsNullOrEmpty(device.DeviceSecretHash))
        return Results.Unauthorized();

    bool secretMatches;
    try
    {
        secretMatches = BCrypt.Net.BCrypt.Verify(request.Secret, device.DeviceSecretHash);
    }
    catch (BCrypt.Net.SaltParseException)
    {
        secretMatches = false;
    }

    if (!secretMatches)
        return Results.Unauthorized();

    var now = DateTime.UtcNow;
    var expiresAt = now.AddMinutes(deviceTokenExpiryMinutes);
    var claims = new List<Claim>
    {
        new(TenantIdClaim, device.TenantId.ToString()),
        new(StoreIdClaim, device.StoreId.ToString()),
        new(DeviceIdClaim, device.Id.ToString()),
        new(TokenTypeClaim, DeviceTokenType)
    };

    var tokenDescriptor = new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(claims),
        Issuer = issuer,
        Audience = audience,
        Expires = expiresAt,
        SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256)
    };
    var tokenHandler = new JwtSecurityTokenHandler();
    var token = tokenHandler.CreateToken(tokenDescriptor);

    return Results.Ok(new DeviceTokenResponse(tokenHandler.WriteToken(token), expiresAt));
}).RequireRateLimiting(LoginRateLimitPolicy);

app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
{
    if (user.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var userId = TryParseGuid(user.FindFirstValue(ClaimTypes.NameIdentifier));
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));

    return Results.Ok(new CurrentUserResponse(
        userId,
        storeId,
        user.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
        user.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
        user.FindFirstValue(CurrencyCodeClaim) ?? string.Empty,
        user.FindFirstValue(CurrencySymbolClaim)));
}).RequireAuthorization();

app.MapGet("/api/stores/current", async (ClaimsPrincipal user, IDbContextFactory<PosDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var store = await db.Stores
        .AsNoTracking()
        .Where(s => s.Id == storeId.Value && !s.IsDeleted)
        .Select(s => new
        {
            s.Id,
            s.Name,
            s.Address,
            s.Phone,
            s.BaseCurrencyId
        })
        .FirstOrDefaultAsync(cancellationToken);

    return store is null ? Results.NotFound() : Results.Ok(store);
}).RequireAuthorization();

var catalogApi = app.MapGroup("/api/catalog")
    .RequireAuthorization();

catalogApi.MapGet("/categories", async (IProductCatalogService catalog, CancellationToken cancellationToken) =>
{
    var categories = await catalog.GetCategoriesAsync(cancellationToken);
    return Results.Ok(categories);
});

catalogApi.MapGet("/products", async (string? query, Guid? categoryId, IProductCatalogService catalog, CancellationToken cancellationToken) =>
{
    var products = await catalog.SearchProductsAsync(query, categoryId, cancellationToken);
    return Results.Ok(products);
});

var salesApi = app.MapGroup("/api/sales")
    .RequireAuthorization();

// No blanket RequireAuthorization() here — ASP.NET Core combines (ANDs) a group-level authorization
// requirement with each endpoint's own, so a bare group-level policy would silently re-impose the
// user-only DefaultPolicy underneath SyncPolicy and defeat device-token sync entirely. Every endpoint
// below declares its own explicit policy instead (SyncPolicy, a ManageXPolicy, or the bare user-only
// default via .RequireAuthorization()).
var syncApi = app.MapGroup("/api/sync");

salesApi.MapPost("/open", async (ISaleService sales, CancellationToken cancellationToken) =>
{
    var invoiceId = await sales.StartNewSaleAsync(cancellationToken);
    var summary = await sales.GetInvoiceSummaryAsync(invoiceId, cancellationToken);
    return Results.Ok(new SaleSnapshotResponse(invoiceId, summary, Array.Empty<CartLineDto>()));
});

salesApi.MapGet("/{invoiceId:guid}", async (Guid invoiceId, ISaleService sales, CancellationToken cancellationToken) =>
{
    try
    {
        var summary = await sales.GetInvoiceSummaryAsync(invoiceId, cancellationToken);
        var lines = await sales.GetCartLinesAsync(invoiceId, cancellationToken);
        return Results.Ok(new SaleSnapshotResponse(invoiceId, summary, lines));
    }
    catch (InvalidOperationException)
    {
        return Results.NotFound();
    }
});

salesApi.MapGet("/{invoiceId:guid}/summary", async (Guid invoiceId, ISaleService sales, CancellationToken cancellationToken) =>
{
    try
    {
        var summary = await sales.GetInvoiceSummaryAsync(invoiceId, cancellationToken);
        return Results.Ok(summary);
    }
    catch (InvalidOperationException)
    {
        return Results.NotFound();
    }
});

salesApi.MapPost("/{invoiceId:guid}/lines", async (
    Guid invoiceId,
    AddSaleLineRequest request,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    try
    {
        await sales.AddOrMergeLineAsync(invoiceId, request.ProductId, request.Quantity, cancellationToken);
        return Results.Ok(await BuildSaleSnapshotAsync(invoiceId, sales, cancellationToken));
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
    {
        return MapSaleError(ex);
    }
});

salesApi.MapDelete("/{invoiceId:guid}/lines/{lineId:guid}", async (
    Guid invoiceId,
    Guid lineId,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    try
    {
        await sales.RemoveLineAsync(invoiceId, lineId, cancellationToken);
        return Results.Ok(await BuildSaleSnapshotAsync(invoiceId, sales, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return MapSaleError(ex);
    }
});

salesApi.MapPatch("/{invoiceId:guid}/lines/{lineId:guid}/quantity", async (
    Guid invoiceId,
    Guid lineId,
    UpdateSaleLineQuantityRequest request,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    try
    {
        await sales.SetLineQuantityAsync(invoiceId, lineId, request.Quantity, cancellationToken);
        return Results.Ok(await BuildSaleSnapshotAsync(invoiceId, sales, cancellationToken));
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
    {
        return MapSaleError(ex);
    }
});

salesApi.MapPatch("/{invoiceId:guid}/lines/{lineId:guid}/discount", async (
    Guid invoiceId,
    Guid lineId,
    UpdateSaleLineDiscountRequest request,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    try
    {
        await sales.SetLineDiscountAsync(invoiceId, lineId, request.DiscountPercent, cancellationToken);
        return Results.Ok(await BuildSaleSnapshotAsync(invoiceId, sales, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return MapSaleError(ex);
    }
});

salesApi.MapPatch("/{invoiceId:guid}/tax", async (
    Guid invoiceId,
    UpdateSaleTaxRequest request,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    try
    {
        await sales.SetInvoiceTaxAsync(invoiceId, request.TaxPercent, cancellationToken);
        return Results.Ok(await BuildSaleSnapshotAsync(invoiceId, sales, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return MapSaleError(ex);
    }
});

salesApi.MapPost("/{invoiceId:guid}/complete/cash", async (
    Guid invoiceId,
    CompleteCashSaleRequest request,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    var result = await sales.CompleteCashSaleAsync(invoiceId, request.CashTendered, cancellationToken);
    if (!result.Success || result.Receipt is null)
        return Results.BadRequest(new ApiErrorResponse(result.ErrorMessage ?? "Could not complete sale."));

    return Results.Ok(new CompleteCashSaleResponse(result.Receipt));
});

salesApi.MapPost("/{invoiceId:guid}/hold", async (
    Guid invoiceId,
    ICurrentSession session,
    IDbContextFactory<PosDbContext> dbFactory,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    var invoice = await GetInvoiceAccessInfoAsync(invoiceId, dbFactory, cancellationToken);
    var accessError = ValidateInvoiceMutationAccess(invoice, session.UserId);
    if (accessError is not null)
        return accessError;

    if (invoice!.Status != InvoiceStatus.Open)
        return Results.BadRequest(new ApiErrorResponse("Invoice must be open to be put on hold."));

    await sales.HoldInvoiceAsync(invoiceId, cancellationToken);
    return Results.Ok(await BuildSaleLifecycleResponseAsync(invoiceId, sales, dbFactory, cancellationToken));
});

salesApi.MapPost("/{invoiceId:guid}/resume", async (
    Guid invoiceId,
    ICurrentSession session,
    IDbContextFactory<PosDbContext> dbFactory,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    var invoice = await GetInvoiceAccessInfoAsync(invoiceId, dbFactory, cancellationToken);
    var accessError = ValidateInvoiceMutationAccess(invoice, session.UserId);
    if (accessError is not null)
        return accessError;

    if (invoice!.Status != InvoiceStatus.Held)
        return Results.BadRequest(new ApiErrorResponse("Only held invoices can be resumed."));

    await sales.ResumeInvoiceAsync(invoiceId, cancellationToken);
    return Results.Ok(await BuildSaleLifecycleResponseAsync(invoiceId, sales, dbFactory, cancellationToken));
});

salesApi.MapPost("/{invoiceId:guid}/cancel", async (
    Guid invoiceId,
    ICurrentSession session,
    IDbContextFactory<PosDbContext> dbFactory,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    var invoice = await GetInvoiceAccessInfoAsync(invoiceId, dbFactory, cancellationToken);
    var accessError = ValidateInvoiceMutationAccess(invoice, session.UserId);
    if (accessError is not null)
        return accessError;

    if (invoice!.Status is not InvoiceStatus.Open and not InvoiceStatus.Held)
        return Results.BadRequest(new ApiErrorResponse("Only open or held invoices can be cancelled."));

    await sales.CancelInvoiceAsync(invoiceId, cancellationToken);
    return Results.Ok(await BuildSaleLifecycleResponseAsync(invoiceId, sales, dbFactory, cancellationToken));
});

salesApi.MapPost("/{invoiceId:guid}/refund", async (
    Guid invoiceId,
    ICurrentSession session,
    IDbContextFactory<PosDbContext> dbFactory,
    ISaleService sales,
    CancellationToken cancellationToken) =>
{
    var invoice = await GetInvoiceAccessInfoAsync(invoiceId, dbFactory, cancellationToken);
    var accessError = ValidateInvoiceMutationAccess(invoice, session.UserId);
    if (accessError is not null)
        return accessError;

    if (invoice!.Status != InvoiceStatus.Paid)
        return Results.BadRequest(new ApiErrorResponse("Only paid invoices can be refunded."));

    var result = await sales.RefundInvoiceAsync(invoiceId, cancellationToken);
    if (!result.Success)
        return Results.BadRequest(new ApiErrorResponse(result.Error ?? "Could not refund invoice."));

    return Results.Ok(await BuildSaleLifecycleResponseAsync(invoiceId, sales, dbFactory, cancellationToken));
}).RequireAuthorization(ProcessRefundsPolicy);

syncApi.MapPost("/invoices/push", async (
    ClaimsPrincipal user,
    InvoiceSyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    // A device token (background sync continuing after an employee logs out) has no "current user"
    // claim at all — the invoice's own recorded username is then the only signal for who rang up that
    // historical sale, and it is verified against real tenant users below rather than trusted outright.
    var isDeviceAuthenticated = string.Equals(user.FindFirstValue(TokenTypeClaim), DeviceTokenType, StringComparison.Ordinal);
    var authenticatedUserId = TryParseGuid(user.FindFirstValue(ClaimTypes.NameIdentifier));
    var authenticatedDeviceId = TryParseGuid(user.FindFirstValue(DeviceIdClaim));
    if (!isDeviceAuthenticated && authenticatedUserId is null)
        return Results.Unauthorized();

    if (request.Invoices.Count == 0)
        return Results.Ok(new InvoiceSyncPushResultDto(Array.Empty<InvoiceSyncInvoiceResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);

    var results = new List<InvoiceSyncInvoiceResultDto>(request.Invoices.Count);

    foreach (var incoming in request.Invoices)
    {
        try
        {
            if (incoming.SyncVersion <= 0)
            {
                results.Add(new InvoiceSyncInvoiceResultDto(incoming.InvoiceId, "Failed", 0, "SyncVersion must be positive."));
                continue;
            }

            // Tenant-scoped: an invoice line referencing another tenant's product id (however it got
            // there — a client bug, a replayed payload, or a deliberate guess) must be rejected exactly
            // like a missing product, never silently accepted as a cross-tenant reference.
            var serverProductIds = await db.Products
                .AsNoTracking()
                .Where(p => !p.IsDeleted && p.TenantId == tenantId)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);
            var missingProductId = incoming.Lines.Count > 0 && incoming.Lines
                .Select(x => x.ProductId)
                .Distinct()
                .Except(serverProductIds)
                .Any();

            if (incoming.Lines.Count > 0 && missingProductId)
            {
                results.Add(new InvoiceSyncInvoiceResultDto(incoming.InvoiceId, "Failed", 0, "One or more products do not exist on the server."));
                continue;
            }

            var invoiceUserId = await ResolveInvoiceUserIdAsync(db, tenantId, storeId.Value, authenticatedUserId, incoming.Username, cancellationToken);
            if (invoiceUserId is null)
            {
                results.Add(new InvoiceSyncInvoiceResultDto(incoming.InvoiceId, "Failed", 0, "Could not verify a tenant user for this invoice."));
                continue;
            }

            if (isDeviceAuthenticated && incoming.DeviceId is { } claimedDeviceId && authenticatedDeviceId is not null && claimedDeviceId != authenticatedDeviceId)
            {
                results.Add(new InvoiceSyncInvoiceResultDto(incoming.InvoiceId, "Failed", 0, "A device credential cannot push an invoice attributed to a different device."));
                continue;
            }

            var resolvedUserId = invoiceUserId.Value;
            var device = await GetOrCreateSyncDeviceAsync(db, tenantId, storeId.Value, incoming, cancellationToken);
            var existing = await db.Invoices
                .Include(i => i.Items)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == incoming.InvoiceId && i.StoreId == storeId.Value && !i.IsDeleted, cancellationToken);

            if (existing is null)
            {
                var created = CreateInvoiceFromSync(incoming, tenantId, storeId.Value, resolvedUserId, device.Id);
                db.Invoices.Add(created);
                await ReconcileInvoiceInventoryAsync(
                    db,
                    tenantId,
                    storeId.Value,
                    resolvedUserId,
                    created.Id,
                    incoming.UpdatedAt,
                    new Dictionary<Guid, decimal>(),
                    BuildInventoryEffect(created.Status, created.Items),
                    cancellationToken);
                results.Add(new InvoiceSyncInvoiceResultDto(incoming.InvoiceId, "Applied", incoming.SyncVersion, null));
                continue;
            }

            if (existing.SyncVersion > incoming.SyncVersion)
            {
                results.Add(new InvoiceSyncInvoiceResultDto(incoming.InvoiceId, "Conflict", existing.SyncVersion, "Server has a newer invoice version."));
                continue;
            }

            if (existing.SyncVersion == incoming.SyncVersion)
            {
                results.Add(new InvoiceSyncInvoiceResultDto(incoming.InvoiceId, "Skipped", existing.SyncVersion, null));
                continue;
            }

            var previousInventoryEffect = BuildInventoryEffect(existing.Status, existing.Items);
            ApplyInvoiceSnapshot(existing, incoming, resolvedUserId, device.Id);
            await ReconcileInvoiceInventoryAsync(
                db,
                tenantId,
                storeId.Value,
                resolvedUserId,
                existing.Id,
                incoming.UpdatedAt,
                previousInventoryEffect,
                BuildInventoryEffect(existing.Status, existing.Items),
                cancellationToken);
            results.Add(new InvoiceSyncInvoiceResultDto(incoming.InvoiceId, "Applied", incoming.SyncVersion, null));
        }
        catch (Exception ex)
        {
            results.Add(new InvoiceSyncInvoiceResultDto(incoming.InvoiceId, "Failed", 0, ex.Message));
        }
    }

    try
    {
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
    catch (DbUpdateConcurrencyException)
    {
        // A truly concurrent request (a lost-ack retry racing the original in-flight push, or two
        // devices' background sync passes overlapping) applied a conflicting write to the same
        // invoice(s) in this batch first — Invoice.SyncVersion is a concurrency token specifically so
        // this loses cleanly instead of silently double-applying a stock/financial effect. Roll back
        // (nothing in this batch commits, including any invoice in it that did not itself conflict —
        // the whole batch is safe to retry, matching this endpoint's existing per-batch atomicity) and
        // fail soft: the caller's InvoiceSyncService already treats a non-success response as "leave
        // these invoices unsynced, retry next pass," which is exactly correct here since the winning
        // request already durably committed the single effect.
        await tx.RollbackAsync(cancellationToken);
        return Results.Conflict(new ApiErrorResponse("One or more invoices in this batch were updated concurrently by another request. Retry this push."));
    }
    catch (DbUpdateException ex) when (POS.Api.Infrastructure.SyncConflictClassifier.IsRetryable(ex))
    {
        // Same reasoning as the concurrency-token path above, for the rarer case where a genuine race
        // manifests as a raw unique-constraint/serialization failure instead (e.g. two concurrent pushes
        // both deciding "this invoice doesn't exist yet" and both trying to insert it — a retry's fresh
        // read now finds the row and takes the update path instead) — still safe to retry as-is.
        await tx.RollbackAsync(cancellationToken);
        return Results.Conflict(new ApiErrorResponse("One or more invoices in this batch could not be saved due to a concurrent write. Retry this push."));
    }
    catch (DbUpdateException)
    {
        // T6 matrix requirement: do not treat every DbUpdateException as a retryable 409. A foreign-key,
        // check, or not-null violation surfacing only at save time (after every earlier per-item existence
        // check passed under this transaction's own snapshot — e.g. a referenced product was deleted by a
        // different request in the gap between this batch's check and its commit) is a real data problem,
        // not a race: retrying the identical payload will fail identically forever. The whole batch is
        // still rolled back atomically exactly like the retryable path above (see
        // Invoice_failed_push_rolls_back_every_write_in_the_batch_including_unrelated_invoices in
        // POS.Tests), but reported as permanent so the caller does not loop retrying something that can
        // never succeed unmodified.
        await tx.RollbackAsync(cancellationToken);
        return Results.UnprocessableEntity(new ApiErrorResponse("One or more invoices in this batch failed a database validation constraint and cannot be retried unmodified."));
    }

    return Results.Ok(new InvoiceSyncPushResultDto(results));
}).RequireAuthorization(SyncPolicy);

syncApi.MapGet("/invoices/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.Invoice, sinceSequence, take, tenantId, storeId.Value, includeGlobalRows: false, cancellationToken);
    var invoices = await ProjectInvoiceSyncInvoices(
            db.Invoices
                .AsNoTracking()
                .Where(i => i.StoreId == storeId.Value && !i.IsDeleted && changes.Select(c => c.EntityId).Contains(i.Id)))
        .ToListAsync(cancellationToken);

    var orderedInvoices = OrderByGuidSequence(changes, invoices, invoice => invoice.InvoiceId);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new InvoiceSyncPullResultDto(orderedInvoices, nextSinceVersion));
}).RequireAuthorization(SyncPolicy);

syncApi.MapPost("/cash-sessions/push", async (
    ClaimsPrincipal user,
    CashSessionSyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (request.Sessions.Count == 0)
        return Results.Ok(new CashSessionSyncPushResultDto(Array.Empty<CashSessionSyncItemResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);

    var results = new List<CashSessionSyncItemResultDto>(request.Sessions.Count);

    foreach (var incoming in request.Sessions)
    {
        try
        {
            if (incoming.SyncVersion <= 0)
            {
                results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Failed", 0, "SyncVersion must be positive."));
                continue;
            }

            if (!await db.Registers.AnyAsync(r => r.Id == incoming.RegisterId && r.StoreId == storeId.Value, cancellationToken))
            {
                results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Failed", 0, "Register does not exist on the server yet."));
                continue;
            }

            var openedByUserId = await ResolveCashActorUserIdAsync(db, tenantId, storeId.Value, incoming.OpenedByUserId, incoming.OpenedByUsername, cancellationToken);
            if (openedByUserId is null)
            {
                results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Failed", 0, "Could not verify a tenant user for OpenedByUsername."));
                continue;
            }

            Guid? closedByUserId = null;
            if (!string.IsNullOrWhiteSpace(incoming.ClosedByUsername) || incoming.ClosedByUserId is not null)
            {
                closedByUserId = await ResolveCashActorUserIdAsync(db, tenantId, storeId.Value, incoming.ClosedByUserId, incoming.ClosedByUsername, cancellationToken);
                if (closedByUserId is null)
                {
                    results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Failed", 0, "Could not verify a tenant user for ClosedByUsername."));
                    continue;
                }
            }

            var existing = await db.CashSessions.FirstOrDefaultAsync(s => s.Id == incoming.CashSessionId && s.StoreId == storeId.Value, cancellationToken);

            if (existing is null)
            {
                var created = new CashSession
                {
                    Id = incoming.CashSessionId,
                    TenantId = tenantId,
                    StoreId = storeId.Value,
                    RegisterId = incoming.RegisterId,
                    OpenedByUserId = openedByUserId.Value,
                    OpenedAt = incoming.OpenedAt,
                    OpeningCashAmount = incoming.OpeningCashAmount,
                    CurrencyCode = incoming.CurrencyCode,
                    Status = incoming.Status,
                    IsSharedSession = incoming.IsSharedSession,
                    ClosedByUserId = closedByUserId,
                    ClosedAt = incoming.ClosedAt,
                    ClosingCountedAmount = incoming.ClosingCountedAmount,
                    ExpectedCashAmount = incoming.ExpectedCashAmount,
                    DiscrepancyAmount = incoming.DiscrepancyAmount,
                    Notes = incoming.Notes,
                    IsSynced = true,
                    SyncVersion = incoming.SyncVersion,
                    CreatedAt = incoming.CreatedAt,
                    UpdatedAt = incoming.UpdatedAt,
                    IsDeleted = incoming.IsDeleted
                };
                db.CashSessions.Add(created);
                await ReconcileCashMovementsAsync(db, created.Id, tenantId, storeId.Value, incoming.Movements, cancellationToken);
                results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Applied", incoming.SyncVersion, null));
                continue;
            }

            // Movements are append-only and idempotent by Id — reconcile them unconditionally, before the
            // header's own version outcome is decided below, so a header Conflict/Skip can never discard a
            // legitimate movement another write already committed against this same session.
            await ReconcileCashMovementsAsync(db, existing.Id, tenantId, storeId.Value, incoming.Movements, cancellationToken);

            if (existing.SyncVersion > incoming.SyncVersion)
            {
                results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Conflict", existing.SyncVersion, "Server has a newer cash session version."));
                continue;
            }

            if (existing.SyncVersion == incoming.SyncVersion)
            {
                results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Skipped", existing.SyncVersion, null));
                continue;
            }

            // incoming.SyncVersion is strictly higher than what the server has recorded — ordinarily this
            // means "apply it." But a closed session's header is an immutable posted fact (tenant.md §6
            // rule 9): a higher-versioned payload that still claims to be reachable here can only mean a
            // device fell out of sync with the close itself (e.g. it never received the close before going
            // offline again), never a legitimate later edit, since a locally closed session accepts no
            // further application writes. Reject rather than reopen it or rewrite its closing figures.
            if (existing.Status == CashSessionStatus.Closed)
            {
                results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Conflict", existing.SyncVersion,
                    "Cash session is already closed; its header cannot be reopened or modified."));
                continue;
            }

            existing.RegisterId = incoming.RegisterId;
            existing.OpenedByUserId = openedByUserId.Value;
            existing.OpenedAt = incoming.OpenedAt;
            existing.OpeningCashAmount = incoming.OpeningCashAmount;
            existing.CurrencyCode = incoming.CurrencyCode;
            existing.Status = incoming.Status;
            existing.IsSharedSession = incoming.IsSharedSession;
            existing.ClosedByUserId = closedByUserId;
            existing.ClosedAt = incoming.ClosedAt;
            existing.ClosingCountedAmount = incoming.ClosingCountedAmount;
            existing.ExpectedCashAmount = incoming.ExpectedCashAmount;
            existing.DiscrepancyAmount = incoming.DiscrepancyAmount;
            existing.Notes = incoming.Notes;
            existing.IsSynced = true;
            existing.SyncVersion = incoming.SyncVersion;
            existing.UpdatedAt = incoming.UpdatedAt;
            existing.IsDeleted = incoming.IsDeleted;

            results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Applied", incoming.SyncVersion, null));
        }
        catch (Exception ex)
        {
            results.Add(new CashSessionSyncItemResultDto(incoming.CashSessionId, "Failed", 0, ex.Message));
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);

    return Results.Ok(new CashSessionSyncPushResultDto(results));
}).RequireAuthorization(SyncPolicy);

syncApi.MapGet("/cash-sessions/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.CashSession, sinceSequence, take, tenantId, storeId.Value, includeGlobalRows: false, cancellationToken);
    var sessionIds = changes.Select(c => c.EntityId).ToList();
    var sessions = await BuildCashSessionSyncDtosAsync(db, storeId.Value, sessionIds, cancellationToken);

    var orderedSessions = OrderByGuidSequence(changes, sessions, s => s.CashSessionId);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new CashSessionSyncPullResultDto(orderedSessions, nextSinceVersion));
}).RequireAuthorization(SyncPolicy);

syncApi.MapGet("/categories/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    // Category is a tenant-wide aggregate (StoreId is always null in its SyncChange rows — see
    // PosDbContext.CapturePendingSyncChanges), so this endpoint needs a real tenant context to scope by
    // — previously it took no ClaimsPrincipal at all, and GetGuidSyncChangesAsync's null-storeId branch
    // had no tenant filter either, so any authenticated caller could pull every tenant's categories.
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.Category, sinceSequence, take, tenantId, null, includeGlobalRows: false, cancellationToken);
    var categories = await ProjectCategorySyncItems(
            db.Categories
                .AsNoTracking()
                .Where(c => c.TenantId == tenantId && changes.Select(change => change.EntityId).Contains(c.Id)))
        .ToListAsync(cancellationToken);

    var orderedCategories = OrderByGuidSequence(changes, categories, category => category.CategoryId);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new CategorySyncPullResultDto(orderedCategories, nextSinceVersion));
}).RequireAuthorization(SyncPolicy);

syncApi.MapGet("/products/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.Product, sinceSequence, take, tenantId, null, includeGlobalRows: false, cancellationToken);
    var products = await ProjectProductSyncItems(
            db.Products
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId && changes.Select(change => change.EntityId).Contains(p.Id)),
            db,
            storeId.Value)
        .ToListAsync(cancellationToken);

    var orderedProducts = OrderByGuidSequence(changes, products, product => product.ProductId);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new ProductSyncPullResultDto(orderedProducts, nextSinceVersion));
}).RequireAuthorization(SyncPolicy);

syncApi.MapGet("/settings/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var changes = await GetKeySyncChangesAsync(db, SyncAggregateTypes.Setting, sinceSequence, take, tenantId, storeId.Value, includeGlobalRows: false, cancellationToken);
    var settings = await ProjectSettingSyncItems(
            db.Settings
                .AsNoTracking()
                .Where(s => s.StoreId == storeId.Value
                         && !s.Key.StartsWith("Sync.")
                         && changes.Select(change => change.EntityKey).Contains(s.Key)))
        .ToListAsync(cancellationToken);

    var orderedSettings = OrderByKeySequence(changes, settings, setting => setting.Key);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new SettingsSyncPullResultDto(orderedSettings, nextSinceVersion));
}).RequireAuthorization(SyncPolicy);

syncApi.MapGet("/users/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.User, sinceSequence, take, tenantId, storeId.Value, includeGlobalRows: false, cancellationToken);
    var users = await ProjectUserSyncItems(
            db.Users
                .AsNoTracking()
                .Where(u => u.StoreId == storeId.Value && changes.Select(change => change.EntityId).Contains(u.Id)))
        .ToListAsync(cancellationToken);

    var orderedUsers = OrderByGuidSequence(changes, users, syncedUser => syncedUser.UserId);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new UserSyncPullResultDto(orderedUsers, nextSinceVersion));
}).RequireAuthorization(SyncPolicy);

syncApi.MapGet("/devices/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.Device, sinceSequence, take, tenantId, storeId.Value, includeGlobalRows: false, cancellationToken);
    var devices = await ProjectDeviceSyncItems(
            db.Devices
                .AsNoTracking()
                .Where(d => d.StoreId == storeId.Value && changes.Select(change => change.EntityId).Contains(d.Id)))
        .ToListAsync(cancellationToken);

    var orderedDevices = OrderByGuidSequence(changes, devices, device => device.DeviceId);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new DeviceSyncPullResultDto(orderedDevices, nextSinceVersion));
}).RequireAuthorization(SyncPolicy);

syncApi.MapGet("/registers/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.Register, sinceSequence, take, tenantId, storeId.Value, includeGlobalRows: false, cancellationToken);
    var registers = await db.Registers
            .AsNoTracking()
            .Where(r => r.StoreId == storeId.Value && changes.Select(change => change.EntityId).Contains(r.Id))
            .Select(r => new RegisterSyncDto(r.Id, r.Number, r.Name, r.IsActive, r.SyncVersion, r.CreatedAt, r.UpdatedAt, r.IsDeleted))
        .ToListAsync(cancellationToken);

    var orderedRegisters = OrderByGuidSequence(changes, registers, register => register.RegisterId);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new RegisterSyncPullResultDto(orderedRegisters, nextSinceVersion));
}).RequireAuthorization(SyncPolicy);

syncApi.MapPost("/registers/push", async (
    ClaimsPrincipal user,
    RegisterSyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (request.Registers.Count == 0)
        return Results.Ok(new RegisterSyncPushResultDto(Array.Empty<RegisterSyncItemResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);

    var results = new List<RegisterSyncItemResultDto>(request.Registers.Count);

    foreach (var incoming in request.Registers)
    {
        try
        {
            var existing = await db.Registers.FirstOrDefaultAsync(r => r.Id == incoming.RegisterId && r.StoreId == storeId.Value, cancellationToken);
            if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
            {
                results.Add(new RegisterSyncItemResultDto(incoming.RegisterId, "Skipped", existing.UpdatedAt, null));
                continue;
            }

            if (existing is null)
            {
                db.Registers.Add(new Register
                {
                    Id = incoming.RegisterId,
                    TenantId = tenantId,
                    StoreId = storeId.Value,
                    Number = incoming.Number,
                    Name = incoming.Name,
                    IsActive = incoming.IsActive,
                    SyncVersion = incoming.SyncVersion,
                    CreatedAt = incoming.CreatedAt,
                    UpdatedAt = incoming.UpdatedAt,
                    IsDeleted = incoming.IsDeleted
                });
            }
            else
            {
                existing.Number = incoming.Number;
                existing.Name = incoming.Name;
                existing.IsActive = incoming.IsActive;
                existing.SyncVersion = incoming.SyncVersion;
                existing.UpdatedAt = incoming.UpdatedAt;
                existing.IsDeleted = incoming.IsDeleted;
            }

            results.Add(new RegisterSyncItemResultDto(incoming.RegisterId, "Applied", incoming.UpdatedAt, null));
        }
        catch (Exception ex)
        {
            results.Add(new RegisterSyncItemResultDto(incoming.RegisterId, "Failed", DateTime.UtcNow, ex.Message));
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);
    return Results.Ok(new RegisterSyncPushResultDto(results));
}).RequireAuthorization(ManageSettingsPolicy);

syncApi.MapGet("/audit-logs/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.AuditLog, sinceSequence, take, tenantId, storeId.Value, includeGlobalRows: false, cancellationToken);
    var auditLogs = await ProjectAuditLogSyncItems(
            db.AuditLogs
                .AsNoTracking()
                .Where(a => a.StoreId == storeId.Value
                         && !a.IsDeleted
                         && changes.Select(change => change.EntityId).Contains(a.Id)))
        .ToListAsync(cancellationToken);

    var orderedAuditLogs = OrderByGuidSequence(changes, auditLogs, auditLog => auditLog.Id);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new AuditLogSyncPullResultDto(orderedAuditLogs, nextSinceVersion));
}).RequireAuthorization(SyncPolicy);

syncApi.MapPost("/audit-logs/push", async (
    ClaimsPrincipal user,
    AuditLogSyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (request.AuditLogs.Count == 0)
        return Results.Ok(new AuditLogSyncPushResultDto(Array.Empty<AuditLogSyncItemResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);

    var results = new List<AuditLogSyncItemResultDto>(request.AuditLogs.Count);

    foreach (var incoming in request.AuditLogs)
    {
        try
        {
            var existing = await db.AuditLogs
                .FirstOrDefaultAsync(a => a.Id == incoming.Id && a.StoreId == storeId.Value && !a.IsDeleted, cancellationToken);

            if (existing is not null)
            {
                results.Add(new AuditLogSyncItemResultDto(incoming.Id, "Skipped", existing.CreatedAt, null));
                continue;
            }

            var userId = await ResolveAuditLogUserIdAsync(db, storeId.Value, incoming.Username, cancellationToken);
            db.AuditLogs.Add(new AuditLog
            {
                Id = incoming.Id,
                TenantId = tenantId,
                StoreId = storeId.Value,
                UserId = userId,
                Action = incoming.Action,
                EntityName = incoming.EntityName,
                EntityId = incoming.EntityId,
                Details = incoming.Details,
                CreatedAt = incoming.CreatedAt,
                UpdatedAt = incoming.CreatedAt,
                IsDeleted = false
            });

            results.Add(new AuditLogSyncItemResultDto(incoming.Id, "Applied", incoming.CreatedAt, null));
        }
        catch (Exception ex)
        {
            results.Add(new AuditLogSyncItemResultDto(incoming.Id, "Failed", DateTime.UtcNow, ex.Message));
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);
    return Results.Ok(new AuditLogSyncPushResultDto(results));
}).RequireAuthorization(SyncPolicy);

syncApi.MapPost("/devices/push", async (
    ClaimsPrincipal user,
    DeviceSyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (request.Devices.Count == 0)
        return Results.Ok(new DeviceSyncPushResultDto(Array.Empty<DeviceSyncItemResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);

    var results = new List<DeviceSyncItemResultDto>(request.Devices.Count);

    foreach (var incoming in request.Devices)
    {
        try
        {
            var existing = await UpsertDeviceSnapshotAsync(db, tenantId, storeId.Value, incoming, cancellationToken);
            if (existing.UpdatedAt > incoming.UpdatedAt)
            {
                results.Add(new DeviceSyncItemResultDto(incoming.DeviceId, "Skipped", existing.UpdatedAt, null));
                continue;
            }

            results.Add(new DeviceSyncItemResultDto(incoming.DeviceId, "Applied", incoming.UpdatedAt, null));
        }
        catch (Exception ex)
        {
            results.Add(new DeviceSyncItemResultDto(incoming.DeviceId, "Failed", DateTime.UtcNow, ex.Message));
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);
    return Results.Ok(new DeviceSyncPushResultDto(results));
}).RequireAuthorization(ManageSettingsPolicy);

syncApi.MapGet("/currency-policy/pull", async (
    ClaimsPrincipal user,
    string? sinceVersion,
    ICurrencyService currencies,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);
    var nextSequence = await db.SyncChanges
        .AsNoTracking()
        .Where(c => c.AggregateType == SyncAggregateTypes.CurrencyPolicy
                 && c.TenantId == tenantId
                 && c.Id > sinceSequence
                 && (c.StoreId == null || c.StoreId == storeId.Value))
        .OrderByDescending(c => c.Id)
        .Select(c => (long?)c.Id)
        .FirstOrDefaultAsync(cancellationToken);

    if (nextSequence is null)
        return Results.Ok(new CurrencyPolicySyncPullResultDto(null, normalizedSinceVersion));

    var currentUpdatedAt = await GetCurrencyPolicyUpdatedAtAsync(db, storeId.Value, cancellationToken);
    var policy = await currencies.GetStoreCurrencyPolicyAsync(cancellationToken);
    return Results.Ok(new CurrencyPolicySyncPullResultDto(
        new CurrencyPolicySyncDto(policy.StoreId, policy.BaseCurrencyId, currentUpdatedAt, policy.Currencies),
        FormatSequenceSinceVersion(nextSequence.Value)));
}).RequireAuthorization();

syncApi.MapPost("/currency-policy/push", async (
    ClaimsPrincipal user,
    CurrencyPolicySyncDto request,
    ICurrencyService currencies,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (request.StoreId != Guid.Empty && request.StoreId != storeId.Value)
        return Results.BadRequest(new ApiErrorResponse("StoreId does not match the authenticated store."));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var currentUpdatedAt = await GetCurrencyPolicyUpdatedAtAsync(db, storeId.Value, cancellationToken);
    if (currentUpdatedAt >= request.UpdatedAt)
        return Results.Ok(new CurrencyPolicySyncPushResultDto("Skipped", currentUpdatedAt, null));

    if (request.BaseCurrencyId == Guid.Empty)
        return Results.Ok(new CurrencyPolicySyncPushResultDto("Failed", currentUpdatedAt, "BaseCurrencyId is required."));

    if (request.Currencies.Count == 0)
        return Results.Ok(new CurrencyPolicySyncPushResultDto("Failed", currentUpdatedAt, "At least one currency snapshot is required."));

    if (!request.Currencies.Any(c => c.Id == request.BaseCurrencyId))
    {
        var ids = string.Join(",", request.Currencies.Select(c => c.Id.ToString("N")));
        return Results.Ok(new CurrencyPolicySyncPushResultDto("Failed", currentUpdatedAt, $"BaseCurrencyId {request.BaseCurrencyId:N} is missing from the currency snapshot: {ids}"));
    }

    var activeCurrencyIds = await db.Currencies
        .AsNoTracking()
        .Where(c => !c.IsDeleted)
        .Select(c => c.Id)
        .ToListAsync(cancellationToken);
    var missingCurrencyIds = activeCurrencyIds
        .Except(request.Currencies.Select(c => c.Id))
        .Select(id => id.ToString("N"))
        .ToList();
    if (missingCurrencyIds.Count > 0)
        return Results.Ok(new CurrencyPolicySyncPushResultDto("Failed", currentUpdatedAt, $"Currency policy snapshot is missing active currencies: {string.Join(",", missingCurrencyIds)}"));

    try
    {
        var rates = request.Currencies
            .Select(c => new CurrencyRateUpdateDto(c.Id, c.ExchangeRate))
            .ToList();

        await currencies.UpdateStoreCurrencyPolicyAsync(request.BaseCurrencyId, rates, cancellationToken);

        await using var afterDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var serverUpdatedAt = await GetCurrencyPolicyUpdatedAtAsync(afterDb, storeId.Value, cancellationToken);
        return Results.Ok(new CurrencyPolicySyncPushResultDto("Applied", serverUpdatedAt, null));
    }
    catch (Exception ex)
    {
        return Results.Ok(new CurrencyPolicySyncPushResultDto("Failed", currentUpdatedAt, ex.Message));
    }
}).RequireAuthorization(ManageSettingsPolicy);

syncApi.MapPost("/products/push", async (
    ClaimsPrincipal user,
    ProductSyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    var userId = TryParseGuid(user.FindFirstValue(ClaimTypes.NameIdentifier));
    if (storeId is null || userId is null)
        return Results.Unauthorized();

    if (request.Products.Count == 0)
        return Results.Ok(new ProductSyncPushResultDto(Array.Empty<ProductSyncItemResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);

    var results = new List<ProductSyncItemResultDto>(request.Products.Count);

    foreach (var incoming in request.Products)
    {
        try
        {
            await EnsureCategoryAsync(db, tenantId, incoming.CategoryId, incoming.CategoryName, incoming.CreatedAt, incoming.UpdatedAt, cancellationToken);

            var existing = await db.Products
                .FirstOrDefaultAsync(p => p.Id == incoming.ProductId && p.TenantId == tenantId, cancellationToken);

            if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
            {
                results.Add(new ProductSyncItemResultDto(incoming.ProductId, "Skipped", existing.UpdatedAt, null));
                continue;
            }

            if (existing is null)
            {
                existing = new Product
                {
                    Id = incoming.ProductId,
                    TenantId = tenantId,
                    CreatedAt = incoming.CreatedAt
                };
                db.Products.Add(existing);
            }

            ApplyProductSnapshot(existing, incoming);
            await UpsertProductInventoryAsync(db, tenantId, storeId.Value, userId.Value, incoming, cancellationToken);
            results.Add(new ProductSyncItemResultDto(incoming.ProductId, "Applied", incoming.UpdatedAt, null));
        }
        catch (Exception ex)
        {
            results.Add(new ProductSyncItemResultDto(incoming.ProductId, "Failed", DateTime.UtcNow, ex.Message));
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);
    return Results.Ok(new ProductSyncPushResultDto(results));
}).RequireAuthorization(ManageProductsPolicy);

syncApi.MapPost("/categories/push", async (
    ClaimsPrincipal user,
    CategorySyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    // Previously derived tenant via ResolveSoleTenantIdAsync (an explicit "no auth context available"
    // fallback documented in docs/TENANT_T0_AUDIT.md finding #6 — only correct for a single-tenant
    // deployment). The endpoint has required a real JWT (ManageProductsPolicy) all along, so its
    // claims are the actual, correct source of tenant scope — this was the T4 fix that note called for.
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (request.Categories.Count == 0)
        return Results.Ok(new CategorySyncPushResultDto(Array.Empty<CategorySyncItemResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);

    var results = new List<CategorySyncItemResultDto>(request.Categories.Count);

    foreach (var incoming in request.Categories)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(incoming.Name))
            {
                results.Add(new CategorySyncItemResultDto(incoming.CategoryId, "Failed", DateTime.UtcNow, "Category name is required."));
                continue;
            }

            var existing = await db.Categories.FirstOrDefaultAsync(c => c.Id == incoming.CategoryId && c.TenantId == tenantId, cancellationToken);
            if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
            {
                results.Add(new CategorySyncItemResultDto(incoming.CategoryId, "Skipped", existing.UpdatedAt, null));
                continue;
            }

            if (existing is null)
            {
                existing = new Category
                {
                    Id = incoming.CategoryId,
                    TenantId = tenantId
                };
                db.Categories.Add(existing);
            }

            ApplyCategorySnapshot(existing, incoming);
            results.Add(new CategorySyncItemResultDto(incoming.CategoryId, "Applied", incoming.UpdatedAt, null));
        }
        catch (Exception ex)
        {
            results.Add(new CategorySyncItemResultDto(incoming.CategoryId, "Failed", DateTime.UtcNow, ex.Message));
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);
    return Results.Ok(new CategorySyncPushResultDto(results));
}).RequireAuthorization(ManageProductsPolicy);

syncApi.MapPost("/settings/push", async (
    ClaimsPrincipal user,
    SettingsSyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (request.Settings.Count == 0)
        return Results.Ok(new SettingsSyncPushResultDto(Array.Empty<SettingSyncItemResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);

    var results = new List<SettingSyncItemResultDto>(request.Settings.Count);

    foreach (var incoming in request.Settings)
    {
        try
        {
            if (incoming.Key.StartsWith("Sync.", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new SettingSyncItemResultDto(incoming.Key, "Skipped", incoming.UpdatedAt, null));
                continue;
            }

            var existing = await db.Settings
                .FirstOrDefaultAsync(s => s.StoreId == storeId.Value && s.Key == incoming.Key, cancellationToken);

            if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
            {
                results.Add(new SettingSyncItemResultDto(incoming.Key, "Skipped", existing.UpdatedAt, null));
                continue;
            }

            if (existing is null)
            {
                db.Settings.Add(new Setting
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    StoreId = storeId.Value,
                    Key = incoming.Key,
                    Value = incoming.Value,
                    CreatedAt = incoming.CreatedAt,
                    UpdatedAt = incoming.UpdatedAt,
                    IsDeleted = incoming.IsDeleted
                });
            }
            else
            {
                existing.Value = incoming.Value;
                existing.CreatedAt = incoming.CreatedAt;
                existing.UpdatedAt = incoming.UpdatedAt;
                existing.IsDeleted = incoming.IsDeleted;
            }

            results.Add(new SettingSyncItemResultDto(incoming.Key, "Applied", incoming.UpdatedAt, null));
        }
        catch (Exception ex)
        {
            results.Add(new SettingSyncItemResultDto(incoming.Key, "Failed", DateTime.UtcNow, ex.Message));
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);
    return Results.Ok(new SettingsSyncPushResultDto(results));
}).RequireAuthorization(ManageSettingsPolicy);

syncApi.MapPost("/users/push", async (
    ClaimsPrincipal user,
    UserSyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    if (storeId is null)
        return Results.Unauthorized();

    if (request.Users.Count == 0)
        return Results.Ok(new UserSyncPushResultDto(Array.Empty<UserSyncItemResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
    var tenantId = await ResolveTenantIdAsync(db, storeId.Value, cancellationToken);

    var results = new List<UserSyncItemResultDto>(request.Users.Count);

    foreach (var incoming in request.Users)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(incoming.Username))
            {
                results.Add(new UserSyncItemResultDto(incoming.UserId, "Failed", DateTime.UtcNow, "Username is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(incoming.RoleName))
            {
                results.Add(new UserSyncItemResultDto(incoming.UserId, "Failed", DateTime.UtcNow, "Role name is required."));
                continue;
            }

            var existing = await FindUserBySyncIdentityAsync(db, storeId.Value, incoming.UserId, incoming.Username, cancellationToken);
            if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
            {
                results.Add(new UserSyncItemResultDto(incoming.UserId, "Skipped", existing.UpdatedAt, null));
                continue;
            }

            var roleId = await EnsureRoleAsync(db, tenantId, incoming.RoleName, incoming.RolePermissionsMask, incoming.RoleUpdatedAt, cancellationToken);

            if (existing is null)
            {
                existing = new User
                {
                    Id = incoming.UserId,
                    TenantId = tenantId,
                    StoreId = storeId.Value
                };
                db.Users.Add(existing);
            }

            ApplyUserSnapshot(existing, incoming, tenantId, roleId, storeId.Value);
            results.Add(new UserSyncItemResultDto(incoming.UserId, "Applied", incoming.UpdatedAt, null));
        }
        catch (Exception ex)
        {
            results.Add(new UserSyncItemResultDto(incoming.UserId, "Failed", DateTime.UtcNow, ex.Message));
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);
    return Results.Ok(new UserSyncPushResultDto(results));
}).RequireAuthorization(ManageUsersPolicy);

app.Run();

static bool ReadBooleanSetting(IConfiguration configuration, string key, bool defaultValue)
{
    var value = configuration[key];
    return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
}

static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

    var forwardLimitRaw = configuration["ReverseProxy:ForwardLimit"];
    if (int.TryParse(forwardLimitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var forwardLimit) && forwardLimit > 0)
        options.ForwardLimit = forwardLimit;

    foreach (var raw in configuration.GetSection("ReverseProxy:KnownProxies").GetChildren().Select(child => child.Value))
    {
        if (!string.IsNullOrWhiteSpace(raw) && IPAddress.TryParse(raw.Trim(), out var address))
            options.KnownProxies.Add(address);
    }

    foreach (var raw in configuration.GetSection("ReverseProxy:KnownNetworks").GetChildren().Select(child => child.Value))
    {
        if (string.IsNullOrWhiteSpace(raw))
            continue;

        var parts = raw.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            continue;

        if (IPAddress.TryParse(parts[0], out var prefix)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefixLength))
        {
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
        }
    }
}

static Guid? TryParseGuid(string? raw) => Guid.TryParse(raw, out var parsed) ? parsed : null;

static Permission GetPermissions(ClaimsPrincipal user) =>
    int.TryParse(user.FindFirstValue(PermissionsClaim), out var mask) ? (Permission)mask : Permission.None;

static bool FixedTimeEquals(string supplied, string expected)
{
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    // CryptographicOperations.FixedTimeEquals requires equal-length spans; a length mismatch alone
    // is not secret, so a short-circuit here doesn't leak anything a timing attack could exploit.
    return suppliedBytes.Length == expectedBytes.Length
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
}

static bool IsUnsafeSigningKey(string value) =>
    string.Equals(value, DevelopmentSigningKey, StringComparison.Ordinal)
    || value.Contains("change-me", StringComparison.OrdinalIgnoreCase)
    || value.Contains("replace", StringComparison.OrdinalIgnoreCase)
    || value.Contains("development", StringComparison.OrdinalIgnoreCase);

static bool TryParseSequenceSinceVersion(string? raw, out long sinceSequence, out string normalized)
{
    if (string.IsNullOrWhiteSpace(raw)
        || string.Equals(raw.Trim(), "0", StringComparison.Ordinal)
        || string.Equals(raw.Trim(), "0:", StringComparison.Ordinal)
        || string.Equals(raw.Trim(), "0:00000000000000000000000000000000", StringComparison.Ordinal)
        || raw.Trim().Contains(':', StringComparison.Ordinal))
    {
        sinceSequence = 0;
        normalized = FormatSequenceSinceVersion(0);
        return true;
    }

    if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSequence) || parsedSequence < 0)
    {
        sinceSequence = 0;
        normalized = FormatSequenceSinceVersion(0);
        return false;
    }

    sinceSequence = parsedSequence;
    normalized = FormatSequenceSinceVersion(parsedSequence);
    return true;
}

static string FormatSequenceSinceVersion(long sequence) =>
    sequence <= 0
        ? "0"
        : sequence.ToString(CultureInfo.InvariantCulture);

static async Task<List<SequenceGuidChange>> GetGuidSyncChangesAsync(
    PosDbContext db,
    string aggregateType,
    long sinceSequence,
    int take,
    Guid tenantId,
    Guid? storeId,
    bool includeGlobalRows,
    CancellationToken cancellationToken)
{
    var changes = await BuildSyncChangeQuery(db.SyncChanges.AsNoTracking(), aggregateType, sinceSequence, tenantId, storeId, includeGlobalRows)
        .Where(c => c.EntityId != null)
        .OrderBy(c => c.Id)
        .Take(take)
        .Select(c => new SequenceGuidChange(c.Id, c.EntityId!.Value))
        .ToListAsync(cancellationToken);

    return changes
        .GroupBy(c => c.EntityId)
        .Select(group => group.Last())
        .OrderBy(c => c.Sequence)
        .ToList();
}

static async Task<List<SequenceKeyChange>> GetKeySyncChangesAsync(
    PosDbContext db,
    string aggregateType,
    long sinceSequence,
    int take,
    Guid tenantId,
    Guid? storeId,
    bool includeGlobalRows,
    CancellationToken cancellationToken)
{
    var changes = await BuildSyncChangeQuery(db.SyncChanges.AsNoTracking(), aggregateType, sinceSequence, tenantId, storeId, includeGlobalRows)
        .Where(c => c.EntityKey != null)
        .OrderBy(c => c.Id)
        .Take(take)
        .Select(c => new SequenceKeyChange(c.Id, c.EntityKey!))
        .ToListAsync(cancellationToken);

    return changes
        .GroupBy(c => c.EntityKey, StringComparer.Ordinal)
        .Select(group => group.Last())
        .OrderBy(c => c.Sequence)
        .ToList();
}

/// <summary>
/// Every sync-change row carries a <see cref="SyncChange.TenantId"/> (never null), so filtering on it
/// unconditionally — not just via <paramref name="storeId"/> — is what actually enforces tenant
/// isolation for a tenant-wide aggregate (Category, Product, CurrencyPolicy — recorded with
/// <c>StoreId = null</c>). Before this filter existed, <paramref name="storeId"/> alone could not
/// distinguish "no store, tenant-wide" from "no store, ANY tenant": a real cross-tenant data leak where
/// one tenant's pull of a null-storeId aggregate silently included every other tenant's rows too. For a
/// store-scoped aggregate this is a no-op (a Store already belongs to exactly one Tenant), so this never
/// changes behavior for those — it only closes the gap for the null-storeId case.
/// </summary>
static IQueryable<SyncChange> BuildSyncChangeQuery(
    IQueryable<SyncChange> query,
    string aggregateType,
    long sinceSequence,
    Guid tenantId,
    Guid? storeId,
    bool includeGlobalRows)
{
    query = query.Where(c => c.AggregateType == aggregateType && c.Id > sinceSequence && c.TenantId == tenantId);

    if (storeId is null)
        return query.Where(c => c.StoreId == null);

    return includeGlobalRows
        ? query.Where(c => c.StoreId == storeId.Value || c.StoreId == null)
        : query.Where(c => c.StoreId == storeId.Value);
}

static List<TItem> OrderByGuidSequence<TItem>(
    IReadOnlyList<SequenceGuidChange> changes,
    IReadOnlyCollection<TItem> items,
    Func<TItem, Guid> itemId)
{
    var byId = items.ToDictionary(itemId);
    return changes
        .Where(change => byId.ContainsKey(change.EntityId))
        .Select(change => byId[change.EntityId])
        .ToList();
}

static List<TItem> OrderByKeySequence<TItem>(
    IReadOnlyList<SequenceKeyChange> changes,
    IReadOnlyCollection<TItem> items,
    Func<TItem, string> itemKey)
{
    var byKey = items.ToDictionary(itemKey, StringComparer.Ordinal);
    return changes
        .Where(change => byKey.ContainsKey(change.EntityKey))
        .Select(change => byKey[change.EntityKey])
        .ToList();
}

static IQueryable<InvoiceSyncInvoiceDto> ProjectInvoiceSyncInvoices(IQueryable<Invoice> invoices) =>
    invoices.Select(i => new InvoiceSyncInvoiceDto(
        i.Id,
        i.DeviceId,
        i.Device != null ? i.Device.Name : null,
        i.SyncVersion,
        i.Status,
        i.TotalAmount,
        i.TaxPercent,
        i.Currency,
        i.Notes,
        i.CreatedAt,
        i.UpdatedAt,
        i.Items
            .OrderBy(x => x.CreatedAt)
            .Select(x => new InvoiceSyncLineDto(
                x.Id,
                x.ProductId,
                x.Quantity,
                x.UnitPrice,
                x.DiscountPercent,
                x.LineTotal,
                x.CreatedAt,
                x.UpdatedAt,
                x.IsDeleted))
            .ToList(),
        i.Payments
            .OrderBy(x => x.CreatedAt)
            .Select(x => new InvoiceSyncPaymentDto(
                x.Id,
                x.Amount,
                x.Method,
                x.PaidAt,
                x.CreatedAt,
                x.UpdatedAt,
                x.IsDeleted))
            .ToList(),
        i.User != null ? i.User.Username : null));

static IQueryable<CategorySyncDto> ProjectCategorySyncItems(IQueryable<Category> categories) =>
    categories.Select(c => new CategorySyncDto(
        c.Id,
        c.Name,
        c.CreatedAt,
        c.UpdatedAt,
        c.IsDeleted));

static IQueryable<ProductSyncDto> ProjectProductSyncItems(
    IQueryable<Product> products,
    PosDbContext db,
    Guid storeId) =>
    products.Select(p => new ProductSyncDto(
        p.Id,
        p.Name,
        p.Barcode,
        p.Price,
        p.Cost,
        p.CategoryId,
        p.Category != null ? p.Category.Name : string.Empty,
        db.Inventories
            .Where(i => i.StoreId == storeId && i.ProductId == p.Id && !i.IsDeleted)
            .Select(i => (decimal?)i.Quantity)
            .FirstOrDefault() ?? 0m,
        p.IsActive,
        p.ImagePath,
        p.CreatedAt,
        p.UpdatedAt,
        p.IsDeleted));

static IQueryable<SettingSyncDto> ProjectSettingSyncItems(IQueryable<Setting> settings) =>
    settings.Select(s => new SettingSyncDto(
        s.Key,
        s.Value,
        s.CreatedAt,
        s.UpdatedAt,
        s.IsDeleted));

static IQueryable<UserSyncDto> ProjectUserSyncItems(IQueryable<User> users) =>
    users.Select(u => new UserSyncDto(
        u.Id,
        u.Username,
        u.PasswordHash,
        u.Role != null ? u.Role.Name : string.Empty,
        u.IsActive,
        u.CreatedAt,
        u.UpdatedAt,
        u.IsDeleted,
        u.Role != null ? u.Role.PermissionsMask : 0,
        u.Role != null ? u.Role.UpdatedAt : default));

static IQueryable<DeviceSyncDto> ProjectDeviceSyncItems(IQueryable<Device> devices) =>
    devices.Select(d => new DeviceSyncDto(
        d.Id,
        d.Name,
        d.SyncVersion,
        d.CreatedAt,
        d.UpdatedAt,
        d.IsDeleted,
        d.EnrollmentCodeHash,
        d.EnrolledAt,
        d.IsRevoked,
        d.RevokedAt));

static IQueryable<AuditLogSyncDto> ProjectAuditLogSyncItems(IQueryable<AuditLog> auditLogs) =>
    auditLogs.Select(a => new AuditLogSyncDto(
        a.Id,
        a.User != null ? a.User.Username : null,
        a.Action,
        a.EntityName,
        a.EntityId,
        a.Details,
        a.CreatedAt));

static async Task<DateTime> GetCurrencyPolicyUpdatedAtAsync(
    PosDbContext db,
    Guid storeId,
    CancellationToken cancellationToken)
{
    var store = await db.Stores
        .AsNoTracking()
        .Where(s => s.Id == storeId && !s.IsDeleted)
        .Select(s => new { s.TenantId, s.UpdatedAt })
        .FirstOrDefaultAsync(cancellationToken);

    var storeUpdatedAt = store?.UpdatedAt ?? DateTime.MinValue;

    var rateUpdatedAt = store is null
        ? DateTime.MinValue
        : await db.TenantCurrencyRates
            .AsNoTracking()
            .Where(r => r.TenantId == store.TenantId && !r.IsDeleted)
            .Select(r => (DateTime?)r.UpdatedAt)
            .MaxAsync(cancellationToken)
            ?? DateTime.MinValue;

    return storeUpdatedAt >= rateUpdatedAt ? storeUpdatedAt : rateUpdatedAt;
}

static async Task<Guid> ResolveTenantIdAsync(PosDbContext db, Guid storeId, CancellationToken cancellationToken) =>
    await db.Stores
        .AsNoTracking()
        .Where(s => s.Id == storeId)
        .Select(s => (Guid?)s.TenantId)
        .FirstOrDefaultAsync(cancellationToken)
    ?? Guid.Empty;

static async Task EnsureCategoryAsync(
    PosDbContext db,
    Guid tenantId,
    Guid categoryId,
    string categoryName,
    DateTime createdAt,
    DateTime updatedAt,
    CancellationToken cancellationToken)
{
    // Tenant-scoped lookup: a product push referencing another tenant's CategoryId (a nested-reference
    // isolation gap — the id alone was previously trusted) must never overwrite that foreign category's
    // Name/UpdatedAt in place. If the id genuinely belongs to another tenant, the insert below fails on
    // the primary key instead — a safe rejection, not a silent cross-tenant write.
    var existing = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId && c.TenantId == tenantId, cancellationToken);
    if (existing is null)
    {
        existing = new Category
        {
            Id = categoryId,
            TenantId = tenantId
        };
        db.Categories.Add(existing);
    }

    if (existing.UpdatedAt > updatedAt)
        return;

    ApplyCategorySnapshot(existing, new CategorySyncDto(categoryId, categoryName, createdAt, updatedAt, false));
}

static void ApplyCategorySnapshot(Category existing, CategorySyncDto incoming)
{
    existing.Name = string.IsNullOrWhiteSpace(incoming.Name)
        ? existing.Name
        : incoming.Name.Trim();
    existing.CreatedAt = incoming.CreatedAt;
    existing.UpdatedAt = incoming.UpdatedAt;
    existing.IsDeleted = incoming.IsDeleted;
}

static async Task<Device> UpsertDeviceSnapshotAsync(
    PosDbContext db,
    Guid tenantId,
    Guid storeId,
    DeviceSyncDto incoming,
    CancellationToken cancellationToken)
{
    Device? device = await db.Devices
        .FirstOrDefaultAsync(d => d.Id == incoming.DeviceId && d.StoreId == storeId, cancellationToken);

    if (device is null)
    {
        // No row under this exact Id — a device with the same display Name may already exist (the
        // unique (StoreId, Name) index allows only one row per name), so find it to update in place
        // rather than violating that constraint with a second row of the same name.
        device = await db.Devices
            .FirstOrDefaultAsync(d => d.StoreId == storeId && d.Name == incoming.Name && !d.IsDeleted, cancellationToken);
    }

    if (device is not null && device.UpdatedAt > incoming.UpdatedAt)
        return device;

    if (device is null)
    {
        device = new Device
        {
            Id = incoming.DeviceId,
            TenantId = tenantId,
            StoreId = storeId
        };
        db.Devices.Add(device);
    }
    // Never reassign an existing row's Id to match incoming.DeviceId, even when it was matched by
    // Name — every FK already pointing at this row's original Id (Invoices.DeviceId, Register
    // bindings, cash-session history) would silently dangle if we repointed it, and an
    // unauthenticated/buggy payload that merely shares a display Name must not be able to repoint an
    // existing device's identity. A name-matched row keeps its own Id and just gets its fields
    // refreshed below; only a genuinely new row is created under incoming.DeviceId.

    device.Name = string.IsNullOrWhiteSpace(incoming.Name) ? "Unknown Device" : incoming.Name.Trim();
    device.SyncVersion = incoming.SyncVersion <= 0 ? 1 : incoming.SyncVersion;
    device.CreatedAt = incoming.CreatedAt;
    device.UpdatedAt = incoming.UpdatedAt;
    device.IsDeleted = incoming.IsDeleted;
    device.EnrollmentCodeHash = incoming.EnrollmentCodeHash;
    device.EnrolledAt = incoming.EnrolledAt;
    // Sticky: revocation can never be undone by a generic snapshot upsert (a stale/compromised
    // client pushing IsRevoked=false with a newer UpdatedAt must not be able to un-revoke itself).
    // There is no "reactivate" feature yet — revoking is currently a one-way action.
    if (incoming.IsRevoked && !device.IsRevoked)
    {
        device.IsRevoked = true;
        device.RevokedAt = incoming.RevokedAt ?? DateTime.UtcNow;
    }
    return device;
}

static async Task<Guid?> ResolveAuditLogUserIdAsync(
    PosDbContext db,
    Guid storeId,
    string? username,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(username))
        return null;

    return await db.Users
        .AsNoTracking()
        .Where(u => u.StoreId == storeId && u.Username == username.Trim() && !u.IsDeleted)
        .Select(u => (Guid?)u.Id)
        .FirstOrDefaultAsync(cancellationToken);
}

static async Task<User?> FindUserBySyncIdentityAsync(
    PosDbContext db,
    Guid storeId,
    Guid userId,
    string username,
    CancellationToken cancellationToken)
{
    var existing = await db.Users
        .FirstOrDefaultAsync(u => u.Id == userId && u.StoreId == storeId, cancellationToken);

    if (existing is not null)
        return existing;

    return await db.Users
        .FirstOrDefaultAsync(u => u.StoreId == storeId && u.Username == username.Trim(), cancellationToken);
}

static async Task<Guid> EnsureRoleAsync(
    PosDbContext db,
    Guid tenantId,
    string roleName,
    int permissionsMask,
    DateTime roleUpdatedAt,
    CancellationToken cancellationToken)
{
    var normalizedRoleName = roleName.Trim();
    var existing = await db.Roles.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Name == normalizedRoleName, cancellationToken);
    if (existing is not null)
    {
        if (existing.IsDeleted)
            existing.IsDeleted = false;

        // Same UpdatedAt-precedence rule used for every other synced aggregate: only apply the
        // incoming permission mask if it is actually fresher than what's already local.
        if (roleUpdatedAt > existing.UpdatedAt)
        {
            existing.PermissionsMask = permissionsMask;
            existing.UpdatedAt = roleUpdatedAt;
        }

        return existing.Id;
    }

    var role = new Role
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Name = normalizedRoleName,
        PermissionsMask = permissionsMask,
        CreatedAt = roleUpdatedAt,
        UpdatedAt = roleUpdatedAt,
        IsDeleted = false
    };
    db.Roles.Add(role);
    return role.Id;
}

static void ApplyUserSnapshot(User existing, UserSyncDto incoming, Guid tenantId, Guid roleId, Guid storeId)
{
    existing.TenantId = tenantId;
    existing.Username = string.IsNullOrWhiteSpace(incoming.Username) ? existing.Username : incoming.Username.Trim();
    existing.NormalizedUsername = existing.Username.Trim().ToUpperInvariant();
    existing.PasswordHash = incoming.PasswordHash;
    existing.RoleId = roleId;
    existing.StoreId = storeId;
    existing.IsActive = incoming.IsActive;
    existing.CreatedAt = incoming.CreatedAt;
    existing.UpdatedAt = incoming.UpdatedAt;
    existing.IsDeleted = incoming.IsDeleted;
}

static void ApplyProductSnapshot(Product existing, ProductSyncDto incoming)
{
    existing.Name = incoming.Name;
    existing.Barcode = incoming.Barcode;
    existing.Price = incoming.Price;
    existing.Cost = incoming.Cost;
    existing.CategoryId = incoming.CategoryId;
    existing.IsWeighted = false;
    existing.IsActive = incoming.IsActive;
    existing.ImagePath = incoming.ImagePath;
    existing.CreatedAt = incoming.CreatedAt;
    existing.UpdatedAt = incoming.UpdatedAt;
    existing.IsDeleted = incoming.IsDeleted;
}

static async Task UpsertProductInventoryAsync(
    PosDbContext db,
    Guid tenantId,
    Guid storeId,
    Guid userId,
    ProductSyncDto incoming,
    CancellationToken cancellationToken)
{
    var inventory = await db.Inventories
        .FirstOrDefaultAsync(i => i.StoreId == storeId && i.ProductId == incoming.ProductId, cancellationToken);

    var previousQuantity = inventory?.Quantity ?? 0m;
    if (inventory is null)
    {
        inventory = new Inventory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProductId = incoming.ProductId,
            StoreId = storeId,
            Quantity = incoming.QuantityOnHand,
            CreatedAt = incoming.UpdatedAt,
            UpdatedAt = incoming.UpdatedAt,
            IsDeleted = false
        };
        db.Inventories.Add(inventory);
    }
    else
    {
        inventory.Quantity = incoming.QuantityOnHand;
        inventory.UpdatedAt = incoming.UpdatedAt;
        inventory.IsDeleted = false;
    }

    var delta = incoming.QuantityOnHand - previousQuantity;
    if (delta == 0)
        return;

    db.StockMovements.Add(new StockMovement
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ProductId = incoming.ProductId,
        StoreId = storeId,
        InventoryId = inventory.Id,
        UserId = userId,
        Type = StockMovementType.ManualSetAdjustment,
        QuantityDelta = delta,
        QuantityAfter = incoming.QuantityOnHand,
        Reference = "PRODUCT_SYNC_PUSH",
        Notes = "Inventory aligned from synced product snapshot.",
        CreatedAt = incoming.UpdatedAt,
        UpdatedAt = incoming.UpdatedAt
    });
}

static async Task<SaleSnapshotResponse> BuildSaleSnapshotAsync(Guid invoiceId, ISaleService sales, CancellationToken cancellationToken)
{
    var summary = await sales.GetInvoiceSummaryAsync(invoiceId, cancellationToken);
    var lines = await sales.GetCartLinesAsync(invoiceId, cancellationToken);
    return new SaleSnapshotResponse(invoiceId, summary, lines);
}

static async Task<SaleLifecycleResponse> BuildSaleLifecycleResponseAsync(
    Guid invoiceId,
    ISaleService sales,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken)
{
    var summary = await sales.GetInvoiceSummaryAsync(invoiceId, cancellationToken);
    var lines = await sales.GetCartLinesAsync(invoiceId, cancellationToken);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var status = await db.Invoices
        .AsNoTracking()
        .Where(i => i.Id == invoiceId && !i.IsDeleted)
        .Select(i => i.Status)
        .FirstAsync(cancellationToken);

    return new SaleLifecycleResponse(invoiceId, status.ToString(), summary, lines);
}

static async Task<InvoiceAccessInfo?> GetInvoiceAccessInfoAsync(
    Guid invoiceId,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken)
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    return await db.Invoices
        .AsNoTracking()
        .Where(i => i.Id == invoiceId && !i.IsDeleted)
        .Select(i => new InvoiceAccessInfo(i.Id, i.UserId, i.Status))
        .FirstOrDefaultAsync(cancellationToken);
}

static IResult? ValidateInvoiceMutationAccess(InvoiceAccessInfo? invoice, Guid currentUserId)
{
    if (invoice is null)
        return Results.NotFound(new ApiErrorResponse("Invoice not found."));

    if (invoice.UserId != currentUserId)
        return Results.Forbid();

    return null;
}

static Invoice CreateInvoiceFromSync(InvoiceSyncInvoiceDto incoming, Guid tenantId, Guid storeId, Guid userId, Guid deviceId)
{
    var invoice = new Invoice
    {
        Id = incoming.InvoiceId,
        TenantId = tenantId,
        StoreId = storeId,
        UserId = userId,
        DeviceId = deviceId,
        Status = incoming.Status,
        TotalAmount = incoming.TotalAmount,
        TaxPercent = incoming.TaxPercent,
        Currency = incoming.Currency,
        Notes = incoming.Notes,
        IsSynced = true,
        SyncVersion = incoming.SyncVersion,
        CreatedAt = incoming.CreatedAt,
        UpdatedAt = incoming.UpdatedAt
    };

    foreach (var line in incoming.Lines)
    {
        invoice.Items.Add(new InvoiceItem
        {
            Id = line.LineId,
            InvoiceId = incoming.InvoiceId,
            ProductId = line.ProductId,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            DiscountPercent = line.DiscountPercent,
            LineTotal = line.LineTotal,
            CreatedAt = line.CreatedAt,
            UpdatedAt = line.UpdatedAt,
            IsDeleted = line.IsDeleted
        });
    }

    foreach (var payment in incoming.Payments)
    {
        invoice.Payments.Add(new Payment
        {
            Id = payment.PaymentId,
            InvoiceId = incoming.InvoiceId,
            Amount = payment.Amount,
            Method = payment.Method,
            PaidAt = payment.PaidAt,
            CreatedAt = payment.CreatedAt,
            UpdatedAt = payment.UpdatedAt,
            IsDeleted = payment.IsDeleted
        });
    }

    return invoice;
}

static void ApplyInvoiceSnapshot(Invoice existing, InvoiceSyncInvoiceDto incoming, Guid userId, Guid deviceId)
{
    existing.UserId = userId;
    existing.DeviceId = deviceId;
    existing.Status = incoming.Status;
    existing.TotalAmount = incoming.TotalAmount;
    existing.TaxPercent = incoming.TaxPercent;
    existing.Currency = incoming.Currency;
    existing.Notes = incoming.Notes;
    existing.IsSynced = true;
    existing.SyncVersion = incoming.SyncVersion;
    existing.CreatedAt = incoming.CreatedAt;
    existing.UpdatedAt = incoming.UpdatedAt;

    var incomingLines = incoming.Lines.ToDictionary(x => x.LineId);
    foreach (var existingLine in existing.Items)
    {
        if (!incomingLines.TryGetValue(existingLine.Id, out var line))
        {
            existingLine.IsDeleted = true;
            existingLine.UpdatedAt = incoming.UpdatedAt;
            continue;
        }

        ApplyLineSnapshot(existingLine, line);
    }

    foreach (var line in incoming.Lines.Where(x => existing.Items.All(y => y.Id != x.LineId)))
    {
        existing.Items.Add(new InvoiceItem
        {
            Id = line.LineId,
            InvoiceId = existing.Id,
            ProductId = line.ProductId,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            DiscountPercent = line.DiscountPercent,
            LineTotal = line.LineTotal,
            CreatedAt = line.CreatedAt,
            UpdatedAt = line.UpdatedAt,
            IsDeleted = line.IsDeleted
        });
    }

    var incomingPayments = incoming.Payments.ToDictionary(x => x.PaymentId);
    foreach (var existingPayment in existing.Payments)
    {
        if (!incomingPayments.TryGetValue(existingPayment.Id, out var payment))
        {
            existingPayment.IsDeleted = true;
            existingPayment.UpdatedAt = incoming.UpdatedAt;
            continue;
        }

        ApplyPaymentSnapshot(existingPayment, payment);
    }

    foreach (var payment in incoming.Payments.Where(x => existing.Payments.All(y => y.Id != x.PaymentId)))
    {
        existing.Payments.Add(new Payment
        {
            Id = payment.PaymentId,
            InvoiceId = existing.Id,
            Amount = payment.Amount,
            Method = payment.Method,
            PaidAt = payment.PaidAt,
            CreatedAt = payment.CreatedAt,
            UpdatedAt = payment.UpdatedAt,
            IsDeleted = payment.IsDeleted
        });
    }
}

static async Task ReconcileInvoiceInventoryAsync(
    PosDbContext db,
    Guid tenantId,
    Guid storeId,
    Guid userId,
    Guid invoiceId,
    DateTime changedAt,
    IReadOnlyDictionary<Guid, decimal> previousEffect,
    IReadOnlyDictionary<Guid, decimal> nextEffect,
    CancellationToken cancellationToken)
{
    var productIds = previousEffect.Keys
        .Concat(nextEffect.Keys)
        .Distinct()
        .ToArray();

    if (productIds.Length == 0)
        return;

    var inventories = await db.Inventories
        .Where(i => i.StoreId == storeId && productIds.Contains(i.ProductId) && !i.IsDeleted)
        .ToDictionaryAsync(i => i.ProductId, cancellationToken);

    foreach (var productId in productIds)
    {
        var previousQuantityDelta = previousEffect.GetValueOrDefault(productId);
        var nextQuantityDelta = nextEffect.GetValueOrDefault(productId);
        var delta = nextQuantityDelta - previousQuantityDelta;
        if (delta == 0)
            continue;

        decimal quantityAfter;
        if (!inventories.TryGetValue(productId, out var inventory))
        {
            // A brand-new inventory row has nothing to race against yet, so a direct set is safe.
            inventory = new Inventory
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProductId = productId,
                StoreId = storeId,
                Quantity = delta,
                CreatedAt = changedAt,
                UpdatedAt = changedAt
            };
            db.Inventories.Add(inventory);
            inventories[productId] = inventory;
            quantityAfter = inventory.Quantity;
        }
        else
        {
            // Apply as an atomic DB-level increment, not an in-memory read-modify-write, so two
            // concurrent requests reconciling different invoices for the same product (e.g. two
            // offline devices whose sales both push around the same time) cannot lose one delta to
            // the other. inventory.Quantity is then re-synced from the DB but excluded from this
            // batch's own final SaveChangesAsync, so that later call does not redundantly (and
            // non-atomically) overwrite what this statement already committed.
            await db.Inventories
                .Where(i => i.Id == inventory.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Quantity, i => i.Quantity + delta)
                    .SetProperty(i => i.UpdatedAt, changedAt), cancellationToken);

            quantityAfter = await db.Inventories
                .Where(i => i.Id == inventory.Id)
                .Select(i => i.Quantity)
                .FirstAsync(cancellationToken);

            inventory.Quantity = quantityAfter;
            inventory.UpdatedAt = changedAt;
            db.Entry(inventory).Property(i => i.Quantity).IsModified = false;
            db.Entry(inventory).Property(i => i.UpdatedAt).IsModified = false;
        }

        // Central stock going negative here means two offline devices' completed sales could not
        // both be honored against the same physical stock — both sales are preserved and applied
        // exactly once (never discarded, clamped, or overwritten); the shortfall is recorded as a
        // visible discrepancy for manager review instead. Strict global stock availability is not
        // guaranteed while devices are offline — see docs/SYNC_STRATEGY.md.
        var isDiscrepancy = quantityAfter < 0m;

        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProductId = productId,
            StoreId = storeId,
            InventoryId = inventory.Id,
            InvoiceId = invoiceId,
            UserId = userId,
            Type = delta < 0m ? StockMovementType.Sale : StockMovementType.Refund,
            QuantityDelta = delta,
            QuantityAfter = quantityAfter,
            Reference = FormatInvoiceReference(invoiceId),
            Notes = delta < 0m
                ? "Inventory reduced while reconciling a synced invoice snapshot."
                : "Inventory increased while reconciling a synced invoice snapshot.",
            IsDiscrepancy = isDiscrepancy,
            CreatedAt = changedAt,
            UpdatedAt = changedAt
        });

        if (isDiscrepancy)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StoreId = storeId,
                UserId = userId,
                Action = "StockDiscrepancyDetected",
                EntityName = "Product",
                EntityId = productId,
                Details = $"Reconciling invoice {FormatInvoiceReference(invoiceId)} drove stock to {quantityAfter:0.####} (below zero). Two or more offline sales likely exceeded available stock before reconnecting.",
                CreatedAt = changedAt,
                UpdatedAt = changedAt
            });
        }
    }
}

static IReadOnlyDictionary<Guid, decimal> BuildInventoryEffect(InvoiceStatus status, IEnumerable<InvoiceItem> lines)
{
    if (status != InvoiceStatus.Paid)
        return new Dictionary<Guid, decimal>();

    return lines
        .Where(line => !line.IsDeleted)
        .GroupBy(line => line.ProductId)
        .ToDictionary(group => group.Key, group => -group.Sum(line => line.Quantity));
}

static string FormatInvoiceReference(Guid invoiceId) => invoiceId.ToString("N")[..12].ToUpperInvariant();

static void ApplyLineSnapshot(InvoiceItem existingLine, InvoiceSyncLineDto line)
{
    existingLine.ProductId = line.ProductId;
    existingLine.Quantity = line.Quantity;
    existingLine.UnitPrice = line.UnitPrice;
    existingLine.DiscountPercent = line.DiscountPercent;
    existingLine.LineTotal = line.LineTotal;
    existingLine.CreatedAt = line.CreatedAt;
    existingLine.UpdatedAt = line.UpdatedAt;
    existingLine.IsDeleted = line.IsDeleted;
}

static void ApplyPaymentSnapshot(Payment existingPayment, InvoiceSyncPaymentDto payment)
{
    existingPayment.Amount = payment.Amount;
    existingPayment.Method = payment.Method;
    existingPayment.PaidAt = payment.PaidAt;
    existingPayment.CreatedAt = payment.CreatedAt;
    existingPayment.UpdatedAt = payment.UpdatedAt;
    existingPayment.IsDeleted = payment.IsDeleted;
}

// Every invoice must be keyed by a Box (Device) — when the pushed invoice carries no device
// identity at all, we resolve/create a per-store fallback "Unknown Device" row instead of
// leaving DeviceId unset, since Invoice.DeviceId is required. Routed separately from
// UpsertDeviceSnapshotAsync's Id/Name matching so a missing device identity never generates a
// fresh random Id on every call (which would otherwise keep rebinding the fallback device's Id).
static async Task<Device> GetOrCreateSyncDeviceAsync(
    PosDbContext db,
    Guid tenantId,
    Guid storeId,
    InvoiceSyncInvoiceDto incoming,
    CancellationToken cancellationToken)
{
    if (incoming.DeviceId is null && string.IsNullOrWhiteSpace(incoming.DeviceName))
        return await GetOrCreateFallbackDeviceAsync(db, tenantId, storeId, incoming.UpdatedAt, cancellationToken);

    return await UpsertDeviceSnapshotAsync(
        db,
        tenantId,
        storeId,
        new DeviceSyncDto(
            incoming.DeviceId ?? Guid.NewGuid(),
            string.IsNullOrWhiteSpace(incoming.DeviceName) ? "Unknown Device" : incoming.DeviceName.Trim(),
            1,
            incoming.CreatedAt,
            incoming.UpdatedAt,
            false),
        cancellationToken);
}

static async Task<Device> GetOrCreateFallbackDeviceAsync(
    PosDbContext db,
    Guid tenantId,
    Guid storeId,
    DateTime now,
    CancellationToken cancellationToken)
{
    const string fallbackName = "Unknown Device";
    var existing = await db.Devices
        .FirstOrDefaultAsync(d => d.StoreId == storeId && d.Name == fallbackName && !d.IsDeleted, cancellationToken);
    if (existing is not null)
        return existing;

    var created = new Device
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        StoreId = storeId,
        Name = fallbackName,
        SyncVersion = 1,
        CreatedAt = now,
        UpdatedAt = now
    };
    db.Devices.Add(created);
    return created;
}

// The pushed invoice's own recorded username is who actually rang up that historical sale — locally
// authenticated at creation time — and stays authoritative even when whoever/whatever is running this
// sync pass right now is someone else (a different cashier, an unattended device credential, or a
// later online reconciliation). Trusting it outright was the real gap (tenant.md: "the server must
// validate authorization rather than trust a submitted UserId") — fixed by requiring it to resolve to
// a genuine, active user of the SAME tenant/store, and rejecting the invoice outright (never silently
// re-attributing it to whoever happens to be pushing) when it does not.
static async Task<Guid?> ResolveInvoiceUserIdAsync(
    PosDbContext db,
    Guid tenantId,
    Guid storeId,
    Guid? authenticatedUserId,
    string? username,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(username))
        return authenticatedUserId;

    // Present but unresolvable (deleted/renamed/inactive user, cross-tenant string, client bug) is
    // deliberately NOT a fallback-to-caller case: silently reattributing the sale to whoever happens
    // to be pushing right now would be the exact misattribution this validation exists to prevent.
    return await db.Users
        .AsNoTracking()
        .Where(u => u.TenantId == tenantId && u.StoreId == storeId && u.Username == username.Trim() && u.IsActive && !u.IsDeleted)
        .Select(u => (Guid?)u.Id)
        .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Resolves a cash-session actor (opener/closer/performer/approver) by username, scoped to the
/// pushing tenant/store. Never falls back to any other identity — null means "reject," matching the same
/// non-negotiable rule <see cref="ResolveInvoiceUserIdAsync"/> applies to a present-but-unresolvable
/// invoice username.</summary>
/// <summary>
/// Resolves a cash-session actor (opener/closer/performer/approver). Tries the claimed <paramref
/// name="userIdHint"/> first — never trusted on its own, only accepted once it is verified to belong to a
/// real, active, non-deleted user of this same tenant/store — so a later username change (renaming the
/// same person) can never strand an offline device's pending push. Falls back to a username match for
/// degenerate/pre-migration payloads that carry no id. Returns null (never a fallback to any other
/// identity) when neither resolves, so the caller can skip rather than misattribute.
/// </summary>
static async Task<Guid?> ResolveCashActorUserIdAsync(
    PosDbContext db,
    Guid tenantId,
    Guid storeId,
    Guid? userIdHint,
    string? username,
    CancellationToken cancellationToken)
{
    if (userIdHint is { } id && id != Guid.Empty)
    {
        var byId = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == id && u.TenantId == tenantId && u.StoreId == storeId && u.IsActive && !u.IsDeleted)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (byId is not null)
            return byId;
    }

    if (string.IsNullOrWhiteSpace(username))
        return null;

    return await db.Users
        .AsNoTracking()
        .Where(u => u.TenantId == tenantId && u.StoreId == storeId && u.Username == username.Trim() && u.IsActive && !u.IsDeleted)
        .Select(u => (Guid?)u.Id)
        .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>
/// A resolved approver id is not itself proof of authorization — verifies the approver is a different
/// person from the performer and currently holds <see cref="Permission.ManageSettings"/> (the same rule
/// <c>CashSessionService.RecordManualMovementAsync</c> enforces for a local write), so a sync payload can
/// never smuggle in a cash-out "approved" by an unauthorized or self-approving user.
/// </summary>
static async Task<bool> IsAuthorizedCashApproverAsync(PosDbContext db, Guid approverUserId, Guid performerUserId, CancellationToken cancellationToken)
{
    if (approverUserId == performerUserId)
        return false;

    var permissions = await db.Users
        .AsNoTracking()
        .Where(u => u.Id == approverUserId && u.IsActive && !u.IsDeleted)
        .Select(u => (int?)u.Role!.PermissionsMask)
        .FirstOrDefaultAsync(cancellationToken);

    return permissions is not null && ((Permission)permissions.Value).HasFlag(Permission.ManageSettings);
}

/// <summary>Inserts whichever incoming movements aren't already present by Id — see the matching client-
/// side comment in InvoiceSyncService.cs for why append-only movements need no update/remove case, and
/// why an unresolved actor/approver or missing Invoice/Payment reference skips just that one movement
/// rather than failing the whole session (whose own header, including the authoritative
/// ExpectedCashAmount/DiscrepancyAmount computed once at close time, still applies).</summary>
static async Task ReconcileCashMovementsAsync(
    PosDbContext db,
    Guid cashSessionId,
    Guid tenantId,
    Guid storeId,
    IReadOnlyList<CashMovementSyncDto> incomingMovements,
    CancellationToken cancellationToken)
{
    if (incomingMovements.Count == 0)
        return;

    var incomingIds = incomingMovements.Select(m => m.MovementId).ToList();
    var existingIds = (await db.CashMovements
            .AsNoTracking()
            .Where(m => m.CashSessionId == cashSessionId && incomingIds.Contains(m.Id))
            .Select(m => m.Id)
            .ToListAsync(cancellationToken))
        .ToHashSet();

    foreach (var incoming in incomingMovements)
    {
        if (existingIds.Contains(incoming.MovementId))
            continue;

        if (incoming.InvoiceId is { } invoiceId && !await db.Invoices.AnyAsync(i => i.Id == invoiceId, cancellationToken))
            continue;

        if (incoming.PaymentId is { } paymentId && !await db.Payments.AnyAsync(p => p.Id == paymentId, cancellationToken))
            continue;

        var performedByUserId = await ResolveCashActorUserIdAsync(db, tenantId, storeId, incoming.PerformedByUserId, incoming.PerformedByUsername, cancellationToken);
        if (performedByUserId is null)
            continue;

        Guid? approvedByUserId = null;
        if (!string.IsNullOrWhiteSpace(incoming.ApprovedByUsername) || incoming.ApprovedByUserId is not null)
        {
            approvedByUserId = await ResolveCashActorUserIdAsync(db, tenantId, storeId, incoming.ApprovedByUserId, incoming.ApprovedByUsername, cancellationToken);
            if (approvedByUserId is null)
                continue;

            // A resolved approver id is not itself proof of authorization — an unauthorized or
            // self-approving "approval" must never be silently accepted into the ledger.
            if (!await IsAuthorizedCashApproverAsync(db, approvedByUserId.Value, performedByUserId.Value, cancellationToken))
                continue;
        }

        db.CashMovements.Add(new CashMovement
        {
            Id = incoming.MovementId,
            TenantId = tenantId,
            CashSessionId = cashSessionId,
            Type = incoming.Type,
            Amount = incoming.Amount,
            Method = incoming.Method,
            CurrencyCode = incoming.CurrencyCode,
            InvoiceId = incoming.InvoiceId,
            PaymentId = incoming.PaymentId,
            PerformedByUserId = performedByUserId.Value,
            ApprovedByUserId = approvedByUserId,
            Notes = incoming.Notes,
            CreatedAt = incoming.CreatedAt,
            UpdatedAt = incoming.CreatedAt
        });
    }
}

static async Task<List<CashSessionSyncDto>> BuildCashSessionSyncDtosAsync(
    PosDbContext db,
    Guid storeId,
    IReadOnlyCollection<Guid> sessionIds,
    CancellationToken cancellationToken)
{
    var sessions = await db.CashSessions
        .AsNoTracking()
        .Where(s => s.StoreId == storeId && sessionIds.Contains(s.Id))
        .ToListAsync(cancellationToken);

    if (sessions.Count == 0)
        return new List<CashSessionSyncDto>();

    var movements = await db.CashMovements
        .AsNoTracking()
        .Where(m => sessionIds.Contains(m.CashSessionId) && !m.IsDeleted)
        .ToListAsync(cancellationToken);

    var userIds = sessions.Select(s => s.OpenedByUserId)
        .Concat(sessions.Where(s => s.ClosedByUserId.HasValue).Select(s => s.ClosedByUserId!.Value))
        .Concat(movements.Select(m => m.PerformedByUserId))
        .Concat(movements.Where(m => m.ApprovedByUserId.HasValue).Select(m => m.ApprovedByUserId!.Value))
        .Distinct()
        .ToList();
    var usernames = await db.Users.AsNoTracking()
        .Where(u => userIds.Contains(u.Id))
        .ToDictionaryAsync(u => u.Id, u => u.Username, cancellationToken);

    var movementsBySession = movements.GroupBy(m => m.CashSessionId).ToDictionary(g => g.Key, g => g.ToList());

    return sessions.Select(s => new CashSessionSyncDto(
        s.Id,
        s.RegisterId,
        s.SyncVersion,
        s.OpenedByUserId,
        usernames.GetValueOrDefault(s.OpenedByUserId, string.Empty),
        s.OpenedAt,
        s.OpeningCashAmount,
        s.CurrencyCode,
        s.Status,
        s.IsSharedSession,
        s.ClosedByUserId,
        s.ClosedByUserId.HasValue ? usernames.GetValueOrDefault(s.ClosedByUserId.Value) : null,
        s.ClosedAt,
        s.ClosingCountedAmount,
        s.ExpectedCashAmount,
        s.DiscrepancyAmount,
        s.Notes,
        s.CreatedAt,
        s.UpdatedAt,
        s.IsDeleted,
        (movementsBySession.TryGetValue(s.Id, out var sessionMovements) ? sessionMovements : new List<CashMovement>())
            .OrderBy(m => m.CreatedAt)
            .Select(m => new CashMovementSyncDto(
                m.Id,
                m.Type,
                m.Amount,
                m.Method,
                m.CurrencyCode,
                m.InvoiceId,
                m.PaymentId,
                m.PerformedByUserId,
                usernames.GetValueOrDefault(m.PerformedByUserId, string.Empty),
                m.ApprovedByUserId,
                m.ApprovedByUserId.HasValue ? usernames.GetValueOrDefault(m.ApprovedByUserId.Value) : null,
                m.Notes,
                m.CreatedAt))
            .ToList()))
        .ToList();
}

static IResult MapSaleError(Exception exception)
{
    var message = exception.Message;
    return message switch
    {
        "Invoice not found." => Results.NotFound(new ApiErrorResponse(message)),
        "Line not found." => Results.NotFound(new ApiErrorResponse(message)),
        _ => Results.BadRequest(new ApiErrorResponse(message))
    };
}

internal sealed record SequenceGuidChange(long Sequence, Guid EntityId);
internal sealed record SequenceKeyChange(long Sequence, string EntityKey);
internal sealed record LoginRequest(string Username, string Password, string? TenantSlug = null);
internal sealed record DeviceTokenRequest(Guid DeviceId, string Secret);
internal sealed record DeviceTokenResponse(string AccessToken, DateTime ExpiresAtUtc);
internal sealed record ProvisionTenantRequest(string TenantName, string TenantSlug, string StoreName, string AdminUsername, string AdminPassword, string BaseCurrencyCode = "ILS");
internal sealed record ProvisionTenantResponse(Guid TenantId, Guid StoreId, Guid AdminUserId);
internal sealed record AddSaleLineRequest(Guid ProductId, decimal Quantity);
internal sealed record UpdateSaleLineQuantityRequest(decimal Quantity);
internal sealed record UpdateSaleLineDiscountRequest(decimal DiscountPercent);
internal sealed record UpdateSaleTaxRequest(decimal TaxPercent);
internal sealed record CompleteCashSaleRequest(decimal CashTendered);
internal sealed record LoginResponse(
    string AccessToken,
    DateTime ExpiresAtUtc,
    Guid UserId,
    Guid StoreId,
    string Username,
    string RoleName,
    string BaseCurrencyCode,
    string? CurrencySymbol);
internal sealed record CurrentUserResponse(
    Guid? UserId,
    Guid? StoreId,
    string Username,
    string RoleName,
    string BaseCurrencyCode,
    string? CurrencySymbol);
internal sealed record SaleSnapshotResponse(
    Guid InvoiceId,
    InvoiceSummaryDto Summary,
    IReadOnlyList<CartLineDto> Lines);
internal sealed record SaleLifecycleResponse(
    Guid InvoiceId,
    string Status,
    InvoiceSummaryDto Summary,
    IReadOnlyList<CartLineDto> Lines);
internal sealed record CompleteCashSaleResponse(ReceiptDto Receipt);
internal sealed record ApiErrorResponse(string ErrorMessage);
internal sealed record InvoiceAccessInfo(Guid InvoiceId, Guid UserId, InvoiceStatus Status);

public partial class Program
{
}