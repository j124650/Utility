using System.Windows;
using Utility.Server.Finder;

namespace Utility.UI.Views;

/// <summary>
/// The window the groups are read in (docs/finder-groups-spec.md §4).
/// </summary>
/// <remarks>
/// <para>
/// A modal window of the app - not a tab and not the reader's browser. It is shown with
/// <see cref="Window.ShowDialog"/> and owned by the main window, so the Finder cannot be
/// used while it is up, and focus returns to the Finder when it closes.
/// </para>
/// <para>
/// The window reads the groups it is given and changes nothing.
/// </para>
/// </remarks>
public partial class GroupsWindow : Window
{
    public GroupsWindow(IReadOnlyList<NameGroup> groups)
    {
        InitializeComponent();

        GroupsGrid.ItemsSource = groups;

        var names = 0;
        foreach (var group in groups)
        {
            names += group.Count;
        }

        CounterText.Text = $"{groups.Count:N0} groups · {names:N0} names";

        // The title states the scope, so the window is identifiable in the task bar and in
        // a screenshot.
        Title = $"Groups - {groups.Count:N0}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
