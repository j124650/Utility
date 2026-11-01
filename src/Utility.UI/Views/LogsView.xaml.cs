using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

/// <summary>
/// The Logs tab: a browser over the logs the app writes.
/// </summary>
public partial class LogsView : UserControl
{
    public LogsView(IServiceProvider services)
    {
        InitializeComponent();

        // The view model is a singleton, so the lists the reader scrolled to, and the log
        // being read, survive switching tabs. The view itself is transient - it is just a
        // view.
        DataContext = services.GetRequiredService<LogsViewModel>();

        // A TreeView's SelectedItem is read-only from markup, so the selection is taken
        // here and handed to the view model - which is what reads the log.
        RecoveryTree.SelectedItemChanged += OnRecoveryTreeSelectionChanged;
    }

    private void OnRecoveryTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is LogsViewModel logs && e.NewValue is LogTreeNode node)
        {
            logs.SelectedNode = node;
        }
    }
}
