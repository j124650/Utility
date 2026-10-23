using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Utility.DropBox.Models;
using Utility.DropBox.Options;
using Utility.DropBox.Services;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// Browse the connected Dropbox: namespaces (personal + team roots), folder structure
/// and file metadata, plus the whole tree as hierarchical JSON.
/// <para>
/// Listing is level-by-level and read-only. Nothing is uploaded, moved or deleted, and
/// download stays an explicit per-file action behind a save dialog.
/// </para>
/// </summary>
public sealed class DropBoxViewModel(
    IDropBoxDataService dropBox,
    IDropBoxRegistrySettings registry,
    ILogSink log) : ViewModelBase
{
    private AsyncRelayCommand? _refresh;
    private AsyncRelayCommand? _goUp;
    private AsyncRelayCommand? _goToPath;
    private AsyncRelayCommand? _buildTree;
    private AsyncRelayCommand? _saveJson;
    private AsyncRelayCommand? _download;

    private string _currentPath = string.Empty;
    private string _pathInput = string.Empty;
    private string _status = "Ready.";
    private DropBoxNamespace? _selectedNamespace;
    private DropBoxFolderTree? _tree;
    private string? _treeJson;
    private int _treeDepth = 2;
    private bool _isTreeBuilding;
    private FileEntry? _selectedEntry;
    private CancellationTokenSource? _treeCts;

    public ObservableCollection<FileEntry> Entries { get; } = [];

    /// <summary>Namespaces this account can browse — personal first, then team roots.</summary>
    public ObservableCollection<DropBoxNamespace> Namespaces { get; } = [];

    public AsyncRelayCommand RefreshCommand => _refresh ??= new AsyncRelayCommand(RefreshAsync);

    public AsyncRelayCommand GoUpCommand => _goUp ??= new AsyncRelayCommand(GoUpAsync);

    public AsyncRelayCommand GoToPathCommand => _goToPath ??= new AsyncRelayCommand(GoToPathAsync);

    public AsyncRelayCommand BuildTreeCommand => _buildTree ??= new AsyncRelayCommand(BuildTreeAsync);

    public AsyncRelayCommand SaveJsonCommand => _saveJson ??= new AsyncRelayCommand(SaveJsonAsync);

    public AsyncRelayCommand DownloadCommand => _download ??= new AsyncRelayCommand(DownloadAsync);

    /// <summary>Where the active credentials came from, shown on the page.</summary>
    public string SettingsSource => registry.IsConfigured
        ? $"credentials: registry {registry.RegistryKeyPath}"
        : "credentials: configuration file (no registry key)";

    /// <summary>True when the registry supplied the credentials.</summary>
    public bool IsUsingRegistry => registry.IsConfigured;

    public DropBoxNamespace? SelectedNamespace
    {
        get => _selectedNamespace;
        set
        {
            if (!SetProperty(ref _selectedNamespace, value))
            {
                return;
            }

            // A namespace change invalidates the current path: the same path means
            // different things in each namespace, and team paths 404 in personal space.
            CurrentPath = string.Empty;
            OnPropertyChanged(nameof(NamespaceLabel));
            _ = RefreshAsync();
        }
    }

    public string NamespaceLabel =>
        SelectedNamespace is null ? "(not connected)" : SelectedNamespace.DisplayLabel;

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(PathDisplay));
                OnPropertyChanged(nameof(CanGoUp));
            }
        }
    }

    public string PathDisplay => string.IsNullOrEmpty(CurrentPath) ? "/" : CurrentPath;

    /// <summary>Editable path box — type a path and press Go.</summary>
    public string PathInput
    {
        get => _pathInput;
        set => SetProperty(ref _pathInput, value);
    }

    public bool CanGoUp => !string.IsNullOrEmpty(CurrentPath);

    /// <summary>Depth for the tree walk; keeps a huge team folder from taking minutes.</summary>
    public int TreeDepth
    {
        get => _treeDepth;
        set => SetProperty(ref _treeDepth, value);
    }

    public DropBoxFolderTree? Tree
    {
        get => _tree;
        set
        {
            if (SetProperty(ref _tree, value))
            {
                OnPropertyChanged(nameof(TreeSummary));
                OnPropertyChanged(nameof(HasTree));
            }
        }
    }

    public string? TreeJson
    {
        get => _treeJson;
        set
        {
            if (SetProperty(ref _treeJson, value))
            {
                OnPropertyChanged(nameof(HasTreeJson));
            }
        }
    }

    public bool HasTree => Tree is not null;

    public bool HasTreeJson => !string.IsNullOrEmpty(TreeJson);

    public bool IsTreeBuilding
    {
        get => _isTreeBuilding;
        private set => SetProperty(ref _isTreeBuilding, value);
    }

    public string TreeSummary => Tree is null
        ? "No tree built yet."
        : $"{Tree.FolderCount} folder(s), {Tree.FileCount} file(s), {Tree.TotalSizeDisplay} in {Tree.DurationMs} ms";

    /// <summary>Selected row in the flat listing.</summary>
    public FileEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                OnPropertyChanged(nameof(SelectedEntryDetails));
            }
        }
    }

    public void SelectEntry(FileEntry? entry) => SelectedEntry = entry;

    /// <summary>Per-file metadata shown in the detail pane.</summary>
    public string SelectedEntryDetails
    {
        get
        {
            var e = SelectedEntry;
            if (e is null)
            {
                return "Select an item to see its metadata.";
            }

            var lines = new System.Text.StringBuilder();
            lines.AppendLine($"Name         : {e.Name}");
            lines.AppendLine($"Type         : {(e.IsFolder ? "Folder" : "File")}");
            lines.AppendLine($"Path         : {e.PathDisplay}");

            if (!e.IsFolder)
            {
                lines.AppendLine($"Size         : {e.SizeDisplay} ({e.SizeBytes:N0} bytes)");
                lines.AppendLine($"Modified     : {e.ModifiedDisplay}");
                lines.AppendLine($"Revision     : {e.Rev ?? "-"}");
                lines.AppendLine($"Content hash : {e.ContentHash ?? "-"}");
                lines.AppendLine($"Downloadable : {(e.IsDownloadable ? "yes" : "no")}");
            }

            if (!string.IsNullOrEmpty(e.Id))
            {
                lines.AppendLine($"Id           : {e.Id}");
            }

            return lines.ToString().TrimEnd();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                // Mirror into the window status bar so it updates as work progresses.
                StatusMessage = value;
            }
        }
    }

    private bool RequireConfigured()
    {
        if (dropBox.IsConfigured)
        {
            return true;
        }

        Status = "Dropbox is not configured. Add credentials to HKCU\\Software\\BbcUtility\\DropBox.";
        log.Warn("Dropbox operation skipped: not configured.");
        return false;
    }

    /// <summary>
    /// Connects, loads namespaces, then lists the current folder. Safe to call on every
    /// page visit — it is the page's refresh entry point.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (!RequireConfigured())
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (Namespaces.Count == 0)
            {
                Status = "Connecting…";
                var namespaces = await dropBox.GetNamespacesAsync();

                Namespaces.Clear();
                foreach (var ns in namespaces)
                {
                    Namespaces.Add(ns);
                }

                // Default to the team root when the account has one: a Business team
                // account's personal space is often empty, so defaulting there would
                // show a blank page and look broken.
                var preferred = Namespaces.FirstOrDefault(x => !x.IsPersonal) ?? Namespaces.FirstOrDefault();
                if (preferred is not null)
                {
                    _selectedNamespace = preferred;
                    OnPropertyChanged(nameof(SelectedNamespace));
                    OnPropertyChanged(nameof(NamespaceLabel));
                }

                log.Info($"Loaded {Namespaces.Count} Dropbox namespace(s).");
            }

            var entries = await dropBox.ListFolderAsync(CurrentPath, ActiveNamespaceId);

            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries.Add(entry);
            }

            SelectedEntry = null;
            Status = $"{entries.Count} item(s) in {NamespaceLabel}{PathDisplay}.";
        }
        catch (Exception ex)
        {
            Status = Describe(ex);
            log.Error($"List failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The namespace id to scope requests to. Personal space must be sent as
    /// <c>null</c>: its namespace id is not a valid path root, and Dropbox answers
    /// <c>invalid_root</c> when it is passed as one.
    /// </summary>
    private string? ActiveNamespaceId =>
        SelectedNamespace is null || SelectedNamespace.IsPersonal ? null : SelectedNamespace.Id;

    // Dropbox surfaces failures as ApiException<TError>, and TError differs per route, so
    // there is no single non-generic type to catch. Its Message already carries the
    // route and the error tag (e.g. "path/not_found/"), which is what the user needs,
    // so pass it through rather than guessing at a closed generic.
    private static string Describe(Exception ex) => ex.Message;

    public Task GoUpAsync()
    {
        if (string.IsNullOrEmpty(CurrentPath))
        {
            return Task.CompletedTask;
        }

        var trimmed = CurrentPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        CurrentPath = slash <= 0 ? string.Empty : trimmed[..slash];

        return RefreshAsync();
    }

    /// <summary>Navigates to whatever is in the path box.</summary>
    public async Task GoToPathAsync()
    {
        var target = PathInput.Trim();

        if (target.Length == 0)
        {
            await RefreshAsync();
            return;
        }

        // Accept a pasted path with or without a leading slash.
        CurrentPath = target.StartsWith('/') ? target.TrimEnd('/') : "/" + target.TrimEnd('/');
        PathInput = CurrentPath;

        await RefreshAsync();
    }

    /// <summary>Double-clicking a folder navigates into it; files are left alone.</summary>
    public async Task OpenEntryAsync(FileEntry? entry)
    {
        if (entry is null || !entry.IsFolder)
        {
            return;
        }

        CurrentPath = entry.PathDisplay;
        PathInput = CurrentPath;

        await RefreshAsync();
    }

    /// <summary>
    /// Walks the tree concurrently and renders it as hierarchical JSON.
    /// Only metadata is fetched — no file content is downloaded.
    /// </summary>
    public async Task BuildTreeAsync()
    {
        if (!RequireConfigured())
        {
            return;
        }

        _treeCts?.Cancel();
        _treeCts = new CancellationTokenSource();
        var ct = _treeCts.Token;

        IsTreeBuilding = true;
        IsBusy = true;
        Tree = null;
        TreeJson = null;

        try
        {
            var depth = Math.Clamp(TreeDepth, 1, 10);
            Status = $"Building tree from {PathDisplay} (depth {depth})…";

            var tree = await dropBox.GetFolderTreeAsync(CurrentPath, ActiveNamespaceId, depth, 6, false, ct);
            if (ct.IsCancellationRequested)
            {
                Status = "Tree build cancelled.";
                return;
            }

            Tree = tree;
            TreeJson = tree.ToJson();
            Status = $"Tree ready: {TreeSummary}.";
            log.Info($"Dropbox tree built: {TreeSummary}");
        }
        catch (OperationCanceledException)
        {
            Status = "Tree build cancelled.";
        }
        catch (Exception ex)
        {
            Status = Describe(ex);
            log.Error($"Tree build failed: {ex.Message}");
        }
        finally
        {
            IsTreeBuilding = false;
            IsBusy = false;
        }
    }

    /// <summary>Writes the tree JSON wherever the user picks.</summary>
    public async Task SaveJsonAsync()
    {
        if (string.IsNullOrEmpty(TreeJson))
        {
            Status = "Build the tree first.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save folder tree as JSON",
            FileName = "dropbox-tree.json",
            Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, TreeJson);
            Status = $"Saved tree JSON to {dialog.FileName}.";
            log.Info($"Dropbox tree JSON saved to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            Status = $"Could not save JSON: {ex.Message}";
            log.Error($"Saving tree JSON failed: {ex.Message}");
        }
    }

    /// <summary>Explicit, per-file download — nothing is fetched implicitly.</summary>
    public async Task DownloadAsync()
    {
        if (!RequireConfigured())
        {
            return;
        }

        var entry = SelectedEntry;
        if (entry is null)
        {
            Status = "Select a file first.";
            return;
        }

        if (entry.IsFolder)
        {
            Status = "Folders are not downloaded directly — open it and select a file.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save file",
            FileName = entry.Name,
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await dropBox.DownloadAsync(entry.PathDisplay, dialog.FileName, ActiveNamespaceId);
            Status = $"Downloaded {entry.Name} to {dialog.FileName}.";
            log.Info($"Downloaded {entry.Name}.");
        }
        catch (Exception ex)
        {
            Status = Describe(ex);
            log.Error($"Download failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}