using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using POS.Application.Abstractions;
using POS.Web.Infrastructure;
using POS.Web.Models;

namespace POS.Web.Controllers;

[AllowAnonymous]
public sealed class AccountController : Controller
{
    private readonly IAuthService _authService;
    private readonly ICurrentSession _session;

    public AccountController(IAuthService authService, ICurrentSession session)
    {
        _authService = authService;
        _session = session;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToLocal(returnUrl);

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(model);

        var result = await _authService.LoginAsync(model.Username, cancellationToken);
        if (!result.Success || !_session.IsAuthenticated)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Invalid username.");
            return View(model);
        }

        if (!HasDashboardAccess(_session.RoleName))
        {
            _session.Clear();
            ModelState.AddModelError(string.Empty, "Dashboard access requires an Admin or Manager role.");
            return View(model);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, _session.UserId.ToString()),
            new(ClaimTypes.Name, _session.Username),
            new(ClaimTypes.Role, _session.RoleName),
            new(WebClaimTypes.StoreId, _session.StoreId.ToString()),
            new(WebClaimTypes.CurrencyCode, _session.BaseCurrencyCode)
        };

        if (!string.IsNullOrWhiteSpace(_session.CurrencySymbol))
            claims.Add(new Claim(WebClaimTypes.CurrencySymbol, _session.CurrencySymbol));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, WebAuthenticationDefaults.Scheme));
        await HttpContext.SignInAsync(
            WebAuthenticationDefaults.Scheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = model.RememberMe,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(model.RememberMe ? 12 : 8)
            });

        return RedirectToLocal(model.ReturnUrl);
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        _session.Clear();
        await HttpContext.SignOutAsync(WebAuthenticationDefaults.Scheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("/account/access-denied")]
    public IActionResult AccessDenied() => View();

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToAction("Index", "Dashboard");
    }

    private static bool HasDashboardAccess(string roleName) =>
        roleName.Equals("Admin", StringComparison.OrdinalIgnoreCase)
        || roleName.Equals("Manager", StringComparison.OrdinalIgnoreCase);
}