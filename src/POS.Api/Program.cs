using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
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

const string StoreIdClaim = "store_id";
const string CurrencyCodeClaim = "currency_code";
const string CurrencySymbolClaim = "currency_symbol";
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
builder.Services.AddAuthorization();
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

app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.RequestServices.GetRequiredService<ICurrentSession>() is ApiCurrentSession session)
    {
        session.Clear();

        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var userId = TryParseGuid(user.FindFirstValue(ClaimTypes.NameIdentifier));
            var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
            var username = user.FindFirstValue(ClaimTypes.Name);
            var roleName = user.FindFirstValue(ClaimTypes.Role);
            var baseCurrencyCode = user.FindFirstValue(CurrencyCodeClaim);

            if (userId.HasValue
                && storeId.HasValue
                && !string.IsNullOrWhiteSpace(username)
                && !string.IsNullOrWhiteSpace(roleName)
                && !string.IsNullOrWhiteSpace(baseCurrencyCode))
            {
                session.Set(
                    userId.Value,
                    storeId.Value,
                    username,
                    roleName,
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

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    IAuthService authService,
    ICurrentSession session,
    CancellationToken cancellationToken) =>
{
    var result = await authService.LoginAsync(request.Username, cancellationToken);
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
        new(StoreIdClaim, session.StoreId.ToString()),
        new(CurrencyCodeClaim, session.BaseCurrencyCode)
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
});

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

var syncApi = app.MapGroup("/api/sync")
    .RequireAuthorization();

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
});

syncApi.MapPost("/invoices/push", async (
    ClaimsPrincipal user,
    InvoiceSyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var storeId = TryParseGuid(user.FindFirstValue(StoreIdClaim));
    var userId = TryParseGuid(user.FindFirstValue(ClaimTypes.NameIdentifier));
    if (storeId is null || userId is null)
        return Results.Unauthorized();

    if (request.Invoices.Count == 0)
        return Results.Ok(new InvoiceSyncPushResultDto(Array.Empty<InvoiceSyncInvoiceResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

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

            var serverProductIds = await db.Products
                .AsNoTracking()
                .Where(p => !p.IsDeleted)
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

            var invoiceUserId = await ResolveInvoiceUserIdAsync(db, storeId.Value, userId.Value, incoming.Username, cancellationToken);
            var device = await GetOrCreateSyncDeviceAsync(db, storeId.Value, incoming, cancellationToken);
            var existing = await db.Invoices
                .Include(i => i.Items)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == incoming.InvoiceId && i.StoreId == storeId.Value && !i.IsDeleted, cancellationToken);

            if (existing is null)
            {
                var created = CreateInvoiceFromSync(incoming, storeId.Value, invoiceUserId, device?.Id);
                db.Invoices.Add(created);
                await ReconcileInvoiceInventoryAsync(
                    db,
                    storeId.Value,
                    invoiceUserId,
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
            ApplyInvoiceSnapshot(existing, incoming, invoiceUserId, device?.Id);
            await ReconcileInvoiceInventoryAsync(
                db,
                storeId.Value,
                invoiceUserId,
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

    await db.SaveChangesAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);

    return Results.Ok(new InvoiceSyncPushResultDto(results));
});

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
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.Invoice, sinceSequence, take, storeId.Value, includeGlobalRows: false, cancellationToken);
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
});

