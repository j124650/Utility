using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(IServiceProvider services)
    {
        InitializeComponent();

        // SettingsViewModel is transient, so every page instance owns its own MlDbContext and
        // disposing it on navigation is safe.
        DataContext = services.GetRequiredService<SettingsViewModel>();
    }
}
