using POS.Application.Abstractions;

namespace POS.Web.Infrastructure;

internal sealed class WebCurrentSession : ICurrentSession
{
    public Guid UserId { get; private set; }
    public Guid StoreId { get; private set; }
    public string Username { get; private set; } = string.Empty;
    public string RoleName { get; private set; } = string.Empty;
    public string BaseCurrencyCode { get; private set; } = "ILS";
    public string? CurrencySymbol { get; private set; }
    public bool IsAuthenticated => UserId != Guid.Empty && StoreId != Guid.Empty;

    public void Set(Guid userId, Guid storeId, string username, string roleName, string baseCurrencyCode, string? currencySymbol)
    {
        UserId = userId;
        StoreId = storeId;
        Username = username;
        RoleName = roleName;
        BaseCurrencyCode = baseCurrencyCode;
        CurrencySymbol = currencySymbol;
    }

    public void Clear()
    {
        UserId = Guid.Empty;
        StoreId = Guid.Empty;
        Username = string.Empty;
        RoleName = string.Empty;
        BaseCurrencyCode = "ILS";
        CurrencySymbol = null;
    }
}