syncApi.MapGet("/categories/pull", async (
    string? sinceVersion,
    int? batchSize,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    if (!TryParseSequenceSinceVersion(sinceVersion, out var sinceSequence, out var normalizedSinceVersion))
        return Results.BadRequest(new ApiErrorResponse("sinceVersion is invalid."));

    var take = Math.Clamp(batchSize ?? 25, 1, 100);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.Category, sinceSequence, take, null, includeGlobalRows: false, cancellationToken);
    var categories = await ProjectCategorySyncItems(
            db.Categories
                .AsNoTracking()
                .Where(c => changes.Select(change => change.EntityId).Contains(c.Id)))
        .ToListAsync(cancellationToken);

    var orderedCategories = OrderByGuidSequence(changes, categories, category => category.CategoryId);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new CategorySyncPullResultDto(orderedCategories, nextSinceVersion));
});

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
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.Product, sinceSequence, take, null, includeGlobalRows: false, cancellationToken);
    var products = await ProjectProductSyncItems(
            db.Products
                .AsNoTracking()
                .Where(p => changes.Select(change => change.EntityId).Contains(p.Id)),
            db,
            storeId.Value)
        .ToListAsync(cancellationToken);

    var orderedProducts = OrderByGuidSequence(changes, products, product => product.ProductId);

    var nextSinceVersion = changes.Count == 0
        ? normalizedSinceVersion
        : FormatSequenceSinceVersion(changes[^1].Sequence);

    return Results.Ok(new ProductSyncPullResultDto(orderedProducts, nextSinceVersion));
});

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
    var changes = await GetKeySyncChangesAsync(db, SyncAggregateTypes.Setting, sinceSequence, take, storeId.Value, includeGlobalRows: false, cancellationToken);
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
});

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
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.User, sinceSequence, take, storeId.Value, includeGlobalRows: false, cancellationToken);
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
});

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
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.Device, sinceSequence, take, storeId.Value, includeGlobalRows: false, cancellationToken);
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
});

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
    var changes = await GetGuidSyncChangesAsync(db, SyncAggregateTypes.AuditLog, sinceSequence, take, storeId.Value, includeGlobalRows: false, cancellationToken);
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
});

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
});

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

    var results = new List<DeviceSyncItemResultDto>(request.Devices.Count);

    foreach (var incoming in request.Devices)
    {
        try
        {
            var existing = await UpsertDeviceSnapshotAsync(db, storeId.Value, incoming, cancellationToken);
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
});

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
    var nextSequence = await db.SyncChanges
        .AsNoTracking()
        .Where(c => c.AggregateType == SyncAggregateTypes.CurrencyPolicy
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
});

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
});

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

    var results = new List<ProductSyncItemResultDto>(request.Products.Count);

    foreach (var incoming in request.Products)
    {
        try
        {
            await EnsureCategoryAsync(db, incoming.CategoryId, incoming.CategoryName, incoming.CreatedAt, incoming.UpdatedAt, cancellationToken);

            var existing = await db.Products
                .FirstOrDefaultAsync(p => p.Id == incoming.ProductId, cancellationToken);

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
                    CreatedAt = incoming.CreatedAt
                };
                db.Products.Add(existing);
            }

            ApplyProductSnapshot(existing, incoming);
            await UpsertProductInventoryAsync(db, storeId.Value, userId.Value, incoming, cancellationToken);
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
});

syncApi.MapPost("/categories/push", async (
    CategorySyncBatchDto request,
    IDbContextFactory<PosDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    if (request.Categories.Count == 0)
        return Results.Ok(new CategorySyncPushResultDto(Array.Empty<CategorySyncItemResultDto>()));

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

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

            var existing = await db.Categories.FirstOrDefaultAsync(c => c.Id == incoming.CategoryId, cancellationToken);
            if (existing is not null && existing.UpdatedAt >= incoming.UpdatedAt)
            {
                results.Add(new CategorySyncItemResultDto(incoming.CategoryId, "Skipped", existing.UpdatedAt, null));
                continue;
            }

            if (existing is null)
            {
                existing = new Category
                {
                    Id = incoming.CategoryId
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
});

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
});

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

            var roleId = await EnsureRoleAsync(db, incoming.RoleName, incoming.UpdatedAt, cancellationToken);

            if (existing is null)
            {
                existing = new User
                {
                    Id = incoming.UserId,
                    StoreId = storeId.Value
                };
                db.Users.Add(existing);
            }

            ApplyUserSnapshot(existing, incoming, roleId, storeId.Value);
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
});

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
    Guid? storeId,
    bool includeGlobalRows,
    CancellationToken cancellationToken)
{
    var changes = await BuildSyncChangeQuery(db.SyncChanges.AsNoTracking(), aggregateType, sinceSequence, storeId, includeGlobalRows)
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
    Guid? storeId,
    bool includeGlobalRows,
    CancellationToken cancellationToken)
{
    var changes = await BuildSyncChangeQuery(db.SyncChanges.AsNoTracking(), aggregateType, sinceSequence, storeId, includeGlobalRows)
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

static IQueryable<SyncChange> BuildSyncChangeQuery(
    IQueryable<SyncChange> query,
    string aggregateType,
    long sinceSequence,
    Guid? storeId,
    bool includeGlobalRows)
{
    query = query.Where(c => c.AggregateType == aggregateType && c.Id > sinceSequence);

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
        u.IsDeleted));

static IQueryable<DeviceSyncDto> ProjectDeviceSyncItems(IQueryable<Device> devices) =>
    devices.Select(d => new DeviceSyncDto(
        d.Id,
        d.Name,
        d.SyncVersion,
        d.CreatedAt,
        d.UpdatedAt,
        d.IsDeleted));

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
    var storeUpdatedAt = await db.Stores
        .AsNoTracking()
        .Where(s => s.Id == storeId && !s.IsDeleted)
        .Select(s => (DateTime?)s.UpdatedAt)
        .FirstOrDefaultAsync(cancellationToken)
        ?? DateTime.MinValue;

    var currencyUpdatedAt = await db.Currencies
        .AsNoTracking()
        .Where(c => !c.IsDeleted)
        .Select(c => (DateTime?)c.UpdatedAt)
        .MaxAsync(cancellationToken)
        ?? DateTime.MinValue;

    return storeUpdatedAt >= currencyUpdatedAt ? storeUpdatedAt : currencyUpdatedAt;
}

