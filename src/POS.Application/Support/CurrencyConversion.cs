namespace POS.Application.Support;

/// <summary>
/// Converts an amount from one currency into store base using per-currency rates (see <see cref="POS.Core.Entities.Currency"/>).
/// </summary>
public static class CurrencyConversion
{
    /// <summary>baseAmount = amount × (fromRate / baseRate)</summary>
    public static decimal ToBase(decimal amount, decimal fromRate, decimal baseRate)
    {
        if (baseRate == 0)
            throw new ArgumentOutOfRangeException(nameof(baseRate), "Base currency rate cannot be zero.");
        return amount * fromRate / baseRate;
    }
}
