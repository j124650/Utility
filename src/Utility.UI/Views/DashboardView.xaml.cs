using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Utility.Server.DropBoxSync;
using Utility.UI.Services;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class DashboardView : UserControl
{
    private readonly DashboardViewModel _vm;

    public DashboardView(IServiceProvider services, IDropBoxTreeState state)
    {
        InitializeComponent();

        _vm = services.GetRequiredService<DashboardViewModel>();
        DataContext = _vm;

        // The tree is read once at startup, before this page exists, so the expansion has to
        // be re-applied whenever the shared node collection changes.
        state.Nodes.CollectionChanged += (_, _) => ScheduleSync();

        // Loaded fires BEFORE the containers are generated. Wait for the generator instead of
        // guessing at dispatcher priorities - it is the only signal that reliably tells us
        // the visual containers actually exist.
        Loaded += (_, _) => WaitForContainers();
    }

    /// <summary>
    /// The tree control, if this page still has one.
    /// <para>
    /// The TreeView is COMMENTED OUT of the XAML for now - the page shows the index's
    /// figures instead - so this is null and every tree operation below returns
    /// immediately. It is looked up BY NAME rather than used as the generated field, so
    /// uncommenting the TreeView brings all of this machinery back with no code change:
    /// the name is registered again and the lookup finds it.
    /// </para>
    /// </summary>
    private TreeView? TreeControl => this.FindName("Tree") as TreeView;

    /// <summary>
    /// Polls (briefly, on the dispatcher) until the ItemContainerGenerator reports that
    /// containers exist, then applies the expansion. Guessing with dispatcher priorities
    /// does not work: the extra passes still ran before the first layout pass created
    /// anything, so the tree came back collapsed.
    /// </summary>
    private void WaitForContainers(int attempt = 0)
    {
        var tree = TreeControl;
        if (tree is null)
        {
            return;
        }

        _vm.ApplyDefaultExpansion();

        var ready = tree.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated ||
                    tree.ItemContainerGenerator.ContainerFromItem(_vm.Nodes.FirstOrDefault()) is not null;

        if (ready || attempt >= 12)
        {
            SyncExpansion();
            return;
        }

        Dispatcher.BeginInvoke(
            () => WaitForContainers(attempt + 1),
            DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Defers to the dispatcher queue so the containers exist before they are touched.
    /// <para>
    /// Expansion is applied to the GENERATED TreeViewItems, not through a style binding:
    /// WPF resets IsExpanded while preparing a container, so a bound value loses that race
    /// and the node never opens. That is why "Expand all" appeared to do nothing.
    /// </para>
    /// </summary>
    private void ScheduleSync()
    {
        _vm.ApplyDefaultExpansion();
        WaitForContainers();
    }

    private void SyncExpansion()
    {
        var tree = TreeControl;
        if (tree is null)
        {
            return;
        }

        // Each pass opens one more level, because a child's container is only generated once its
        // parent is open. Yield to the dispatcher between passes so container generation
        // actually runs - a synchronous loop only ever sees the stale set. Repeat until a
        // pass changes nothing, bounded so a pathological tree cannot spin.
        for (var pass = 0; pass < 64 && ApplyToContainers(tree); pass++)
        {
            Dispatcher.BeginInvoke(SyncExpansion, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Expands or collapses everything.
    /// <para>
    /// Operates on the GENERATED containers, not the model. The model alone cannot express
    /// "expand all": a child container simply does not exist until its parent is open, so
    /// setting IsExpanded on the model reaches nothing. Each pass opens the current
    /// containers, which realizes the next level, and the next pass opens that - until a
    /// pass finds nothing left to change. TreeViewItem.Loaded then applies the state to the
    /// newly realized containers.
    /// </para>
    /// </summary>
    internal void ExpandOrCollapseAll(bool expand)
    {
        var tree = TreeControl;
        if (tree is null)
        {
            return;
        }

        var changed = SetContainers(tree, expand, maxDepth: 64, depth: 0);

        // Re-schedule ONLY when expanding. Expanding a container realizes its children, so
        // the next pass has more to open and progress is real. Collapsing destroys the
        // children, so the generator keeps reporting stale containers and every pass claims
        // "changed" - re-scheduling there was an infinite loop that hung the UI.
        if (expand && changed)
        {
            Dispatcher.BeginInvoke(() => ExpandOrCollapseAll(expand), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Applies <paramref name="expand"/> to every realized container, depth first.
    /// Returns true if anything changed.
    /// </summary>
    private static bool SetContainers(ItemsControl parent, bool expand, int maxDepth, int depth)
    {
        var changed = false;

        if (depth > maxDepth)
        {
            return false;
        }

        // ItemContainerGenerator, NOT parent.Items. parent.Items holds the bound DATA items;
        // the TreeViewItem containers live on the generator.
        foreach (var container in GeneratedContainers(parent).ToList())
        {
            if (container.DataContext is not DropBoxTreeNode)
            {
                continue;
            }

            // Recurse BEFORE changing our own state: once this container collapses, its
            // children are no longer generated and become unreachable.
            if (container.IsExpanded && container.Items.Count > 0 &&
                SetContainers(container, expand, maxDepth, depth + 1))
            {
                changed = true;
            }

            if (container.IsExpanded != expand)
            {
                container.IsExpanded = expand;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Yields the realized TreeViewItem containers of an ItemsControl, top level first.
    /// Children of a container are only generated once it is expanded, so this is naturally
    /// incremental - each pass sees one more level.
    /// </summary>
    private static IEnumerable<TreeViewItem> GeneratedContainers(ItemsControl parent)
    {
        var generator = parent.ItemContainerGenerator;
        if (generator is null || generator.Status == GeneratorStatus.NotStarted)
        {
            yield break;
        }

        var count = parent.Items.Count;

        for (var i = 0; i < count; i++)
        {
            var item = parent.Items[i];
            var container = generator.ContainerFromIndex(i) as TreeViewItem ??
                            generator.ContainerFromItem(item) as TreeViewItem;

            if (container is not null)
            {
                yield return container;
            }
        }
    }

    /// <summary>
    /// Applies the model's expansion to every generated container. Returns true if any
    /// container's state changed OR any container has children that are not realized yet -
    /// a container can be perfectly expanded while its children containers do not exist yet,
    /// which is the case that left the tree half-open after "Expand all".
    /// </summary>
    private bool ApplyToContainers(ItemsControl parent)
    {
        var changed = false;

        foreach (var container in GeneratedContainers(parent).ToList())
        {
            if (container.DataContext is not DropBoxTreeNode node)
            {
                continue;
            }

            if (container.IsExpanded != node.IsExpanded)
            {
                container.IsExpanded = node.IsExpanded;
                changed = true;
            }
            else if (node.IsExpanded && node.Children.Count > 0 && container.Items.Count == 0)
            {
                // Expanded per the model, but its children have not been realized. Another
                // pass is needed - the model wants this open and the UI cannot show it yet.
                changed = true;
            }

            if (container.Items.Count > 0 && ApplyToContainers(container))
            {
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Expand/Collapse all act on the CONTAINERS, not the view model.
    /// <para>
    /// Setting IsExpanded on the model cannot express "expand all": a child container does
    /// not exist until its parent is open, so the model write lands on nothing that is
    /// rendered. The probe log confirmed the command fired and the model was already correct
    /// while the UI stayed shut. Working on the containers - repeatedly, as each pass
    /// realizes the next level - is what actually opens the tree.
    /// </para>
    /// </summary>
    private void OnExpandAllClick(object sender, RoutedEventArgs e) => ExpandOrCollapseAll(expand: true);

    private void OnCollapseAllClick(object sender, RoutedEventArgs e) => ExpandOrCollapseAll(expand: false);

    /// <summary>
    /// Applies the model's expansion the moment a container is realized.
    /// <para>
    /// This is the reliable hook. A container is generated when its parent opens and always
    /// starts collapsed, and WPF overwrites IsExpanded during preparation - so any attempt
    /// to set it from outside (Loaded on the page, a binding, a dispatcher pass) races with
    /// container generation and loses. TreeViewItem.Loaded fires once the container exists
    /// and is final.
    /// </para>
    /// </summary>
    private void OnTreeItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item && item.DataContext is DropBoxTreeNode node)
        {
            item.IsExpanded = node.IsExpanded;
        }
    }

    /// <summary>
    /// Writes a user-driven expansion back to the model, so the model is what a later
    /// <see cref="SyncExpansion"/> pass re-applies.
    /// </summary>
    private void OnTreeItemExpanded(object sender, RoutedEventArgs e) => SetExpanded(e.Source, true);

    /// <summary>
    /// Writes a user-driven collapse back to the model.
    /// <para>
    /// There is no "spurious Collapsed" to filter out. A TreeViewItem is born collapsed, so a
    /// Collapsed event only ever reports a real transition, and <see cref="OnTreeItemLoaded"/>
    /// re-applies the model the moment the container is final - that is what makes the default
    /// expansion stick. The previous guard re-asserted IsExpanded whenever the model still said
    /// open, which blocked every collapse: a clicked expander collapsed and was immediately
    /// reopened, and "Collapse all" did nothing at all.
    /// </para>
    /// </summary>
    private void OnTreeItemCollapsed(object sender, RoutedEventArgs e) => SetExpanded(e.Source, false);

    /// <summary>
    /// Writes one node's expansion to the model.
    /// <para>
    /// Takes <paramref name="source"/> (the event's Source), never the handler's sender.
    /// Expanded/Collapsed are BUBBLING routed events, so this handler runs once per
    /// TreeViewItem ancestor and sender is that ancestor - writing back from sender would
    /// collapse the whole ancestor chain when the user closes a single deep node.
    /// </para>
    /// </summary>
    private void SetExpanded(object source, bool expanded)
    {
        if (source is TreeViewItem item && item.DataContext is DropBoxTreeNode node)
        {
            _vm.SetNodeExpanded(node, expanded);
        }
    }
}
