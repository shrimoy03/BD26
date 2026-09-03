using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MerchantTerminal.ViewModels;

/// <summary>Brush pickers for the AYS register replica.</summary>
public static class Converters
{
    private static readonly IBrush LineSelected = new SolidColorBrush(Color.Parse("#55534A"));
    private static readonly IBrush KeyOutline = new SolidColorBrush(Color.Parse("#E7CFC4"));
    private static readonly IBrush KeyTextDim = new SolidColorBrush(Color.Parse("#C4888D"));

    /// <summary>Selected basket line gets the thin focus box the AYS draws.</summary>
    public static readonly IValueConverter SelectedLineBrush =
        new FuncValueConverter<bool, IBrush>(selected => selected ? LineSelected : Brushes.Transparent);

    /// <summary>Enabled T-keys carry a pale inner outline.</summary>
    public static readonly IValueConverter TKeyOutlineBrush =
        new FuncValueConverter<bool, IBrush>(enabled => enabled ? KeyOutline : Brushes.Transparent);

    /// <summary>Idle T-keys dim their "Tn" prefix into the red.</summary>
    public static readonly IValueConverter TKeyTextBrush =
        new FuncValueConverter<bool, IBrush>(enabled => enabled ? Brushes.White : KeyTextDim);
}
