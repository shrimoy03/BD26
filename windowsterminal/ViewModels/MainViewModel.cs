using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MerchantTerminal.Models;
using MerchantTerminal.Services;

namespace MerchantTerminal.ViewModels;

/// <summary>
/// The screens of the AYS register, in the order the BLM POS flow deck walks
/// them: loyalty prompt → merchandise scan → checkout → (more payments) →
/// tender execution → purchase complete.
/// </summary>
public enum RegisterStage
{
    Loyalty,
    Scan,
    Checkout,
    MorePayments,
    CashTender,
    CardTender,
    SignatureWait,
    Complete,
}

/// <summary>One of the eight T-keys under the screen. Blank label = idle key.</summary>
public sealed record TKey(int Index, string Label, bool IsEnabled)
{
    public string Prefix => $"T{Index}";
    public bool HasLabel => Label.Length > 0;
}

public partial class MainViewModel : ViewModelBase
{
    // The store 59 test register rings 50.00 → 0.83 tax; match the deck.
    private const decimal TaxRate = 0.0166m;
    private const string BuildLabel = "Build 2026.6.1_1112";

    private static readonly IReadOnlyList<Product> Catalog = new List<Product>
    {
        new("3145891313406", "Chanel Beaute", "Beauty", 50m),
        new("3365440057838", "Ysl Cosmetics", "Beauty", 30m),
        new("1230000456789", "Little Brown Bag Tote", "Exclusives", 38m),
        new("7930006543210", "Valentino Donna Edp", "Beauty", 170m),
    };

    private readonly ITerminalLink? _link;
    private readonly TerminalLinkHost? _host;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _cartSyncTimer;
    private readonly DispatcherTimer _flowTimer;      // mock EMV / signature / complete pacing
    private readonly DispatcherTimer _signatureTimer; // 89-second signature countdown
    private int _uid = 1;
    private int _txnBase = 40880;
    private string? _terminalOrderId;
    private Action? _flowTimerAction;

    public MainViewModel() : this(null) { }

