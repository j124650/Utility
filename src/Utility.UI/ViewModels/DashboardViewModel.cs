using System.Collections.ObjectModel;
using Utility.Server.DropBoxSync;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// The synced Dropbox tree, and the command that starts a sync.
/// <para>
/// The tree lives in the singleton <see cref="IDropBoxTreeState"/>, so navigating to this
/// page does NOT re-read it - it is loaded once at startup, or when the toolbar's
/// Read/Refresh button asks for it. The page itself never queries anything on activation.
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
    private readonly ILogSink _log;

    private AsyncRelayCommand? _readTree;
    private AsyncRelayCommand? _runSync;

    public DashboardViewModel(IDropBoxTreeState state, IDropBoxSyncService sync, ILogSink log)
    {
        _state = state;
        _sync = sync;
        _log = log;

        // The tree is read once at startup, which happens BEFORE this view model exists.
        // Subscribe so the page reflects whatever the warm-up already produced, and pull
        // the current values once in case the warm-up finished before this was created.
        state.StateChanged += (_, _) => PublishState();
        PublishState();
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

    private int _folders;
    private int _files;
    private int _withDimensions;
    private int _unverified;
    private string _sizeDisplay = "—";
    private string _lastSyncDisplay = "—";

    public string FoldersDisplay => _folders.ToString("N0");

    public string FilesDisplay => _files.ToString("N0");

    public string WithDimensionsDisplay => _withDimensions.ToString("N0");

    public string SizeDisplay
    {
        get => _sizeDisplay;
        private set => SetProperty(ref _sizeDisplay, value);
    }

    public string LastSyncDisplay
    {
        get => _lastSyncDisplay;
        private set => SetProperty(ref _lastSyncDisplay, value);
    }

    /// <summary>Non-null only when some folder was never fully listed.</summary>
    public string UnverifiedDisplay => _unverified > 0 ? $"{_unverified} not fully listed" : "";

    public bool HasUnverified => _unverified > 0;

    /// <summary>
    /// Loads the tree only if it was never loaded. Used at startup; navigation never calls it.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_state.IsLoaded)
        {
            return;
        }

        await ReadTreeAsync(force: false);
    }

    private async Task ReadTreeAsync(bool force)
    {
        IsBusy = true;
        try
        {
            await _state.LoadAsync(force);
            ApplyDefaultExpansion();

            StatusMessage = _state.Error is null
                ? $"{_state.Nodes.Count} root(s), {_folders:N0} folder(s), {_files:N0} file(s)."
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
    /// Pushes the shared state's current values into the bindable properties. Called on
    /// construction and whenever the state changes - the tree may already have been read
    /// at startup, before this view model existed.
    /// </summary>
    private void PublishState()
    {
        var summary = _state.Summary;
        if (summary is not null)
        {
            _folders = summary.Folders;
            _files = summary.Files;
            _withDimensions = summary.WithDimensions;
            _unverified = summary.UnverifiedFolders;
            SizeDisplay = FormatSize(summary.TotalBytes);
            LastSyncDisplay = summary.LastSyncedUtc is null
                ? "—"
                : summary.LastSyncedUtc.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }

        OnPropertyChanged(nameof(HasTree));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(TreeError));
        OnPropertyChanged(nameof(FoldersDisplay));
        OnPropertyChanged(nameof(FilesDisplay));
        OnPropertyChanged(nameof(WithDimensionsDisplay));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(LastSyncDisplay));
        OnPropertyChanged(nameof(UnverifiedDisplay));
        OnPropertyChanged(nameof(HasUnverified));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824)
        {
            return $"{bytes / 1_073_741_824.0:0.##} GB";
        }

        if (bytes >= 1_048_576)
        {
            return $"{bytes / 1_048_576.0:0.##} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:0.##} KB";
        }

        return $"{bytes} B";
    }
}
