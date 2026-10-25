using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class LiemListView : UserControl
{
    public LiemListView(IServiceProvider services)
    {
        InitializeComponent();

        // LiemListViewModel is transient, so every page instance owns its own services and
        // disposing it on navigation is safe.
        DataContext = services.GetRequiredService<LiemListViewModel>();
    }
}
