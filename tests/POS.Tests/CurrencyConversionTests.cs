using POS.Application.Support;

namespace POS.Tests;

public class CurrencyConversionTests
{
    [Fact]
    public void ToBase_same_rate_is_identity()
    {
        var b = CurrencyConversion.ToBase(42.5m, 1m, 1m);
        Assert.Equal(42.5m, b);
    }

    [Fact]
    public void ToBase_converts_using_rate_ratio()
    {
        // ILS base rate 1, USD rate 3.65 → 100 USD → 365 ILS
        var b = CurrencyConversion.ToBase(100m, 3.65m, 1m);
        Assert.Equal(365m, b);
    }

    [Fact]
    public void ToBase_zero_base_rate_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CurrencyConversion.ToBase(1m, 1m, 0m));
    }
}