    public MainViewModel(ITerminalLink? link)
    {
        _link = link;
        _host = link as TerminalLinkHost;
        Setup = new SetupViewModel(_host, () => OnUiThread(() =>
        {
            ShowSetup = false;
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

        _cartSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _cartSyncTimer.Tick += (_, _) => FlushCartSync();

        _flowTimer = new DispatcherTimer();
        _flowTimer.Tick += (_, _) =>
        {
            _flowTimer.Stop();
            var action = _flowTimerAction;
            _flowTimerAction = null;
            action?.Invoke();
        };

        _signatureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _signatureTimer.Tick += (_, _) =>
        {
            if (SignatureSecondsLeft > 0) SignatureSecondsLeft--;
        };

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => TickClock();
        _clock.Start();
        TickClock();

        Refresh();
    }

    public ObservableCollection<SaleLine> Lines { get; } = new();
    public ObservableCollection<Payment> Payments { get; } = new();

    private SaleLine? _selected;

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial string DateText { get; set; } = "";

    [ObservableProperty]
    public partial string TimeText { get; set; } = "";

    [ObservableProperty]
    public partial string EntryBuffer { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomerLinked), nameof(LoyaltyNumberDisplay))]
    public partial string? CustomerName { get; set; }

    [ObservableProperty]
    public partial bool ShowSetup { get; set; }

    [ObservableProperty]
    public partial bool ShowItems { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineLabel))]
    public partial bool IsTerminalConnected { get; set; }

    [ObservableProperty]
    public partial bool IsAwaitingTerminal { get; set; }

    [ObservableProperty]
    public partial int SignatureSecondsLeft { get; set; } = 89;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsLoyaltyStage), nameof(IsScanStage), nameof(IsCheckoutStage),
        nameof(IsMorePaymentsStage), nameof(IsCashStage), nameof(IsCardStage),
        nameof(IsSignatureStage), nameof(IsCompleteStage), nameof(IsTenderSelectStage),
        nameof(ShowBasket), nameof(ShowTotals), nameof(ShowCoupons), nameof(TKeys),
        nameof(ShowScanEntry), nameof(ShowCashEntry))]
    public partial RegisterStage Stage { get; set; } = RegisterStage.Loyalty;

    public bool IsLoyaltyStage => Stage == RegisterStage.Loyalty;
    public bool IsScanStage => Stage == RegisterStage.Scan;
    public bool IsCheckoutStage => Stage == RegisterStage.Checkout;
    public bool IsMorePaymentsStage => Stage == RegisterStage.MorePayments;
    public bool IsCashStage => Stage == RegisterStage.CashTender;
    public bool IsCardStage => Stage == RegisterStage.CardTender;
    public bool IsSignatureStage => Stage == RegisterStage.SignatureWait;
    public bool IsCompleteStage => Stage == RegisterStage.Complete;
    public bool IsTenderSelectStage => Stage is RegisterStage.Checkout or RegisterStage.MorePayments;
    public bool ShowBasket => Stage != RegisterStage.Loyalty;
    public bool ShowTotals => Stage is RegisterStage.Checkout or RegisterStage.MorePayments
        or RegisterStage.CashTender or RegisterStage.CardTender or RegisterStage.SignatureWait;
    public bool ShowCoupons => ShowTotals;
    public bool ShowScanEntry => Stage == RegisterStage.Scan;
    public bool ShowCashEntry => Stage == RegisterStage.CashTender;

    /// <summary>Setup screen (F9). Null host = design-time / no live link.</summary>
    public SetupViewModel Setup { get; }

    public string TerminalTargetLabel => _host is null
        ? "Not configured"
        : TerminalLinkHost.Describe(_host.Config);

    public string Build => BuildLabel;
    public string OnlineLabel => IsTerminalConnected ? "Online" : "Offline";

    public bool CustomerLinked => CustomerName is not null;
    public string LoyaltyNumberDisplay => CustomerLinked ? "Loyalty Number: XXXXXXXXX8585" : "";

    // ----- Derived money values (AYS prints bare numbers, no currency sign) -----

    public decimal Subtotal => Lines.Sum(l => l.Amount);
    public decimal Tax => Math.Round(Subtotal * TaxRate, 2, MidpointRounding.AwayFromZero);
    public decimal Total => Subtotal + Tax;
    public decimal Balance => Math.Max(0, Total - Payments.Sum(p => p.Amount));

    public string SubtotalDisplay => Subtotal.ToString("N2");
    public string TaxDisplay => Tax.ToString("N2");
    public string TotalDisplay => Total.ToString("N2");
    public string AmountDueDisplay => Balance.ToString("N2");
    public string PurchItemsLabel => $"Purch Items: {Lines.Count}";
    public bool HasItems => Lines.Count > 0;
    public string CouponsLabel => "Coupons: 0   Savings: 0.00";

    public string TxnId => $"T-0059-18-{_txnBase}";

    // ----- Scan / cash entry -----

    public string ScanEntryDisplay => EntryBuffer;
    public string CashEntryDisplay => (CashEntryCents / 100m).ToString("N2");
    private long CashEntryCents
    {
        get
        {
            var digits = new string(EntryBuffer.Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? 0 : long.TryParse(digits, out var v) ? v : 0;
        }
    }

    public string SignatureCountdownText =>
        $"Transaction will be canceled in {SignatureSecondsLeft} seconds " +
        "if a signature is not captured.  If more time is needed, press Esc.";

    partial void OnSignatureSecondsLeftChanged(int value) =>
        OnPropertyChanged(nameof(SignatureCountdownText));

    // ----- T-keys -----

    public IReadOnlyList<TKey> TKeys => BuildTKeys();

    private IReadOnlyList<TKey> BuildTKeys()
    {
        var hasItems = Lines.Count > 0;
        return Stage switch
        {
            RegisterStage.Loyalty => new[]
            {
                new TKey(1, "Lookup Loyalty Number", true),
                new TKey(2, "Enroll in Loyalty", true),
                new TKey(3, "Apply for New Account", true),
                new TKey(4, "Lookup Account", true),
                new TKey(5, "Items", true),
                new TKey(6, "Terminal Setup", true),
                new TKey(7, "Pay By Link", true),
                new TKey(8, "Input Account on Signature Pad", true),
            },
            RegisterStage.Scan => new[]
            {
                new TKey(1, "Checkout", hasItems),
                new TKey(2, "Change Price", hasItems),
                new TKey(3, "Send Merchandise", true),
                new TKey(4, "Add Gift Receipts on All", true),
                new TKey(5, "Items", true),
                new TKey(6, "Change Tax", hasItems),
                new TKey(7, "Loyallist Lookup/Enrollment", !CustomerLinked),
                new TKey(8, "Add Registry on All", true),
            },
            RegisterStage.Checkout => new[]
            {
                new TKey(1, "Bloomingdale's Card/ Bloomingdale's Pay", true),
                new TKey(2, "Biometric Pay", true),
                new TKey(3, "", false),
                new TKey(4, "", false),
                new TKey(5, "Gift Card/Rewards Happy Returns", true),
                new TKey(6, "", false),
                new TKey(7, "", false),
                new TKey(8, "More Payment Methods", true),
            },
            RegisterStage.MorePayments => new[]
            {
                new TKey(1, "Bloomingdale's Card/ Bloomingdale's Pay", true),
                new TKey(2, "Bankcard/ Debit Card/ Mobile Wallet", true),
                new TKey(3, "Cash", true),
                new TKey(4, "Check", true),
                new TKey(5, "Gift Card/Rewards Happy Returns", true),
                new TKey(6, "Reward Certificate", true),
                new TKey(7, "PayPal/Venmo", false),
                new TKey(8, "More", true),
            },
            RegisterStage.CardTender => new[]
            {
                new TKey(1, "", false),
                new TKey(2, "Apply for New Account", true),
                new TKey(3, "", false),
                new TKey(4, "Lookup Account", true),
                new TKey(5, "", false),
                new TKey(6, "", false),
                new TKey(7, "Promo Plans", true),
                new TKey(8, "Input Account on Signature Pad", true),
            },
            RegisterStage.SignatureWait => new[]
            {
                new TKey(1, "", false),
                new TKey(2, "", false),
                new TKey(3, "", false),
                new TKey(4, "Re-Prompt Signature", true),
                new TKey(5, "", false),
                new TKey(6, "", false),
                new TKey(7, "", false),
                new TKey(8, "", false),
            },
            _ => Enumerable.Range(1, 8).Select(i => new TKey(i, "", false)).ToArray(),
        };
    }

    [RelayCommand]
    private void PressTKey(TKey key)
    {
        if (!key.IsEnabled) return;
        switch (Stage)
        {
            case RegisterStage.Loyalty:
                PressLoyaltyKey(key.Index);
                break;
            case RegisterStage.Scan:
                PressScanKey(key.Index);
                break;
            case RegisterStage.Checkout:
                PressCheckoutKey(key.Index);
                break;
            case RegisterStage.MorePayments:
                PressMorePaymentsKey(key.Index);
                break;
            case RegisterStage.CardTender:
                PressCardKey(key.Index);
                break;
            case RegisterStage.SignatureWait when key.Index == 4:
                SignatureSecondsLeft = 89;
                Status = "Signature re-prompted";
                break;
        }
    }

    private void PressLoyaltyKey(int index)
    {
        switch (index)
        {
            case 1:
            case 4:
            case 8:
                LinkLoyalty();
                break;
            case 2:
                Status = "Loyallist enrollment sent to Signature Pad";
                break;
            case 3:
                Status = "New account application sent to Signature Pad";
                break;
            case 5:
                OpenItems();
                break;
            case 6:
                OpenSetup();
                break;
            case 7:
                Status = "Pay By Link — register is in Send Merchandise mode";
                break;
        }
    }

    private void LinkLoyalty()
    {
        CustomerName = "B.TEST";
        Status = "Loyallist XXXXXXXXX8585 linked";
        Stage = RegisterStage.Scan;
        ClearEntry();
        Refresh();
    }

    private void PressScanKey(int index)
    {
        switch (index)
        {
            case 1:
                BeginCheckout();
                break;
            case 2: Status = "Change Price — supervisor required"; break;
            case 3: Status = "Send Merchandise mode"; break;
            case 4: Status = "Gift receipts added on all"; break;
            case 5: OpenItems(); break;
            case 6: Status = "Change Tax — supervisor required"; break;
            case 7: LinkLoyalty(); break;
            case 8: Status = "Registry added on all"; break;
        }
    }

    private void BeginCheckout()
    {
        if (Lines.Count == 0) return;
        Stage = RegisterStage.Checkout;
        ClearEntry();
        Refresh();
    }

    private void PressCheckoutKey(int index)
    {
        switch (index)
        {
            case 1:
                StartCardTender("BD_LOYALLIST");
                break;
            case 2:
                // Biometric Pay: navigate the terminal to the PaymentScreen
                // form; the Face/Palm buttons there fire the tender event back.
                StartCardTender("BIOMETRIC");
                break;
            case 5:
                Status = "Gift Card / Rewards — have customer swipe the card";
                break;
            case 8:
                Stage = RegisterStage.MorePayments;
                break;
        }
    }

    private void PressMorePaymentsKey(int index)
    {
        switch (index)
        {
            case 1:
                StartCardTender("BD_LOYALLIST");
                break;
            case 2:
                StartCardTender("CARD");
                break;
            case 3:
                Stage = RegisterStage.CashTender;
                ClearEntry();
                break;
            case 4: Status = "Check tender is not available on this register"; break;
            case 5: Status = "Gift Card / Rewards — have customer swipe the card"; break;
            case 6: Status = "No reward certificates on file"; break;
            case 8: Status = "No more payment methods"; break;
        }
    }

    private void PressCardKey(int index)
    {
        switch (index)
        {
            case 2: Status = "New account application sent to Signature Pad"; break;
            case 4: Status = "Lookup account on Signature Pad"; break;
            case 7: Status = "No promo plans for this basket"; break;
            case 8: Status = "Key-in prompted on Signature Pad"; break;
        }
    }

    // ----- Scan / cash entry (keyboard + scan gun) -----

    public void EntryChar(char c)
    {
        var ok = Stage switch
        {
            RegisterStage.Scan => char.IsLetterOrDigit(c),
            RegisterStage.CashTender => char.IsDigit(c),
            _ => false,
        };
        if (!ok) return;
        EntryBuffer += c;
        OnPropertyChanged(nameof(ScanEntryDisplay));
        OnPropertyChanged(nameof(CashEntryDisplay));
    }

    public void EntryBackspace()
    {
        if (EntryBuffer.Length == 0) return;
        EntryBuffer = EntryBuffer[..^1];
        OnPropertyChanged(nameof(ScanEntryDisplay));
        OnPropertyChanged(nameof(CashEntryDisplay));
    }

    public void EntrySubmit()
    {
        switch (Stage)
        {
            case RegisterStage.Scan:
                var upc = EntryBuffer.Trim();
                ClearEntry();
                if (upc.Length > 0) ScanUpc(upc);
                break;

            case RegisterStage.CashTender:
                var tendered = CashEntryCents / 100m;
                if (tendered + 0.005m < Total)
                {
                    Status = tendered == 0 ? "" : "Cash tendered is less than the amount due";
                    return;
                }
                ClearEntry();
                CompleteSale("Cash", Total);
                break;

            case RegisterStage.Complete:
                NewSale();
                break;
        }
    }

    private void ClearEntry()
    {
        EntryBuffer = "";
        OnPropertyChanged(nameof(ScanEntryDisplay));
        OnPropertyChanged(nameof(CashEntryDisplay));
    }

    // ----- Items page (T5): tap-to-add for the demo -----

    public IReadOnlyList<Product> CatalogItems => Catalog;

    [RelayCommand]
    private void OpenItems()
    {
        if (Stage is not (RegisterStage.Loyalty or RegisterStage.Scan)) return;
        ShowItems = true;
    }

    [RelayCommand]
    private void CloseItems() => ShowItems = false;

    [RelayCommand]
    private void AddCatalogItem(Product product)
    {
        // Tapping an item from the loyalty prompt implies bypassing loyalty.
        if (Stage == RegisterStage.Loyalty)
        {
            Stage = RegisterStage.Scan;
        }
        ScanUpc(product.Sku);
    }

    public void ScanUpc(string upc)
    {
        var product = Catalog.FirstOrDefault(p => p.Sku == upc);
        if (product is null)
        {
            Status = $"UPC {upc} not on file";
            return;
        }
        var line = new SaleLine(_uid++, product);
        Lines.Add(line);
        SelectLineInternal(line);
        Status = $"{product.Name} added";
        Refresh();
    }

    [RelayCommand]
    private void SelectLine(SaleLine line) => SelectLineInternal(line);

    private void SelectLineInternal(SaleLine line)
    {
        foreach (var l in Lines) l.IsSelected = l == line;
        _selected = line;
    }

    /// <summary>Delete key — void the selected line (scan stage only).</summary>
    [RelayCommand]
    private void DeleteLine()
    {
        if (ShowSetup || ShowItems || Stage != RegisterStage.Scan || _selected is null) return;
        Lines.Remove(_selected);
        _selected = Lines.LastOrDefault();
        if (_selected is not null) SelectLineInternal(_selected);
        Status = "Item deleted";
        Refresh();
    }

    // ----- F-keys / Esc -----

    /// <summary>F3 — cancel the whole transaction from any stage.</summary>
    [RelayCommand]
    private async Task CancelTransactionAsync()
    {
        if (ShowSetup) return;
        await CancelTerminalPaymentAsync();
        NewSale();
        Status = "Transaction canceled";
    }

    /// <summary>F6 — bypass loyalty and go straight to merchandise.</summary>
    [RelayCommand]
    private void Bypass()
    {
        if (ShowSetup || Stage != RegisterStage.Loyalty) return;
        Stage = RegisterStage.Scan;
        Refresh();
    }

    /// <summary>F8 — suspend (mocked; the demo never recalls).</summary>
    [RelayCommand]
    private void Suspend()
    {
        if (Stage is RegisterStage.Scan or RegisterStage.Checkout or RegisterStage.MorePayments)
        {
            Status = "Transaction suspended";
        }
    }

    /// <summary>Esc — stage-appropriate step back.</summary>
    [RelayCommand]
    private async Task EscapeAsync()
    {
        if (ShowItems)
        {
            ShowItems = false;
            return;
        }
        if (ShowSetup)
        {
            ShowSetup = false;
            return;
        }

        switch (Stage)
        {
            case RegisterStage.Checkout:
            case RegisterStage.MorePayments:
                Stage = RegisterStage.Scan; // Return to Merchandise Section
                Refresh();
                break;
            case RegisterStage.CashTender:
                Stage = RegisterStage.MorePayments; // Change Form of Payment
                ClearEntry();
                break;
            case RegisterStage.CardTender:
                await CancelTerminalPaymentAsync();
                Stage = RegisterStage.Checkout; // Change Form of Payment
                Refresh();
                break;
            case RegisterStage.SignatureWait:
                SignatureSecondsLeft = 89; // more time
                break;
        }
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
        ShowSetup = true;
    }

    // ----- Card tender (customer terminal / WinkPay) -----

    /// <summary>
    /// T1 (Bloomingdale's Card / Pay → PxRetailer payment-options page, where
    /// the customer can pick face/palm) or T2 (bankcard → straight EMV).
    /// With no terminal connected the flow is simulated so the demo still runs.
    /// </summary>
    private void StartCardTender(string method)
    {
        Stage = RegisterStage.CardTender;
        Refresh();

        if (_link is { IsConnected: true })
        {
            _terminalOrderId = $"{TxnId}-{Payments.Count + 1}";
            IsAwaitingTerminal = true;
            _ = Task.Run(async () =>
            {
                var sent = await _link.SendAsync(new PosMessage
                {
                    Type = PosMessageTypes.StartPayment,
                    OrderId = _terminalOrderId,
                    AmountCents = (long)Math.Round(Balance * 100),
                    Currency = "USD",
                    Method = method,
                });
                if (!sent)
                {
                    OnUiThread(() =>
                    {
                        IsAwaitingTerminal = false;
                        _terminalOrderId = null;
                        Status = "Customer terminal unreachable";
                    });
                }
            });
        }
        else
        {
            // Standalone mock: card goes in, then the signature screen.
            ScheduleFlow(TimeSpan.FromSeconds(2.5), () =>
            {
                if (Stage != RegisterStage.CardTender) return;
                Stage = RegisterStage.SignatureWait;
                SignatureSecondsLeft = 89;
                _signatureTimer.Start();
                ScheduleFlow(TimeSpan.FromSeconds(4), () =>
                {
                    if (Stage != RegisterStage.SignatureWait) return;
                    _signatureTimer.Stop();
                    CompleteSale("Bloomingdale's Card", Total);
                });
            });
        }
    }

    private async Task CancelTerminalPaymentAsync()
    {
        _flowTimer.Stop();
        _flowTimerAction = null;
        _signatureTimer.Stop();
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
    }

    private void HandleTerminalMessage(PosMessage m)
    {
        // A tender button on the PxRetailer form (PAYMENTSTATUS FireEvent):
        // face/palm hands the sale to the WinkPay app, card starts EMV.
        if (m.Type == PosMessageTypes.TenderSelected)
        {
            var method = m.Method switch
            {
                "FACE" => "FACE",
                "PALM" => "PALM",
                "CARD" or "CREDIT" or "DEBIT" => "CARD",
                _ => null,
            };
            if (method is null) return;

            if (Lines.Count == 0)
            {
                Status = "Customer picked a tender but the basket is empty";
                return;
            }

            // The customer can start the tender themselves from the terminal;
            // follow along on the register instead of dropping the event.
            if (!IsAwaitingTerminal)
            {
                _terminalOrderId = $"{TxnId}-{Payments.Count + 1}";
                IsAwaitingTerminal = true;
                Stage = RegisterStage.CardTender;
                Refresh();
            }

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
                CompleteSale(TenderLabelFor(m.Method), Math.Min(amount, Balance));
                break;
            case "DECLINED":
                Status = m.Reason ?? "Payment declined on customer terminal";
                Stage = RegisterStage.Checkout;
                Refresh();
                break;
            case "CANCELLED":
                Status = "Payment cancelled on customer terminal";
                Stage = RegisterStage.Checkout;
                Refresh();
                break;
        }
    }

    private static string TenderLabelFor(string? method) => method switch
    {
        "FACE" or "PALM" or "WINK" => "Bloomingdale's Pay",
        "CASH" => "Cash",
        null => "Bloomingdale's Card",
        _ => "Bloomingdale's Card",
    };

    // ----- Complete / new sale -----

    private void CompleteSale(string label, decimal amount)
    {
        _signatureTimer.Stop();
        Payments.Add(new Payment(label, "", amount));
        Stage = RegisterStage.Complete;
        Status = "";
        Refresh();

        // The real register bounces back to the loyalty prompt on its own.
        ScheduleFlow(TimeSpan.FromSeconds(6), NewSale);
    }

    [RelayCommand]
    private void NewSale()
    {
        _flowTimer.Stop();
        _flowTimerAction = null;
        _signatureTimer.Stop();
        _txnBase++;
        Lines.Clear();
        Payments.Clear();
        CustomerName = null;
        _selected = null;
        ShowItems = false;
        IsAwaitingTerminal = false;
        _terminalOrderId = null;
        ClearEntry();
        Status = "";
        Stage = RegisterStage.Loyalty;
        Refresh();
    }

    private void ScheduleFlow(TimeSpan delay, Action action)
    {
        _flowTimer.Stop();
        _flowTimerAction = action;
        _flowTimer.Interval = delay;
        _flowTimer.Start();
    }

    // ----- Live cart mirroring -----

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

    private string? _lastCartJson;

    /// <summary>
    /// Debounced push of the basket to the customer terminal. Called from
    /// Refresh() so every mutation (scan, delete, new sale) lands on the
    /// PxRetailer screen without a manual send. Skipped while a payment is
    /// in flight so we don't stomp the EMV screens.
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

    // ----- Helpers -----

    private void Refresh()
    {
        foreach (var name in DerivedProps) OnPropertyChanged(name);
        QueueCartSync();
    }

    private static readonly string[] DerivedProps =
    {
        nameof(Subtotal), nameof(Tax), nameof(Total), nameof(Balance),
        nameof(SubtotalDisplay), nameof(TaxDisplay), nameof(TotalDisplay), nameof(AmountDueDisplay),
        nameof(PurchItemsLabel), nameof(CouponsLabel), nameof(HasItems), nameof(TxnId), nameof(TKeys),
        nameof(CustomerLinked), nameof(LoyaltyNumberDisplay),
    };

    private void TickClock()
    {
        var now = DateTime.Now;
        DateText = now.ToString("MM/dd");
        TimeText = now.ToString("h:mm tt");
    }

    private static void OnUiThread(Action action) => Dispatcher.UIThread.Post(action);
}
