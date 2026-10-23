using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Utility.DropBox.Services;
using Utility.Server;
using Utility.Server.DropBoxSync;
using Utility.Server.ML.Entities;
using Utility.Server.ML.Services;
using Utility.UI.Mvvm;

namespace Utility.UI.ViewModels;

/// <summary>
/// Read-only overview: database reachability, the Dropbox account, and a few
/// aggregate figures read from the ml. schema, plus the already-synced Dropbox tree.
/// </summary>
public sealed class DashboardViewModel(
    IDatabaseProbe databaseProbe,
    IMlDataService ml,
    IDropBoxDataService dropBox,
    IDropBoxTreeService dropBoxTree,
    ILogSink log) : ViewModelBase
{
    private AsyncRelayCommand? _refresh;
    private AsyncRelayCommand? _expandAll;
    private AsyncRelayCommand? _collapseAll;

    private string _databaseStatus = "Not checked";
    private string _databaseDetail = string.Empty;
    private bool _databaseOk;
    private string _dropBoxStatus = "Not checked";
    private string _dropBoxDetail = string.Empty;
    private bool _dropBoxOk;
    private long _mediaCountValue;
    private string _libraryPathValue = string.Empty;
    private string _exportPathValue = string.Empty;
    private string _systemIdValue = string.Empty;

    public ObservableCollection<string> RecentExports { get; } = [];

    /// <summary>
    /// The already-synced Dropbox hierarchy, as a tree. Loaded from ml.DropBoxNode - no
    /// Dropbox call - which is the payoff of having synced it.
    /// </summary>
    public ObservableCollection<DropBoxTreeNode> DropBoxTree { get; } = [];

    public AsyncRelayCommand RefreshCommand => _refresh ??= new AsyncRelayCommand(RefreshAsync);

    public AsyncRelayCommand ExpandAllCommand => _expandAll ??= new AsyncRelayCommand(() =>
    {
        SetTreeExpanded(DropBoxTree, depthLimit: -1);
        return Task.CompletedTask;
    });

    public AsyncRelayCommand CollapseAllCommand => _collapseAll ??= new AsyncRelayCommand(() =>
    {
        SetTreeExpanded(DropBoxTree, depthLimit: 1);
        return Task.CompletedTask;
    });
    private int _treeFolders;
    private int _treeFiles;
    private int _treeWithDimensions;
    private int _treeUnverified;
    private string _treeSizeDisplay = "—";
    private string _treeLastSyncDisplay = "never";
    private bool _hasSyncedTree;

    public bool HasSyncedTree
    {
        get => _hasSyncedTree;
        private set => SetProperty(ref _hasSyncedTree, value);
    }

    public string TreeFoldersDisplay => _treeFolders.ToString("N0");

    public string TreeFilesDisplay => _treeFiles.ToString("N0");

    public string TreeWithDimensionsDisplay => _treeWithDimensions.ToString("N0");

    public string TreeSizeDisplay
    {
        get => _treeSizeDisplay;
        private set => SetProperty(ref _treeSizeDisplay, value);
    }

    public string TreeLastSyncDisplay
    {
        get => _treeLastSyncDisplay;
        private set => SetProperty(ref _treeLastSyncDisplay, value);
    }

    /// <summary>Exposed so the view can expand/collapse without walking the collection twice.</summary>
    public bool HasUnverifiedFolders => _treeUnverified > 0;

    public int TreeUnverifiedCount => _treeUnverified;

    public string DatabaseStatus
    {
        get => _databaseStatus;
        private set => SetProperty(ref _databaseStatus, value);
    }

    public string DatabaseDetail
    {
        get => _databaseDetail;
        private set => SetProperty(ref _databaseDetail, value);
    }

    public bool DatabaseOk
    {
        get => _databaseOk;
        private set => SetProperty(ref _databaseOk, value);
    }

    public string DropBoxStatus
    {
        get => _dropBoxStatus;
        private set => SetProperty(ref _dropBoxStatus, value);
    }

    public string DropBoxDetail
    {
        get => _dropBoxDetail;
        private set => SetProperty(ref _dropBoxDetail, value);
    }

    public bool DropBoxOk
    {
        get => _dropBoxOk;
        private set => SetProperty(ref _dropBoxOk, value);
    }

    public long MediaCount
    {
        get => _mediaCountValue;
        private set
        {
            if (SetProperty(ref _mediaCountValue, value))
            {
                OnPropertyChanged(nameof(MediaCountDisplay));
            }
        }
    }

    public string MediaCountDisplay => MediaCount.ToString("N0");

    /// <summary>Media library root from ml.Settings.</summary>
    public string LibraryPath
    {
        get => _libraryPathValue;
        private set => SetProperty(ref _libraryPathValue, value);
    }

    /// <summary>Export destination from ml.Settings.</summary>
    public string ExportPath
    {
        get => _exportPathValue;
        private set => SetProperty(ref _exportPathValue, value);
    }

    public string SystemId
    {
        get => _systemIdValue;
        private set => SetProperty(ref _systemIdValue, value);
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await CheckDatabaseAsync();
            await CheckDropBoxAsync();
            await LoadSyncedTreeAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Loads the synced Dropbox hierarchy from the database. This is the point of having
    /// synced it: the dashboard reads the table and never waits on the Dropbox API.
    /// </summary>
    private async Task LoadSyncedTreeAsync()
    {
        try
        {
            var (roots, summary) = await dropBoxTree.GetSyncedTreeAsync();

            DropBoxTree.Clear();
            foreach (var root in roots)
            {
                DropBoxTree.Add(root);
            }

            // Open the top two levels: enough to show the shape without dumping a
            // nine-thousand-node tree into view.
            SetTreeExpanded(DropBoxTree, depthLimit: 2);

            _treeFolders = summary.Folders;
            _treeFiles = summary.Files;
            _treeWithDimensions = summary.WithDimensions;
            _treeUnverified = summary.UnverifiedFolders;
            TreeSizeDisplay = FormatSize(summary.TotalBytes);
            TreeLastSyncDisplay = summary.LastSyncedUtc is null
                ? "never"
                : summary.LastSyncedUtc.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

            HasSyncedTree = DropBoxTree.Count > 0;

            OnPropertyChanged(nameof(TreeFoldersDisplay));
            OnPropertyChanged(nameof(TreeFilesDisplay));
            OnPropertyChanged(nameof(TreeWithDimensionsDisplay));
            OnPropertyChanged(nameof(HasUnverifiedFolders));
            OnPropertyChanged(nameof(TreeUnverifiedCount));

            log.Info($"Synced tree loaded from database: {summary.Folders} folder(s), " +
                     $"{summary.Files} file(s), {summary.WithDimensions} with dimensions.");
        }
        catch (Exception ex)
        {
            HasSyncedTree = false;
            log.Error($"Loading synced tree failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets expansion across the tree. <paramref name="depthLimit"/> of -1 opens every
    /// level; a positive value opens only that many, which keeps a large sync from
    /// materialising thousands of items in view at once.
    /// </summary>
    private static void SetTreeExpanded(
        IEnumerable<DropBoxTreeNode> nodes, int depthLimit = -1, int depth = 1)
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

    private async Task CheckDatabaseAsync()
    {
        try
        {
            var probe = await databaseProbe.ProbeAsync();
            DatabaseOk = probe.Success;
            DatabaseStatus = probe.Success ? "Connected" : "Unavailable";
            DatabaseDetail = probe.Display;
            log.Info($"SQL Server: {DatabaseStatus} — {probe.Message}");

            if (!probe.Success)
            {
                return;
            }

            // Sequential, not Task.WhenAll: all three share one scoped MlDbContext,
            // and EF forbids concurrent operations on a single context instance.
            MediaCount = await ml.CountMediaAsync();

            var settings = await ml.GetSettingsAsync();
            LibraryPath = settings?.MediaLibraryLocalPath ?? "(unset)";
            ExportPath = settings?.ExportLocalPath ?? "(unset)";
            SystemId = settings?.SystemId.ToString() ?? "(unset)";

            RecentExports.Clear();
            foreach (var header in await ml.GetExportHeadersAsync(10))
            {
                RecentExports.Add(
                    $"{header.CreateDate:yyyy-MM-dd HH:mm} — {header.ArchiveName} — {header.Description}");
            }
        }
        catch (Exception ex)
        {
            DatabaseOk = false;
            DatabaseStatus = "Error";
            DatabaseDetail = ex.Message;
            log.Error($"Database check failed: {ex.Message}");
        }
    }

    private async Task CheckDropBoxAsync()
    {
        if (!dropBox.IsConfigured)
        {
            DropBoxOk = false;
            DropBoxStatus = "Not configured";
            DropBoxDetail = "Set DropBox:AppKey and a token in Settings, then save.";
            return;
        }

        try
        {
            var account = await dropBox.GetAccountAsync();
            DropBoxOk = true;
            DropBoxStatus = "Connected";
            DropBoxDetail = string.IsNullOrWhiteSpace(account.DisplayName)
                ? account.Email ?? "Account connected"
                : $"{account.DisplayName} ({account.Email})";
            log.Info($"Dropbox: connected as {account.Email}");
        }
        catch (Exception ex)
        {
            DropBoxOk = false;
            DropBoxStatus = "Error";
            DropBoxDetail = ex.Message;
            log.Error($"Dropbox check failed: {ex.Message}");
        }
    }
}

/// <summary>In-memory log pane. Keeps the most recent lines for the UI to bind to.</summary>
public interface ILogSink
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);

    ObservableCollection<string> Lines { get; }
}

public sealed class LogSink : ILogSink
{
    private const int MaxLines = 500;

    public ObservableCollection<string> Lines { get; } = [];

    public void Info(string message) => Add("INFO", message);

    public void Warn(string message) => Add("WARN", message);

    public void Error(string message) => Add("ERR ", message);

    private void Add(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Insert(line));
            return;
        }

        Insert(line);
    }

    private void Insert(string line)
    {
        Lines.Insert(0, line);
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(Lines.Count - 1);
        }
    }
}