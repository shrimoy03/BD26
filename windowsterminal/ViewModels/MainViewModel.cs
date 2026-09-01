using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MerchantTerminal.Models;
using MerchantTerminal.Services;

namespace MerchantTerminal.ViewModels;

public enum Overlay
{
    None,
    Customer,
    Promos,
    Tender,
    Done,
    Setup,
}

public partial class MainViewModel : ViewModelBase
{
    private const decimal TaxRate = 0.08875m;

    private static readonly IReadOnlyList<Product> Catalog = new List<Product>
    {
        new("5901-0170", "Valentino Donna Born In Roma Eau de Parfum", "Beauty", 170m),
        new("8842-0299", "Diamond Stud Earrings (1/3 ct. t.w.), 14k White Gold", "Fine Jewelry", 299m),
        new("7712-0149", "The Sak Leather Crossbody", "Handbags", 149m),
        new("3310-1290", "Cashmere Wrap Coat", "Designer Ready-to-Wear", 1290m),
        new("0001-0038", "Little Brown Bag Tote", "Bloomingdale's Exclusives", 38m),
    };

    private static readonly IReadOnlyList<PromoDef> PromoDefs = new List<PromoDef>
    {
        new("loyal10", "Loyallist · 10% off select items", "Auto-qualified · with your Bloomingdale's Loyallist card", 0.10m, null, "Beauty", "Loyallist 10%"),
        new("rewards", "Loyallist Rewards redemption", "Requires linked Loyallist member · $10.00 available", null, 10m, "*", "Rewards $10"),
        new("jewel", "Fine Jewelry private event · $50 off $250", "Manager approval · one per transaction", null, 50m, "Fine Jewelry", "Private event"),
        new("emp", "Associate discount · 20%", "Employee 44182 · excludes fine jewelry", 0.20m, null, "none", "Associate"),
    };

    private readonly ITerminalLink? _link;
    private readonly TerminalLinkHost? _host;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _cartSyncTimer;
    private int _uid = 1;
    private int _txnBase = 40880;
    private string? _terminalOrderId;

    public MainViewModel() : this(null) { }

    public MainViewModel(ITerminalLink? link)
    {
        _link = link;
        _host = link as TerminalLinkHost;
        Setup = new SetupViewModel(_host, () => OnUiThread(() =>
        {
            ActiveOverlay = Overlay.None;
            OnPropertyChanged(nameof(TerminalTargetLabel));
        }));

        if (_link is not null)
        {
            _link.ClientConnected += () => OnUiThread(() =>
            {
                IsTerminalConnected = true;
                Status = "Customer terminal connected";
                _lastCartJson = null;
                QueueCartSync(); // mirror the current basket on connect
            });
            _link.ClientDisconnected += () => OnUiThread(() =>
            {
                IsTerminalConnected = false;
                Status = "Customer terminal disconnected";
            });
            _link.MessageReceived += m => OnUiThread(() => HandleTerminalMessage(m));
        }

        Promos = new ObservableCollection<PromoRow>(PromoDefs.Select(d => new PromoRow(d)));

        // Seed state matching the design handoff.
        AddLine(Catalog[0], select: true);
        AddLine(Catalog[4], select: false);
        Lines[1].Quantity = 2;
        Status = "Item 5590-0021 added · qty 2";
        SelectLineInternal(Lines[0]);
        Refresh();

        _cartSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _cartSyncTimer.Tick += (_, _) => FlushCartSync();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText = FormatClock(DateTime.Now);
        _clock.Start();
        ClockText = FormatClock(DateTime.Now);
    }

    public ObservableCollection<SaleLine> Lines { get; } = new();
    public ObservableCollection<Payment> Payments { get; } = new();
    public ObservableCollection<PromoRow> Promos { get; }
    public IReadOnlyList<Product> QuickKeys => Catalog;
    public IReadOnlyList<CustomerRecord> Customers { get; } = new List<CustomerRecord>
    {
        new("Luna Martinez", "+1 917 442 0118 · luna.m@mail.com", "LOYALLIST", "1,300 pts · $10 rewards", "LM"),
        new("Shrimoy Satpathy", "+1 646 220 7741 · shrimoy@wink.cloud", "TOP OF THE LIST", "48,210 pts", "SS"),
        new("Priya Rai", "+1 212 908 3355 · priya.r@mail.com", "MEMBER", "1,240 pts", "PR"),
    };

