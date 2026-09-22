using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Application.Abstractions;
using POS.Infrastructure.Data;
using POS.Web.Infrastructure;
using POS.Web.Models;

namespace POS.Web.Controllers;

/// <summary>
/// Tenant-aware store (branch) administration and the active-store selector — tenant.md Stage 4T/T5.
/// <see cref="Index"/>/<see cref="Create"/>/<see cref="GrantAccess"/>/<see cref="RevokeAccess"/> are tenant
/// business administration (require <c>ManageStores</c>); <see cref="Switch"/> is available to any signed-in
/// user and only ever moves them into a store they are already authorized for — it never grants access.
/// </summary>
[Authorize(Policy = WebAuthorizationPolicies.DashboardAccess)]
public sealed class StoresController : Controller
{
    private readonly IStoreAccessService _storeAccess;
    private readonly IDbContextFactory<PosDbContext> _dbFactory;
    private readonly ICurrentSession _session;

    public StoresController(IStoreAccessService storeAccess, IDbContextFactory<PosDbContext> dbFactory, ICurrentSession session)
    {
        _storeAccess = storeAccess;
        _dbFactory = dbFactory;
        _session = session;
    }

    [Authorize(Policy = WebAuthorizationPolicies.ManageStores)]
    [HttpGet]
    public async Task<IActionResult> Index(Guid? storeId, CancellationToken cancellationToken)
    {
        var stores = await _storeAccess.GetTenantStoresAsync(cancellationToken);
        var selectedId = storeId ?? _session.StoreId;
        var selected = stores.FirstOrDefault(s => s.Id == selectedId) ?? stores.FirstOrDefault();

        var accessRows = selected is null
            ? Array.Empty<POS.Application.Models.StoreUserAccessRowDto>()
            : await _storeAccess.GetStoreAccessRowsAsync(selected.Id, cancellationToken);

        var model = new StoresViewModel
        {
            ActiveStoreId = _session.StoreId,
            Stores = stores,
            SelectedStoreId = selected?.Id ?? Guid.Empty,
            SelectedStoreName = selected?.Name,
            AccessRows = accessRows
        };

        return View(model);
    }

    [Authorize(Policy = WebAuthorizationPolicies.ManageStores)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateStoreFormViewModel form, CancellationToken cancellationToken)
    {
        var (success, error, store) = await _storeAccess.CreateStoreAsync(form.Name, form.Address, form.Phone, cancellationToken);
        if (!success)
        {
            TempData["StoresError"] = error ?? "Could not create the store.";
            return RedirectToAction(nameof(Index));
        }

        TempData["StoresSuccess"] = $"Store '{store!.Name}' created.";
        return RedirectToAction(nameof(Index), new { storeId = store.Id });
    }

    [Authorize(Policy = WebAuthorizationPolicies.ManageStores)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GrantAccess(Guid userId, Guid storeId, CancellationToken cancellationToken)
    {
        var (success, error) = await _storeAccess.GrantStoreAccessAsync(userId, storeId, cancellationToken);
        TempData[success ? "StoresSuccess" : "StoresError"] = success ? "Store access granted." : (error ?? "Could not grant store access.");
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [Authorize(Policy = WebAuthorizationPolicies.ManageStores)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeAccess(Guid userId, Guid storeId, CancellationToken cancellationToken)
    {
        var (success, error) = await _storeAccess.RevokeStoreAccessAsync(userId, storeId, cancellationToken);
        TempData[success ? "StoresSuccess" : "StoresError"] = success ? "Store access revoked." : (error ?? "Could not revoke store access.");
        return RedirectToAction(nameof(Index), new { storeId });
    }

    /// <summary>
    /// Moves the current session's active store, re-issuing the auth cookie with the new store's id and
    /// currency claims (never the user, tenant, role or permission claims — those are unaffected by which
    /// store is active). Server-validates access via <see cref="IStoreAccessService.CanCurrentUserAccessStoreAsync"/>
    /// every time — a client-supplied store id is never trusted on its own, matching tenant.md §5's
    /// "client-supplied IDs identify requested resources; they never grant access."
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Switch(Guid storeId, string? returnUrl, CancellationToken cancellationToken)
    {
        if (!await _storeAccess.CanCurrentUserAccessStoreAsync(storeId, cancellationToken))
        {
            TempData["StoresError"] = "You do not have access to that store.";
            return RedirectToLocal(returnUrl);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var store = await db.Stores
            .AsNoTracking()
            .Where(s => s.Id == storeId && !s.IsDeleted)
            .Select(s => new { s.Id, Code = s.BaseCurrency!.Code, Symbol = s.BaseCurrency!.Symbol })
            .FirstOrDefaultAsync(cancellationToken);

        if (store is null)
            return NotFound();

        var authenticateResult = await HttpContext.AuthenticateAsync(WebAuthenticationDefaults.Scheme);
        var existingClaims = authenticateResult.Principal?.Claims ?? Enumerable.Empty<Claim>();

        var claims = existingClaims
            .Where(c => c.Type is not (WebClaimTypes.StoreId or WebClaimTypes.CurrencyCode or WebClaimTypes.CurrencySymbol))
            .ToList();
        claims.Add(new Claim(WebClaimTypes.StoreId, store.Id.ToString()));
        claims.Add(new Claim(WebClaimTypes.CurrencyCode, store.Code));
        if (!string.IsNullOrWhiteSpace(store.Symbol))
            claims.Add(new Claim(WebClaimTypes.CurrencySymbol, store.Symbol));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, WebAuthenticationDefaults.Scheme));
        await HttpContext.SignInAsync(WebAuthenticationDefaults.Scheme, principal, authenticateResult.Properties);

        return RedirectToLocal(returnUrl);
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToAction("Index", "Dashboard");
    }
}
