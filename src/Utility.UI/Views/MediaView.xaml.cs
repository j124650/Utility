using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class MediaView : UserControl
{
    public MediaView(IServiceProvider services)
    {
        InitializeComponent();

        // MediaViewModel is transient, so every page instance owns its own MlDbContext and
        // disposing it on navigation is safe.
        DataContext = services.GetRequiredService<MediaViewModel>();
    }
}