    private readonly List<string> _appliedPromos = new();
    private SaleLine? _selected;

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial string ClockText { get; set; } = "";

    [ObservableProperty]
    public partial string EntryBuffer { get; set; } = "";

    [ObservableProperty]
    public partial CustomerRecord? Customer { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCustomer), nameof(ShowPromos), nameof(ShowTender),
        nameof(ShowDone), nameof(ShowSetup))]
    public partial Overlay ActiveOverlay { get; set; } = Overlay.None;

    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TerminalStatusLabel), nameof(TerminalTargetLabel), nameof(CardTileHint))]
    public partial bool IsTerminalConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TendersEnabled))]
    public partial bool IsAwaitingTerminal { get; set; }

    public bool ShowCustomer => ActiveOverlay == Overlay.Customer;
    public bool ShowPromos => ActiveOverlay == Overlay.Promos;
    public bool ShowTender => ActiveOverlay == Overlay.Tender;
    public bool ShowDone => ActiveOverlay == Overlay.Done;
    public bool ShowSetup => ActiveOverlay == Overlay.Setup;
    public bool TendersEnabled => !IsAwaitingTerminal;

    /// <summary>Setup screen (F9). Null host = design-time / no live link.</summary>
    public SetupViewModel Setup { get; }

    public string TerminalStatusLabel => IsTerminalConnected ? "Terminal" : "No terminal";
    public string TerminalTargetLabel => _host is null
        ? "Not configured"
        : TerminalLinkHost.Describe(_host.Config);

    public string ThemeLabel => IsDarkTheme ? "LIGHT THEME" : "DARK THEME";
    public string EntryDisplay => EntryBuffer + "▌";

    // ----- Derived money values -----

    public decimal Subtotal => Lines.Sum(l => l.Amount);

    public decimal Discount
    {
        get
        {
            decimal disc = 0;
            foreach (var id in _appliedPromos)
            {
                var p = PromoDefs.First(d => d.Id == id);
                if (p.Pct is { } pct)
                {
                    var base_ = Lines.Where(l => p.Scope == "*" || l.Product.Dept == p.Scope).Sum(l => l.Amount);
                    disc += base_ * pct;
                }
                else
                {
                    disc += p.Flat ?? 0;
                }
            }
            return Math.Min(disc, Subtotal);
        }
    }

    public decimal Tax => Math.Round((Subtotal - Discount) * TaxRate, 2, MidpointRounding.AwayFromZero);
    public decimal Total => Subtotal - Discount + Tax;
    public decimal Paid => Payments.Sum(p => p.Amount);
    public decimal Balance => Math.Max(0, Total - Paid);
    public decimal Change => Math.Max(0, Paid - Total);
    public bool IsSettled => Balance <= 0.005m && Payments.Count > 0;

    public string SubtotalDisplay => Money.Format(Subtotal);
    public string DiscountDisplay => Discount > 0 ? "–" + Money.Format(Discount) : Money.Format(0);
    public string DiscountLabel => _appliedPromos.Count > 0 ? $"Discounts ({_appliedPromos.Count})" : "Discounts";
    public string TaxDisplay => Money.Format(Tax);
    public string TotalDisplay => Money.Format(Total);
    public string BalanceDisplay => Money.Format(Balance);
    public string ChangeDisplay => Money.Format(Change);
    public bool HasPaid => Payments.Count > 0;
    public bool IsEmpty => Lines.Count == 0;
    public bool IsNotEmpty => Lines.Count > 0;

    public string TxnId => $"T-0142-07-{(IsSettled ? _txnBase + 1 : _txnBase)}";
    public string LineCountLabel =>
        $"{Lines.Count} line{(Lines.Count == 1 ? "" : "s")} · {Lines.Sum(l => l.Quantity)} units";

    public string CustomerName => Customer?.Name ?? "No customer linked";
    public string CustomerMeta => Customer is { } c ? $"{c.Tier} · {c.Points}" : "Search by phone, email, or Loyallist card";
    public string CustomerInitials => Customer?.Initials ?? "+";
    public string CustomerAction => Customer is null ? "LOOK UP" : "VIEW";
    public string CustomerHint => Customer is null ? "Not linked" : "Linked";
    public string PromoHint => $"{_appliedPromos.Count} applied · F4";
    public string PromoScope => $"{Lines.Count} lines · {Money.Format(Subtotal)} eligible";

    public string TenderLabel => Payments.Count > 0 ? "Continue tender" : "Tender";
    public string TenderTitle => $"Tender · {Money.Format(Total)} total";
    public string TenderHint => IsAwaitingTerminal
        ? "Awaiting customer on the payment terminal…"
        : Payments.Count > 0
            ? $"Split tender in progress · {Payments.Count} authorised"
            : "Card, cash, gift card, or split across tenders";
    public string CardTileHint => IsTerminalConnected
        ? "Customer terminal · chip, tap, or Wink"
        : "Chip, contactless, or manual";

    public string DoneMessage =>
        (Customer is { } c ? $"{c.Name} earned {Math.Round(Total)} Loyallist points. " : "") +
        $"Receipt printed and emailed. Transaction {Money.Format(Total)} settled across " +
        $"{Payments.Count} tender{(Payments.Count == 1 ? "" : "s")}.";

    // ----- Basket -----

    [RelayCommand]
    private void AddItem(Product product)
    {
        var existing = Lines.FirstOrDefault(l => l.Product.Sku == product.Sku);
        if (existing is not null)
        {
            existing.Quantity++;
            SelectLineInternal(existing);
            Status = $"{product.Name} · qty {existing.Quantity}";
        }
        else
        {
            var line = AddLine(product, select: true);
            Status = $"Item {product.Sku} added";
            SelectLineInternal(line);
        }
        Refresh();
    }

    private SaleLine AddLine(Product product, bool select)
    {
        var line = new SaleLine(_uid++, product);
        Lines.Add(line);
        Renumber();
        if (select) SelectLineInternal(line);
        return line;
    }

    [RelayCommand]
    private void SelectLine(SaleLine line)
    {
        SelectLineInternal(line);
        Status = $"Line {Lines.IndexOf(line) + 1} selected";
    }

    private void SelectLineInternal(SaleLine line)
    {
        foreach (var l in Lines) l.IsSelected = l == line;
        _selected = line;
    }

    [RelayCommand]
    private void IncrementQty(SaleLine line)
    {
        line.Quantity++;
        SelectLineInternal(line);
        Status = "Quantity increased";
        Refresh();
    }

    [RelayCommand]
    private void DecrementQty(SaleLine line)
    {
        line.Quantity = Math.Max(1, line.Quantity - 1);
        SelectLineInternal(line);
        Status = "Quantity decreased";
        Refresh();
    }

    private void Renumber()
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].Num = (i + 1).ToString("D2");
        }
    }

    // ----- Scan / SKU entry -----

    public void EntryChar(char c)
    {
        if (char.IsLetterOrDigit(c) || c == '-')
        {
            EntryBuffer += c;
            OnPropertyChanged(nameof(EntryDisplay));
        }
    }

    public void EntryBackspace()
    {
        if (EntryBuffer.Length > 0)
        {
            EntryBuffer = EntryBuffer[..^1];
            OnPropertyChanged(nameof(EntryDisplay));
        }
    }

    public void EntrySubmit()
    {
        var sku = EntryBuffer.Trim();
        EntryBuffer = "";
        OnPropertyChanged(nameof(EntryDisplay));
        if (sku.Length == 0) return;

        var product = Catalog.FirstOrDefault(p => p.Sku == sku);
        if (product is not null) AddItem(product);
        else Status = $"SKU {sku} not found";
    }

    // ----- Function pad -----

    [RelayCommand] private void ItemSearch() => Status = "Catalogue search opened";
    [RelayCommand] private void OpenPromos() => ActiveOverlay = Overlay.Promos;
    [RelayCommand] private void OpenCustomer() => ActiveOverlay = Overlay.Customer;
    [RelayCommand] private void PriceOverride() => Status = "Manager authorisation required";
    [RelayCommand] private void ShipFromStore() => Status = "Checking network inventory…";
    [RelayCommand] private void SuspendSale() => Status = "Sale suspended · recall code 4471";

    [RelayCommand]
    private void GiftOptions() =>
        Status = _selected is null ? "Select a line for gift options" : $"Gift wrap added to line {Lines.IndexOf(_selected) + 1}";

    [RelayCommand]
    private void VoidLine()
    {
        if (_selected is null) return;
        Lines.Remove(_selected);
        _selected = Lines.FirstOrDefault();
        if (_selected is not null) SelectLineInternal(_selected);
        Renumber();
        Status = "Line voided";
        Refresh();
    }

    // ----- Customer -----

    [RelayCommand]
    private void PickCustomer(CustomerRecord customer)
    {
        Customer = customer;
        ActiveOverlay = Overlay.None;
        Status = $"{customer.Name} linked · {customer.Tier}";
        Refresh();
    }

    // ----- Promotions -----

    [RelayCommand]
    private void TogglePromo(PromoRow row)
    {
        var p = row.Def;
        if (p.Id == "loyalty" && Customer is null)
        {
            Status = "Link a Loyallist member to redeem rewards";
            return;
        }
        if (p.Id == "emp")
        {
            Status = "Associate discount blocked · fine jewelry in basket";
            return;
        }

        var on = _appliedPromos.Contains(p.Id);
        if (on) _appliedPromos.Remove(p.Id);
        else _appliedPromos.Add(p.Id);

        foreach (var line in Lines.Where(l => p.Scope == "*" || l.Product.Dept == p.Scope))
        {
            line.Promo = on ? null : p.Tag;
        }

        Status = (on ? "Removed " : "Applied ") + p.Name;
        Refresh();
    }

    // ----- Overlays -----

    [RelayCommand]
    private void CloseOverlay()
    {
        if (IsAwaitingTerminal) return; // don't leave tender mid-authorisation
        ActiveOverlay = Overlay.None;
    }

    [RelayCommand]
    private void OpenSetup()
    {
        if (IsAwaitingTerminal)
        {
            Status = "Finish or cancel the payment before changing setup";
            return;
        }

        Setup.RescanCommand.Execute(null);
        ActiveOverlay = Overlay.Setup;
    }

    [RelayCommand]
    private void OpenTender()
    {
        if (Lines.Count == 0)
        {
            Status = "Basket is empty";
            return;
        }
        ActiveOverlay = Overlay.Tender;
    }

    // ----- Tenders -----

    private PosMessage BuildCartMessage() => new()
    {
        Type = PosMessageTypes.DisplayCart,
        OrderId = TxnId,
        Currency = "USD",
        Items = Lines.Select(l => new CartLine(l.Product.Name, l.Quantity,
            (long)Math.Round(l.Amount * 100m))).ToArray(),
        SubtotalCents = (long)Math.Round(Subtotal * 100m),
        TaxCents = (long)Math.Round(Tax * 100m),
        AmountCents = (long)Math.Round(Total * 100m),
    };

    [RelayCommand]
    private async Task SendToTerminalAsync()
    {
        if (_link is null || !IsTerminalConnected)
        {
            Status = "No customer terminal connected";
            return;
        }

        Status = await _link.SendAsync(BuildCartMessage())
            ? $"Basket sent to customer terminal · {Money.Format(Total)}"
            : "Failed to send basket to terminal";
    }

    // ----- Live cart mirroring -----

    private string? _lastCartJson;

    /// <summary>
    /// Debounced push of the basket to the customer terminal. Called from
    /// Refresh() so every mutation (add line, qty, void, promo, new sale)
    /// lands on the PxRetailer screen without a manual send. Skipped while a
    /// payment is in flight so we don't stomp the EMV screens.
    /// </summary>
    private void QueueCartSync()
    {
        if (_link is null || !IsTerminalConnected || IsAwaitingTerminal) return;
        _cartSyncTimer.Stop();
        _cartSyncTimer.Start();
    }

    private void FlushCartSync()
    {
        _cartSyncTimer.Stop();
        if (_link is null || !IsTerminalConnected || IsAwaitingTerminal) return;

        var message = BuildCartMessage();
        var json = PosJson.Serialize(message);
        if (json == _lastCartJson) return;
        _lastCartJson = json;
        _ = Task.Run(async () =>
        {
            if (!await _link.SendAsync(message))
            {
                OnUiThread(() => _lastCartJson = null); // retry on next change
            }
        });
    }

    [RelayCommand]
    private async Task TenderCardAsync()
    {
        if (IsAwaitingTerminal) return;

        if (_link is { IsConnected: true })
        {
            _terminalOrderId = $"{TxnId}-{Payments.Count + 1}";
            IsAwaitingTerminal = true;
            Refresh();
            Status = "Payment sent to customer terminal";

            var sent = await _link.SendAsync(new PosMessage
            {
                Type = PosMessageTypes.StartPayment,
                OrderId = _terminalOrderId,
                AmountCents = (long)Math.Round(Balance * 100),
                Currency = "USD",
                Method = "CARD", // PxRetailer: straight into the EMV flow
            });

            if (!sent)
            {
                IsAwaitingTerminal = false;
                _terminalOrderId = null;
                Status = "Customer terminal unreachable";
                Refresh();
            }
        }
        else
        {
            Pay("Visa •••• 4417", "Contactless · approved 8813", Balance);
        }
    }

    [RelayCommand]
    private async Task CancelTerminalAsync()
    {
        if (!IsAwaitingTerminal) return;
        if (_link is not null && _terminalOrderId is not null)
        {
            await _link.SendAsync(new PosMessage
            {
                Type = PosMessageTypes.CancelPayment,
                OrderId = _terminalOrderId,
            });
        }
        IsAwaitingTerminal = false;
        _terminalOrderId = null;
        Status = "Terminal payment cancelled";
        Refresh();
    }

    private void HandleTerminalMessage(PosMessage m)
    {
        // A tender button on the PxRetailer form (PAYMENTSTATUS FireEvent):
        // face/palm hands the sale to the WinkPay app, card starts EMV.
        if (m.Type == PosMessageTypes.TenderSelected)
        {
            if (!IsAwaitingTerminal || _terminalOrderId is null) return;
            var method = m.Method switch
            {
                "FACE" => "FACE",
                "PALM" => "PALM",
                "CARD" or "CREDIT" or "DEBIT" => "CARD",
                _ => null,
            };
            if (method is null) return;

            Status = method == "CARD"
                ? "Customer chose card — starting EMV"
                : $"Customer chose {m.Method!.ToLowerInvariant()} — starting WinkPay";
            _ = _link!.SendAsync(new PosMessage
            {
                Type = PosMessageTypes.StartPayment,
                OrderId = _terminalOrderId,
                AmountCents = (long)Math.Round(Balance * 100),
                Currency = "USD",
                Method = method,
            });
            return;
        }

        if (m.Type != PosMessageTypes.PaymentResult || !IsAwaitingTerminal) return;
        if (_terminalOrderId is not null && m.OrderId is not null && m.OrderId != _terminalOrderId) return;

        IsAwaitingTerminal = false;
        _terminalOrderId = null;

        switch (m.Status)
        {
            case "APPROVED":
                _ = _link!.SendAsync(new PosMessage { Type = PosMessageTypes.ShowThanks });
                var amount = m.AmountCents is { } cents ? cents / 100m : Balance;
                Pay(m.Method ?? "Card · customer terminal", "Customer terminal · approved", Math.Min(amount, Balance));
                break;
            case "DECLINED":
                Status = m.Reason ?? "Payment declined on customer terminal";
                Refresh();
                break;
            case "CANCELLED":
                Status = "Payment cancelled on customer terminal";
                Refresh();
                break;
        }
    }

    [RelayCommand]
    private void TenderCircle() =>
        Pay("Loyallist card •••• 1234", Customer is { } c ? $"On file · {c.Name}" : "Manual entry", Balance);

    [RelayCommand]
    private void TenderCash() => Pay("Cash", "Drawer 2 · tendered", Balance);

    [RelayCommand]
    private void TenderGift() =>
        Pay("Gift card •••• 2210", "Partial · $500.00 balance", Math.Min(Balance, 500m));

    [RelayCommand]
    private void TenderSplit() =>
        Pay("Visa •••• 4417", "Split 1 of 2", Math.Min(Balance, 500m));

    [RelayCommand]
    private async Task TenderLoyallistAsync()
    {
        if (IsAwaitingTerminal) return;

        if (_link is { IsConnected: true })
        {
            _terminalOrderId = $"{TxnId}-{Payments.Count + 1}";
            IsAwaitingTerminal = true;
            Refresh();
            Status = "Customer choosing payment on terminal";

            var sent = await _link.SendAsync(new PosMessage
            {
                Type = PosMessageTypes.StartPayment,
                OrderId = _terminalOrderId,
                AmountCents = (long)Math.Round(Balance * 100),
                Currency = "USD",
                Method = "BD_LOYALLIST", // PxRetailer: payment-options page
            });

            if (!sent)
            {
                IsAwaitingTerminal = false;
                _terminalOrderId = null;
                Status = "Customer terminal unreachable";
                Refresh();
            }
        }
        else
        {
            Status = "No customer terminal connected";
        }
    }

    private void Pay(string label, string detail, decimal amount)
    {
        Payments.Add(new Payment(label, detail, amount));
        Status = $"{label} authorised · {Money.Format(amount)}";
        Refresh();
    }

    [RelayCommand]
    private void Finish() => ActiveOverlay = Overlay.Done;

    [RelayCommand]
    private void NewSale()
    {
        _txnBase++;
        Lines.Clear();
        Payments.Clear();
        _appliedPromos.Clear();
        foreach (var row in Promos) row.IsApplied = false;
        Customer = null;
        _selected = null;
        ActiveOverlay = Overlay.None;
        Status = "New sale started · scan an item";
        Refresh();
    }

    // ----- Theme -----

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        OnPropertyChanged(nameof(ThemeLabel));
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    // ----- Helpers -----

    private void Refresh()
    {
        foreach (var row in Promos)
        {
            row.IsApplied = _appliedPromos.Contains(row.Def.Id);
            row.IsBlocked = row.Def.Id == "emp" || (row.Def.Id == "loyalty" && Customer is null);
        }

        foreach (var name in DerivedProps) OnPropertyChanged(name);
        QueueCartSync();
    }

    private static readonly string[] DerivedProps =
    {
        nameof(Subtotal), nameof(Discount), nameof(Tax), nameof(Total), nameof(Balance), nameof(Change),
        nameof(SubtotalDisplay), nameof(DiscountDisplay), nameof(DiscountLabel), nameof(TaxDisplay),
        nameof(TotalDisplay), nameof(BalanceDisplay), nameof(ChangeDisplay), nameof(HasPaid),
        nameof(IsEmpty), nameof(IsNotEmpty), nameof(IsSettled), nameof(TxnId), nameof(LineCountLabel),
        nameof(CustomerName), nameof(CustomerMeta), nameof(CustomerInitials), nameof(CustomerAction),
        nameof(CustomerHint), nameof(PromoHint), nameof(PromoScope), nameof(TenderLabel),
        nameof(TenderTitle), nameof(TenderHint), nameof(CardTileHint), nameof(DoneMessage),
    };

    private static string FormatClock(DateTime now) => now.ToString("ddd d MMM · h:mm tt");

    private static void OnUiThread(Action action) => Dispatcher.UIThread.Post(action);
}