static async Task EnsureCategoryAsync(
    PosDbContext db,
    Guid categoryId,
    string categoryName,
    DateTime createdAt,
    DateTime updatedAt,
    CancellationToken cancellationToken)
{
    var existing = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
    if (existing is null)
    {
        existing = new Category
        {
            Id = categoryId
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
    Guid storeId,
    DeviceSyncDto incoming,
    CancellationToken cancellationToken)
{
    var matchedByName = false;
    Device? device = await db.Devices
        .FirstOrDefaultAsync(d => d.Id == incoming.DeviceId && d.StoreId == storeId, cancellationToken);

    if (device is null)
    {
        device = await db.Devices
            .FirstOrDefaultAsync(d => d.StoreId == storeId && d.Name == incoming.Name && !d.IsDeleted, cancellationToken);
        matchedByName = device is not null;
    }

    if (device is not null && device.UpdatedAt > incoming.UpdatedAt)
        return device;

    if (device is null)
    {
        device = new Device
        {
            Id = incoming.DeviceId,
            StoreId = storeId
        };
        db.Devices.Add(device);
    }
    else if (matchedByName && device.Id != incoming.DeviceId)
    {
        device.Id = incoming.DeviceId;
    }

    device.Name = string.IsNullOrWhiteSpace(incoming.Name) ? "Unknown Device" : incoming.Name.Trim();
    device.SyncVersion = incoming.SyncVersion <= 0 ? 1 : incoming.SyncVersion;
    device.CreatedAt = incoming.CreatedAt;
    device.UpdatedAt = incoming.UpdatedAt;
    device.IsDeleted = incoming.IsDeleted;
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
    string roleName,
    DateTime updatedAt,
    CancellationToken cancellationToken)
{
    var normalizedRoleName = roleName.Trim();
    var existing = await db.Roles.FirstOrDefaultAsync(r => r.Name == normalizedRoleName, cancellationToken);
    if (existing is not null)
    {
        if (existing.IsDeleted)
            existing.IsDeleted = false;

        if (existing.UpdatedAt < updatedAt)
            existing.UpdatedAt = updatedAt;

        return existing.Id;
    }

    var role = new Role
    {
        Id = Guid.NewGuid(),
        Name = normalizedRoleName,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt,
        IsDeleted = false
    };
    db.Roles.Add(role);
    return role.Id;
}

static void ApplyUserSnapshot(User existing, UserSyncDto incoming, Guid roleId, Guid storeId)
{
    existing.Username = string.IsNullOrWhiteSpace(incoming.Username) ? existing.Username : incoming.Username.Trim();
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

static Invoice CreateInvoiceFromSync(InvoiceSyncInvoiceDto incoming, Guid storeId, Guid userId, Guid? deviceId)
{
    var invoice = new Invoice
    {
        Id = incoming.InvoiceId,
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

static void ApplyInvoiceSnapshot(Invoice existing, InvoiceSyncInvoiceDto incoming, Guid userId, Guid? deviceId)
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

        if (!inventories.TryGetValue(productId, out var inventory))
        {
            inventory = new Inventory
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                StoreId = storeId,
                Quantity = 0m,
                CreatedAt = changedAt,
                UpdatedAt = changedAt
            };
            db.Inventories.Add(inventory);
            inventories[productId] = inventory;
        }

        inventory.Quantity += delta;
        inventory.UpdatedAt = changedAt;

        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            StoreId = storeId,
            InventoryId = inventory.Id,
            InvoiceId = invoiceId,
            UserId = userId,
            Type = delta < 0m ? StockMovementType.Sale : StockMovementType.Refund,
            QuantityDelta = delta,
            QuantityAfter = inventory.Quantity,
            Reference = FormatInvoiceReference(invoiceId),
            Notes = delta < 0m
                ? "Inventory reduced while reconciling a synced invoice snapshot."
                : "Inventory increased while reconciling a synced invoice snapshot.",
            CreatedAt = changedAt,
            UpdatedAt = changedAt
        });
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

static async Task<Device?> GetOrCreateSyncDeviceAsync(
    PosDbContext db,
    Guid storeId,
    InvoiceSyncInvoiceDto incoming,
    CancellationToken cancellationToken)
{
    if (incoming.DeviceId is null && string.IsNullOrWhiteSpace(incoming.DeviceName))
        return null;

    return await UpsertDeviceSnapshotAsync(
        db,
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

static async Task<Guid> ResolveInvoiceUserIdAsync(
    PosDbContext db,
    Guid storeId,
    Guid fallbackUserId,
    string? username,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(username))
        return fallbackUserId;

    var userId = await db.Users
        .AsNoTracking()
        .Where(u => u.StoreId == storeId && u.Username == username.Trim() && !u.IsDeleted)
        .Select(u => (Guid?)u.Id)
        .FirstOrDefaultAsync(cancellationToken);

    return userId ?? fallbackUserId;
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
internal sealed record LoginRequest(string Username);
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