using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class FinderView : UserControl
{
    public FinderView(IServiceProvider services)
    {
        InitializeComponent();

        // FinderViewModel is transient, so every page instance owns its own services and
        // disposing it on navigation is safe.
        DataContext = services.GetRequiredService<FinderViewModel>();
    }
}
