namespace POS.Web.Infrastructure;

internal static class WebAuthenticationDefaults
{
    public const string Scheme = "DashboardCookie";
}

internal static class WebAuthorizationPolicies
{
    public const string DashboardAccess = "DashboardAccess";
}

internal static class WebClaimTypes
{
    public const string StoreId = "store_id";
    public const string CurrencyCode = "currency_code";
    public const string CurrencySymbol = "currency_symbol";
}