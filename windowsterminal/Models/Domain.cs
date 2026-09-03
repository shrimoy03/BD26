using System;

namespace MerchantTerminal.Models;

public static class Money
{
    public static string Format(decimal amount) =>
        (amount < 0 ? "–$" : "$") + Math.Abs(amount).ToString("N2");
}

public sealed record Product(string Sku, string Name, string Dept, decimal Price)
{
    public string PriceDisplay => Money.Format(Price);
}

public sealed record Payment(string Label, string Detail, decimal Amount)
{
    public string AmountDisplay => Money.Format(Amount);

    /// <summary>The AYS prints bare numbers with no currency sign.</summary>
    public string AysAmount => Amount.ToString("N2");
}
