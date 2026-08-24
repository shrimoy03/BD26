using CommunityToolkit.Mvvm.ComponentModel;

namespace MerchantTerminal.Models;

public partial class SaleLine : ObservableObject
{
    public SaleLine(int uid, Product product)
    {
        Uid = uid;
        Product = product;
    }

    public int Uid { get; }
    public Product Product { get; }

    public string Name => Product.Name;
    public string Meta => $"{Product.Sku} · {Product.Dept}";
    public string PriceDisplay => Money.Format(Product.Price);
    public decimal Amount => Product.Price * Quantity;

    [ObservableProperty]
    public partial string Num { get; set; } = "01";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount), nameof(AmountDisplay))]
    public partial int Quantity { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPromo))]
    public partial string? Promo { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string AmountDisplay => Money.Format(Amount);
    public bool HasPromo => Promo is not null;
}
