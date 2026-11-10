using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace ClipBar.App.Converters;

/// <summary>
/// Picks the WPF-UI appearance for a filter chip: the chip representing the active filter uses
/// <see cref="ControlAppearance.Primary"/> (accent fill), the others stay <see cref="ControlAppearance.Secondary"/>
/// so they read as quiet pills on the panel surface.
/// <para>
/// This exists because the chips are ordinary <c>ui:Button</c>s now (the same template as the
/// settings window's buttons), and a button has no IsSelected state to trigger on.
/// </para>
/// Values: the item's <c>FilterOption.Value</c>, the view model's <c>SelectedFilter</c>.
/// </summary>
public sealed class FilterAppearanceConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return ControlAppearance.Secondary;
        }

        var itemFilter = values[0] is ClipBar.Core.Models.ClipFilter item ? item : (ClipBar.Core.Models.ClipFilter?)null;
        var selected = values[1] is ClipBar.Core.Models.ClipFilter current ? current : (ClipBar.Core.Models.ClipFilter?)null;

        if (itemFilter is null || selected is null)
        {
            return ControlAppearance.Secondary;
        }

        return itemFilter == selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
