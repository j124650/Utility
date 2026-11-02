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
    private readonly SyncNewViewModel _syncNew;

    private AsyncRelayCommand? _readTree;
    private AsyncRelayCommand? _runSync;
    private AsyncRelayCommand? _runSyncNew;
    private AsyncRelayCommand? _runEnrichOnly;
    private AsyncRelayCommand? _continueSync;
    private AsyncRelayCommand? _refreshStats;

    public DashboardViewModel(
        IDropBoxTreeState state,
        IDropBoxSyncService sync,
        IDropBoxSyncStatsService stats,
        ILogSink log,
        SyncNewViewModel syncNew)
    {
        _state = state;
        _sync = sync;
        _stats = stats;
        _log = log;
        _syncNew = syncNew;

        // The figures are read at startup, which happens BEFORE this view model exists.
        // Subscribe so the page reflects whatever the warm-up already produced, and pull
        // the current values once in case the warm-up finished before this was created.
        _stats.PropertyChanged += (_, _) => PublishStats();
        PublishStats();

        // The button's enabled state depends on whether a walk is running, and the run
        // state lives in the sync service. The service raises PropertyChanged for it, so
        // the button's state follows the run without anyone copying the flag around.
        _sync.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanContinueSync));
            OnPropertyChanged(nameof(CanRunSync));
        };

        // The two-phase run lives in its own view model, which is a singleton so the
        // busy screen outlives this page. Its state decides whether the button is live.
        _syncNew.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanRunSync));
    }

    /// <summary>
    /// The two-phase sync's busy screen and its run. A singleton, so the run's state
    /// survives the page being navigated away from.
    /// </summary>
    public SyncNewViewModel SyncNew => _syncNew;

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

    /// <summary>
    /// Carries an unfinished sync to the end.
    /// <para>
    /// This is NOT a retry of the folders that failed. A folder whose listing was
    /// refused (404, no access) will be refused again, and re-listing it changes
    /// nothing.
    /// </para>
    /// <para>
    /// What it does is find the FRONTIER: the folders the previous walk wrote but never
    /// expanded. Their contents were never listed, and everything below them was never
    /// discovered - so the tree is missing that whole branch, not just those folders.
    /// Listing them, and then walking down into whatever they contain, is what makes
    /// the sync complete.
    /// </para>
    /// </summary>
    public AsyncRelayCommand ContinueSyncCommand => _continueSync ??= new AsyncRelayCommand(ContinueSyncAsync);

    /// <summary>
    /// True when there is something to continue: folders whose contents were never
    /// listed. This is the same set the button acts on.
    /// </summary>
    public bool CanContinueSync => Stats.HasNotSynced && !Sync.IsRunning && !_syncNew.IsBusy;

    /// <summary>
    /// Runs the two-phase sync: the structure, then whatever it marked for enrichment.
    /// <para>
    /// It reads the SAME path box as the one-phase sync above it, so both buttons act
    /// on the folder the user typed.
    /// </para>
    /// </summary>
    public AsyncRelayCommand RunSyncNewCommand => _runSyncNew ??= new AsyncRelayCommand(RunSyncNewAsync);

    /// <summary>
    /// Runs the enrichment pass on its own: the files a previous sync marked as
    /// changed, and nothing else.
    /// <para>
    /// It needs no path - the work list is the set of rows tagged 'pending' - so the
    /// path box does not apply to it.
    /// </para>
    /// </summary>
    public AsyncRelayCommand RunEnrichOnlyCommand => _runEnrichOnly ??= new AsyncRelayCommand(RunEnrichOnlyAsync);

    /// <summary>
    /// True when a sync may be started: neither the one-phase sync nor the two-phase
    /// one is running. Two walks over the same table would fight each other.
    /// </summary>
    public bool CanRunSync => !Sync.IsRunning && !_syncNew.IsBusy;

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
    private async Task ContinueSyncAsync()
    {
        var result = await _sync.ContinueAsync();

        // Acknowledged for the same reason as the one-phase sync above: the outcome is
        // reported on the page's status line, and the flag it would leave behind keeps
        // the service's IsBusy set - which the app's blur is bound to.
        _sync.AcknowledgeResult();

        if (result.Completed)
        {
            StatusMessage = $"Listed {result.FoldersWritten:N0} folder(s), {result.FilesWritten:N0} file(s).";
            _log.Info(StatusMessage);
        }
        else
        {
            // Cancelled or failed: the service already carries the reason.
            StatusMessage = result.Error ?? "The sync did not complete.";
            _log.Warn(StatusMessage);
        }

        // The figures are read here too, not only when the busy screen is dismissed:
        // the user may dismiss it, and the button's own state depends on the count.
        await _stats.LoadAsync();
    }

    /// <summary>
    /// Runs the enrichment pass on its own, and reads the figures again when it ends.
    /// </summary>
    private async Task RunEnrichOnlyAsync()
    {
        await _syncNew.EnrichOnlyAsync();

        // The figures are read here: the run wrote dimensions the page counts.
        await _stats.LoadAsync();
        OnPropertyChanged(nameof(CanContinueSync));

        var message = _syncNew.Error ?? _syncNew.Outcome;

        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
            _log.Info(message);
        }
    }

    /// <summary>
    /// Runs the two-phase sync and reads the figures again when it ends.
    /// </summary>
    private async Task RunSyncNewAsync()
    {
        var path = SyncPath?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "A sync needs a folder path.";
            return;
        }

        await _syncNew.StartAsync(path);

        // The figures are read here, not only when the busy screen is dismissed: the
        // run wrote rows the page counts, and the button's own state depends on them.
        await _stats.LoadAsync();
        OnPropertyChanged(nameof(CanContinueSync));

        var message = _syncNew.Error ?? _syncNew.Outcome;

        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
            _log.Info(message);
        }
    }

    private async Task RunSyncAsync()
    {
        var path = SyncPath?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "A sync needs a folder path.";
            return;
        }

        var result = await _sync.SyncAsync(path);

        // The service keeps a finished run's result so the busy screen can show it
        // until it is dismissed - and the service's IsBusy covers "a result waiting to
        // be acknowledged". The screen that would dismiss it is collapsed in the
        // markup, so nothing ever clears the flag, and the app stays blurred for the
        // rest of the session.
        //
        // The outcome is reported on the page's status line below, so the result has
        // been seen: acknowledging it is what clears the flag.
        _sync.AcknowledgeResult();

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

        // Three states, not two: the figures are known, they are being read, or they
        // are unknown. "Unknown" must not look like "all good".
        Stats = stats is null
            ? DropBoxSyncStatsView.NotRead(_stats.IsLoading, _stats.Error)
            : DropBoxSyncStatsView.From(stats);

        OnPropertyChanged(nameof(Stats));
        OnPropertyChanged(nameof(HasTree));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(TreeError));
        OnPropertyChanged(nameof(CanContinueSync));
    }
}
