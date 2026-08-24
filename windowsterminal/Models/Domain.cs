using System;

namespace MerchantTerminal.Models;

public static class Money
{
    // "–$1,234.56" with an en dash for negatives, matching the design spec.
    public static string Format(decimal amount) =>
        (amount < 0 ? "–$" : "$") + Math.Abs(amount).ToString("N2");
}

public sealed record Product(string Sku, string Name, string Dept, decimal Price)
{
    public string PriceDisplay => Money.Format(Price);
}

public sealed record CustomerRecord(string Name, string Contact, string Tier, string Points, string Initials);

public sealed record Payment(string Label, string Detail, decimal Amount)
{
    public string AmountDisplay => Money.Format(Amount);
}

public sealed record PromoDef(
    string Id,
    string Name,
    string Terms,
    decimal? Pct,
    decimal? Flat,
    string Scope,   // department name, "*" for all, "none" for never eligible
    string Tag)
{
    public string ValueDisplay => Pct is { } p
        ? $"{Math.Round(p * 100)}%"
        : "–" + Money.Format(Flat ?? 0);
}
