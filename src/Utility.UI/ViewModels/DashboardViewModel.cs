using System.Collections.ObjectModel;
using Utility.Server.DropBoxSync;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// The synced Dropbox tree, and nothing else.
/// <para>
/// The tree lives in the singleton <see cref="IDropBoxTreeState"/>, so navigating to this
/// page does NOT re-read it - it is loaded once at startup, or when the toolbar's
/// Read/Refresh button asks for it. The page itself never queries anything on activation.
/// </para>
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    /// <summary>
        /// Bumped whenever the expansion state changes. The view watches this and syncs the
        /// generated TreeViewItem containers, because WPF resets IsExpanded while preparing a
        /// container and a style binding cannot win that race.
        /// </summary>
        private int _expansionVersion;

        public int ExpansionVersion
                {
                    get => _expansionVersion;
                    private set
                    {
                        _expansionVersion = value;

                        // Named explicitly: a parameterless OnPropertyChanged() would take the name
                        // of THIS setter ("value") via CallerMemberName, and the view listens for
                        // ExpansionVersion - so Expand all would silently never trigger a sync.
                        OnPropertyChanged(nameof(ExpansionVersion));
                    }
                }
    private readonly IDropBoxTreeState _state;
    private readonly ILogSink _log;

    private AsyncRelayCommand? _readTree;
    private AsyncRelayCommand? _expandAll;
    private AsyncRelayCommand? _collapseAll;

    public DashboardViewModel(IDropBoxTreeState state, ILogSink log)
    {
        _state = state;
        _log = log;

        // The tree is read once at startup, which happens BEFORE this view model exists.
        // Subscribe so the page reflects whatever the warm-up already produced, and pull
        // the current values once in case the warm-up finished before this was created.
        state.StateChanged += (_, _) => PublishState();
        PublishState();
    }

    /// <summary>Bound directly: the collection instance never changes, only its contents.</summary>
    public ObservableCollection<DropBoxTreeNode> Nodes => _state.Nodes;

    public AsyncRelayCommand ReadTreeCommand => _readTree ??= new AsyncRelayCommand(() => ReadTreeAsync(force: true));

    public AsyncRelayCommand ExpandAllCommand => _expandAll ??= new AsyncRelayCommand(() =>
            {
                SetTreeExpanded(_state.Nodes, depthLimit: -1);
                ExpansionChanged();
                return Task.CompletedTask;
            });

        public AsyncRelayCommand CollapseAllCommand => _collapseAll ??= new AsyncRelayCommand(() =>
        {
            SetTreeExpanded(_state.Nodes, depthLimit: 1);
            ExpansionChanged();
            return Task.CompletedTask;
        });

        /// <summary>Tells the view to push the model's expansion onto the generated containers.</summary>
            private void ExpansionChanged() => ExpansionVersion++;

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