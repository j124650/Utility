using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class LiemListView : UserControl
{
    public LiemListView(IServiceProvider services)
    {
        InitializeComponent();

        // The app-wide DataGrid style fixes the row height at 26 px, which clips the card
        // (§18.4) to its first line. NaN is the DataGrid's "size the row to its content"
        // value; it cannot be written in XAML, because RowHeight is a plain double.
        ListGrid.RowHeight = double.NaN;

        // The view model is a singleton (§14.1), so the list survives switching tabs. The
        // view itself is transient - it is just a view.
        DataContext = services.GetRequiredService<LiemListViewModel>();
    }
}
