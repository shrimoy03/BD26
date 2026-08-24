using CommunityToolkit.Mvvm.ComponentModel;
using MerchantTerminal.Models;

namespace MerchantTerminal.ViewModels;

public partial class PromoRow : ObservableObject
{
    public PromoRow(PromoDef def) => Def = def;

    public PromoDef Def { get; }
    public string Name => Def.Name;
    public string Terms => Def.Terms;
    public string ValueDisplay => Def.ValueDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    public partial bool IsApplied { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    public partial bool IsBlocked { get; set; }

    public string State => IsApplied ? "APPLIED" : IsBlocked ? "UNAVAILABLE" : "APPLY";
}
