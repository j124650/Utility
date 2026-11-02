using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Utility.DropBox.Models;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class DropBoxView : UserControl, IDisposable
{
    public DropBoxView(IServiceProvider services)
    {
        InitializeComponent();

        // DropBoxViewModel is transient: this page owns its own instance and state.
        DataContext = services.GetRequiredService<DropBoxViewModel>();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        (DataContext as DropBoxViewModel)?.SelectEntry(FilesGrid.SelectedItem as FileEntry);

    private async void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DropBoxViewModel vm && FilesGrid.SelectedItem is FileEntry entry)
        {
            await vm.OpenEntryAsync(entry);
        }
    }

    /// <summary>Enter in the path box navigates, matching the Go button.</summary>
    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not DropBoxViewModel vm)
        {
            return;
        }

        e.Handled = true;
        await vm.GoToPathAsync();
    }

    /// <summary>
    /// Releases the view model.
    /// <para>
    /// The view model listens to the sync services, which are singletons — so without
    /// this the page's handler would outlive the page and keep it alive. The window
    /// disposes the page it navigates away from.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        (DataContext as IDisposable)?.Dispose();
        DataContext = null;
    }
}