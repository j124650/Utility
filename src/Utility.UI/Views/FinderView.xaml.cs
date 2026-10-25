using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class FinderView : UserControl
{
    public FinderView(IServiceProvider services)
    {
        InitializeComponent();

        // The app-wide DataGrid style fixes the row height at 26 px, which is right for the
        // flat grids and wrong for this one: a record is a card of seven lines (§18.4), and
        // a fixed height clips it to the first line. NaN is the DataGrid's "size the row to
        // its content" value - it cannot be written in XAML, because RowHeight is a plain
        // double and "Auto" is not a number.
        PairsGrid.RowHeight = double.NaN;

        // The view model is a singleton (§14.1), so the rows, the filter and the selected
        // row survive switching tabs. The view itself is transient - it is just a view.
        DataContext = services.GetRequiredService<FinderViewModel>();
    }
}
