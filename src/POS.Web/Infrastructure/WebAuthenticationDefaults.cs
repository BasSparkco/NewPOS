namespace POS.Web.Infrastructure;

internal static class WebAuthenticationDefaults
{
    public const string Scheme = "DashboardCookie";
}

/// <summary>
/// One policy per <see cref="POS.Core.Enums.Permission"/> flag, mirroring the granular gating WPF
/// already applies via <c>ICurrentSession.HasPermission</c>, plus <see cref="DashboardAccess"/> as the
/// coarse "can reach the dashboard at all" gate (any permission granted, regardless of role name).
/// </summary>
internal static class WebAuthorizationPolicies
{
    public const string DashboardAccess = "DashboardAccess";
    public const string ManageProducts = "Permission:ManageProducts";
    public const string ViewReports = "Permission:ViewReports";
    public const string ViewAudit = "Permission:ViewAudit";
    public const string ManageUsers = "Permission:ManageUsers";
    public const string ManageSettings = "Permission:ManageSettings";
    public const string ProcessRefunds = "Permission:ProcessRefunds";
    public const string ManageStores = "Permission:ManageStores";
}

internal static class WebRateLimitPolicies
{
    public const string Login = "login";
}

internal static class WebClaimTypes
{
    public const string TenantId = "tenant_id";
    public const string StoreId = "store_id";
    public const string CurrencyCode = "currency_code";
    public const string CurrencySymbol = "currency_symbol";
    public const string Permissions = "permissions";
}