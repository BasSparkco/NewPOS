namespace POS.Application.Abstractions;

public interface ICurrentSession
{
    Guid UserId { get; }
    Guid StoreId { get; }
    string Username { get; }
    string RoleName { get; }
    /// <summary>ISO-like code for the store base currency (e.g. ILS).</summary>
    string BaseCurrencyCode { get; }
    /// <summary>Optional display symbol (e.g. ₪).</summary>
    string? CurrencySymbol { get; }
    bool IsAuthenticated { get; }

    void Set(Guid userId, Guid storeId, string username, string roleName, string baseCurrencyCode, string? currencySymbol);
    void Clear();
}
