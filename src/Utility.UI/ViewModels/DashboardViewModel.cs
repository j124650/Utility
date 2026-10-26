using System.Collections.ObjectModel;
using Utility.Server.DropBoxSync;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// The synced Dropbox tree, and the command that starts a sync.
/// <para>
/// The tree lives in the singleton <see cref="IDropBoxTreeState"/>. It is NOT loaded
/// automatically: the page never queries anything on activation, and the app's startup
/// warm-up does not read it either. The tree is read only when the user presses
/// Read / Refresh, and that read is kept in the singleton, so navigating away and back
/// shows it again without another query.
/// </para>
/// <para>
/// <b>The sync service is bound directly.</b> <see cref="IDropBoxSyncService"/> owns the
/// run state and raises PropertyChanged for it, so the toolbar and the busy screen bind
/// to that object. No second service copies the progress record out of it.
/// </para>
/// <para>
/// There are deliberately NO expand/collapse commands here. Expanding "everything" is a
/// container-level operation - a child TreeViewItem does not exist until its parent is open,
/// so a model-only write lands on nothing that is rendered. That machinery belongs to the
/// view, and the view exposes it as commands of its own.
/// </para>
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    /// <summary>
    /// The folder the sync starts from.
    /// <para>
    /// Fixed, not derived from the tree: this is the scope the user asked for, and it is
    /// the same value on every run. It is the Virtual Catalog branch - the brand tree
    /// under LightBoxPics - because that is the part of the team folder this app
    /// actually works with; the level above it holds unrelated collections.
    /// </para>
    /// <para>
    /// Spelled exactly as Dropbox reports it, including the space in "Virtual Catalog".
    /// </para>
    /// </summary>
    private const string DefaultSyncPath = "/Images-Catalogs Team Folder/LightBoxPics/Virtual Catalog";

    private readonly IDropBoxTreeState _state;
    private readonly IDropBoxSyncService _sync;
    private readonly IDropBoxSyncStatsService _stats;
    private readonly ILogSink _log;

    private AsyncRelayCommand? _readTree;
    private AsyncRelayCommand? _runSync;
    private AsyncRelayCommand? _refreshStats;

    public DashboardViewModel(
        IDropBoxTreeState state,
        IDropBoxSyncService sync,
        IDropBoxSyncStatsService stats,
        ILogSink log)
    {
        _state = state;
        _sync = sync;
        _stats = stats;
        _log = log;

        // The figures are read at startup, which happens BEFORE this view model exists.
        // Subscribe so the page reflects whatever the warm-up already produced, and pull
        // the current values once in case the warm-up finished before this was created.
        _stats.PropertyChanged += (_, _) => PublishStats();
        PublishStats();
    }

    /// <summary>Bound directly: the collection instance never changes, only its contents.</summary>
    public ObservableCollection<DropBoxTreeNode> Nodes => _state.Nodes;

    /// <summary>
    /// The sync service, exposed so the toolbar binds straight to its live state.
    /// <para>
    /// Bound as an object rather than copied into properties on purpose: the service is a
    /// singleton whose state outlives this page, and the binding engine subscribes to it
    /// directly - so no handler has to be attached and detached as pages come and go.
    /// </para>
    /// </summary>
    public IDropBoxSyncService Sync => _sync;

    /// <summary>
    /// The index's figures, shaped for display.
    /// <para>
    /// A snapshot object rather than loose properties: the not-synced folders are a LIST,
    /// and a list cannot be a property change on this view model without a collection
    /// that raises its own notifications.
    /// </para>
    /// </summary>
    public DropBoxSyncStatsView Stats { get; private set; } = DropBoxSyncStatsView.Empty;

    /// <summary>
    /// Reads the figures again. This is what the toolbar's Read / Refresh button does:
    /// the tree read it used to run took twenty minutes and is no longer what the page
    /// is about.
    /// </summary>
    public AsyncRelayCommand RefreshStatsCommand =>
        _refreshStats ??= new AsyncRelayCommand(RefreshStatsAsync);

    /// <summary>
    /// Reads the tree. Still here for when the tree view comes back - nothing on the
    /// page invokes it now.
    /// </summary>
    public AsyncRelayCommand ReadTreeCommand => _readTree ??= new AsyncRelayCommand(() => ReadTreeAsync(force: true));

    /// <summary>
    /// Walks the configured Dropbox folder and stores it in the synced table.
    /// <para>
    /// The walk always covers the whole subtree: no depth limit, and every file's own
    /// metadata is fetched, because that is the only route to pixel dimensions.
    /// </para>
    /// </summary>
    public AsyncRelayCommand RunSyncCommand => _runSync ??= new AsyncRelayCommand(RunSyncAsync);

    private string _syncPath = DefaultSyncPath;

    /// <summary>
    /// The Dropbox folder to sync, relative to the team namespace root.
    /// </summary>
    public string SyncPath
    {
        get => _syncPath;
        set => SetProperty(ref _syncPath, value);
    }

    /// <summary>
    /// Records a user-driven expansion on the model. No notification is raised: the view
    /// already reflects the click, and re-running the default expansion would undo exactly
    /// what the user just did.
    /// </summary>
    public void SetNodeExpanded(DropBoxTreeNode node, bool expanded) => node.IsExpanded = expanded;

    public bool HasTree => _state.Nodes.Count > 0;

    public bool IsEmpty => _state.Nodes.Count == 0;

    public bool IsLoading => _state.IsBusy;

    public string? TreeError => _state.Error;

    /// <summary>
    /// Reads the figures and publishes them.
    /// </summary>
    private async Task RefreshStatsAsync()
    {
        await _stats.LoadAsync();

        StatusMessage = _stats.Error is null
            ? $"{_stats.Stats?.TotalObjects ?? 0:N0} synced object(s)."
            : $"Index stats failed: {_stats.Error}";

        _log.Info($"Dashboard stats: {StatusMessage}");
    }

    private async Task ReadTreeAsync(bool force)
    {
        IsBusy = true;
        try
        {
            await _state.LoadAsync(force);
            ApplyDefaultExpansion();

            StatusMessage = _state.Error is null
                ? $"{_state.Nodes.Count} root(s), {_state.Nodes.Count:N0} node(s)."
                : $"Tree read failed: {_state.Error}";

            _log.Info($"Dashboard tree: {StatusMessage}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Runs a sync.
    /// <para>
    /// The busy screen that appears over the whole window carries the live progress and
    /// the Stop button, so nothing has to be reported here. The tree is re-read when that
    /// screen is dismissed, which the window does.
    /// </para>
    /// </summary>
    private async Task RunSyncAsync()
    {
        var path = SyncPath?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "A sync needs a folder path.";
            return;
        }

        var result = await _sync.SyncAsync(path);

        if (result.Completed)
        {
            StatusMessage = $"Synced {result.FoldersWritten:N0} folder(s), {result.FilesWritten:N0} file(s).";
            _log.Info(StatusMessage);
        }
        else
        {
            // Cancelled or failed: the service already carries the reason.
            StatusMessage = result.Error ?? "The sync did not complete.";
            _log.Warn(StatusMessage);
        }
    }

    /// <summary>
    /// Applies the default expansion to the MODEL only. The view then copies it onto the
    /// generated containers, which is the only way it sticks - WPF resets IsExpanded while
    /// preparing a TreeViewItem, so a style binding loses that race.
    /// <para>
    /// Called on load and after every read, so it deliberately overrides whatever the user
    /// had open: a refresh should present the tree in its standard shape.
    /// </para>
    /// </summary>
    public void ApplyDefaultExpansion() => SetTreeExpanded(_state.Nodes, depthLimit: 2);

    /// <summary>
    /// Sets expansion. <paramref name="depthLimit"/> of -1 opens every level; a positive
    /// value opens only that many. Row height and layout never change, so nothing on the
    /// page shifts when a node expands or collapses.
    /// </summary>
    private static void SetTreeExpanded(IEnumerable<DropBoxTreeNode> nodes, int depthLimit = -1, int depth = 1)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = depthLimit < 0 || depth < depthLimit;
            SetTreeExpanded(node.Children, depthLimit, depth + 1);
        }
    }

    /// <summary>
    /// Pushes the figures into <see cref="Stats"/>. Called on construction and whenever
    /// the stats service changes - the figures are read at startup, before this view model
    /// exists.
    /// </summary>
    private void PublishStats()
    {
        var stats = _stats.Stats;
        Stats = stats is null ? DropBoxSyncStatsView.Empty : DropBoxSyncStatsView.From(stats);

        OnPropertyChanged(nameof(Stats));
        OnPropertyChanged(nameof(HasTree));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(TreeError));
    }
}
