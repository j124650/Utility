using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Utility.Server.Finder.Repair;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// One node in the recovery log's tree: a day, or a log file within a day.
/// </summary>
public sealed class LogTreeNode
{
    /// <summary>The day — "2026-10-31" — or the log file's name.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>The log file, for a file node. Null for a day.</summary>
    public string? Path { get; init; }

    /// <summary>When the file was last written.</summary>
    public DateTime LastWritten { get; init; }

    /// <summary>The files under a day.</summary>
    public ObservableCollection<LogTreeNode> Children { get; } = [];

    public bool IsFolder => Path is null;
}

/// <summary>
/// One of the app's own log files.
/// </summary>
public sealed class AppLogFile
{
    public string Name { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public DateTime LastWritten { get; init; }

    public long Size { get; init; }

    public string SizeDisplay => Size >= 1024L * 1024
        ? $"{Size / 1024.0 / 1024.0:N1} MB"
        : Size >= 1024
            ? $"{Size / 1024.0:N0} KB"
            : $"{Size:N0} bytes";

    public string LastWrittenDisplay => LastWritten.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>
/// The Logs tab: a browser over the logs the app writes.
/// </summary>
/// <remarks>
/// <para>
/// Two sub-tabs, one per kind of log. The Recovery tab's master is a tree of the days
/// and the log files under them; the App tab's master is the app's own log files. Both
/// masters are ordered most recent first, and both details are read on selection.
/// </para>
/// <para>
/// The tab READS and it writes nothing (§1 of the spec).
/// </para>
/// </remarks>
public sealed class LogsViewModel : ViewModelBase
{
    private readonly ILogSink _log;

    private LogTreeNode? _selectedNode;
    private AppLogFile? _selectedAppLog;
    private bool _showFailuresOnly;
    private bool _isReading;
    private string _recoveryFigures = string.Empty;
    private string _recoveryText = string.Empty;
    private string _appText = string.Empty;
    private string _status = string.Empty;

    /// <summary>Every operation of the log being read — the filter narrows the VIEW.</summary>
    private List<RecoveryLogOperation>? _allOperations;

    /// <summary>Whether a recovery log has been read.</summary>
    public bool HasRecoveryLog
    {
        get => _hasRecoveryLog;
        private set => SetProperty(ref _hasRecoveryLog, value);
    }

    private bool _hasRecoveryLog;

    /// <summary>Whether the recovery log being shown is in the OLDER format.</summary>
    public bool RecoveryIsLegacy
    {
        get => _recoveryIsLegacy;
        private set => SetProperty(ref _recoveryIsLegacy, value);
    }

    private bool _recoveryIsLegacy;

    /// <summary>Whether an app log has been read.</summary>
    public bool HasAppLog
    {
        get => _hasAppLog;
        private set => SetProperty(ref _hasAppLog, value);
    }

    private bool _hasAppLog;

    public LogsViewModel(ILogSink log)
    {
        _log = log;
    }

    /// <summary>Where the logs live — beside the app's own log.</summary>
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Utility",
        "logs");

    /// <summary>The recovery logs: the days, and the log files under each.</summary>
    public ObservableCollection<LogTreeNode> RecoveryDays { get; } = [];

    /// <summary>The app's own log files, most recent first.</summary>
    public ObservableCollection<AppLogFile> AppLogs { get; } = [];

    /// <summary>The operations of the log being read, for the Recovery detail.</summary>
    public ObservableCollection<RecoveryLogOperation> Operations { get; } = [];

    /// <summary>The log's own figures, for the Recovery detail.</summary>
    public string RecoveryFigures
    {
        get => _recoveryFigures;
        private set => SetProperty(ref _recoveryFigures, value);
    }

    /// <summary>The raw text of a log in the OLDER format, shown as it is.</summary>
    public string RecoveryText
    {
        get => _recoveryText;
        private set => SetProperty(ref _recoveryText, value);
    }

    /// <summary>The app log's text, as it was written.</summary>
    public string AppText
    {
        get => _appText;
        private set => SetProperty(ref _appText, value);
    }

    /// <summary>Whether the detail is still reading.</summary>
    public bool IsReading
    {
        get => _isReading;
        private set => SetProperty(ref _isReading, value);
    }

    /// <summary>What the tab last did, for the status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Whether the detail shows only the operations that failed.
    /// </summary>
    /// <remarks>
    /// It narrows the VIEW, never the set: every operation is in the log, and turning it
    /// off shows every one of them.
    /// </remarks>
    public bool ShowFailuresOnly
    {
        get => _showFailuresOnly;
        set
        {
            if (SetProperty(ref _showFailuresOnly, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>The selected node of the recovery tree.</summary>
    public LogTreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value) && value?.Path is { } path)
            {
                _ = LoadRecoveryAsync(path);
            }
        }
    }

    /// <summary>The selected app log.</summary>
    public AppLogFile? SelectedAppLog
    {
        get => _selectedAppLog;
        set
        {
            if (SetProperty(ref _selectedAppLog, value) && value is not null)
            {
                _ = LoadAppAsync(value);
            }
        }
    }

    /// <summary>Reads the lists again — the logs folder, which changes while the app runs.</summary>
    public RelayCommand RefreshCommand => _refresh ??= new RelayCommand(_ => Refresh());

    private RelayCommand? _refresh;

    /// <summary>
    /// Writes the run's failures as a CSV, where the reader chooses to put it.
    /// </summary>
    public RelayCommand ExportFailedCommand =>
        _exportFailed ??= new RelayCommand(_ => ExportFailed(), _ => CanExportFailed);

    private RelayCommand? _exportFailed;

    /// <summary>
    /// Whether there is anything to export — the log has at least one failure.
    /// </summary>
    public bool CanExportFailed
    {
        get => _canExportFailed;
        private set
        {
            if (SetProperty(ref _canExportFailed, value))
            {
                ExportFailedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _canExportFailed;

    /// <summary>The log being read, for the export's file name.</summary>
    private string _currentLogName = string.Empty;

    /// <summary>
    /// Reads the lists again — the logs folder, which changes while the app runs.
    /// </summary>
    public void Refresh()
    {
        LoadDays();
        LoadAppLogs();
        Status = $"{RecoveryDays.Count} day(s), {AppLogs.Count} app log(s).";
    }

    /// <summary>
    /// The recovery tree: the days, most recent first, and the log files under each.
    /// </summary>
    /// <remarks>
    /// A recovery log is found by its NAME wherever it is, so a log from before the dated
    /// folders existed is filed under the date in its own name — one rule, and every
    /// recovery log stays reachable.
    /// </remarks>
    private void LoadDays()
    {
        RecoveryDays.Clear();

        if (!Directory.Exists(LogDirectory))
        {
            return;
        }

        // The files in the folders, by day.
        var byDay = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var folder in Directory.GetDirectories(LogDirectory))
        {
            var day = Path.GetFileName(folder);

            var files = Directory.GetFiles(folder, "Recover *.log");
            if (files.Length == 0)
            {
                continue;
            }

            byDay[day] = files.ToList();
        }

        // And the recovery logs that are NOT in a folder — the ones from before the dated
        // folders existed. They are filed under the date in their own name.
        foreach (var file in Directory.GetFiles(LogDirectory, "Recover *.log"))
        {
            var day = DayOf(file);

            if (!byDay.TryGetValue(day, out var files))
            {
                files = new List<string>();
                byDay[day] = files;
            }

            files.Add(file);
        }

        foreach (var day in byDay.Keys.OrderByDescending(x => x, StringComparer.Ordinal))
        {
            var node = new LogTreeNode
            {
                Label = day,
            };

            foreach (var file in byDay[day].OrderByDescending(FileLastWritten))
            {
                node.Children.Add(new LogTreeNode
                {
                    Label = Path.GetFileName(file),
                    Path = file,
                    LastWritten = FileLastWritten(file),
                });
            }

            RecoveryDays.Add(node);
        }
    }

    /// <summary>The app's own log files, most recently written first.</summary>
    private void LoadAppLogs()
    {
        AppLogs.Clear();

        if (!Directory.Exists(LogDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(LogDirectory, "*.log")
            .Where(f => !Path.GetFileName(f).StartsWith("Recover ", StringComparison.Ordinal))
            .OrderByDescending(FileLastWritten))
        {
            AppLogs.Add(new AppLogFile
            {
                Name = Path.GetFileName(file),
                Path = file,
                LastWritten = FileLastWritten(file),
                Size = new FileInfo(file).Length,
            });
        }
    }

    /// <summary>
    /// Reads a recovery log and shows it.
    /// </summary>
    /// <remarks>
    /// The read is off the UI thread: a full run's log is ~340 MB, and reading it takes
    /// seconds. The log's TEXT is never held in memory — the reader parses it a line at a
    /// time and keeps the operations, which is what the detail shows.
    /// </remarks>
    private async Task LoadRecoveryAsync(string path)
    {
        IsReading = true;
        Operations.Clear();
        RecoveryText = string.Empty;
        RecoveryFigures = string.Empty;
        HasRecoveryLog = false;
        RecoveryIsLegacy = false;

        _currentLogName = Path.GetFileNameWithoutExtension(path);
        CanExportFailed = false;

        try
        {
            if (!RecoveryLogReader.IsJsonLines(path))
            {
                // The older format: shown as its own text, because it is readable already.
                RecoveryText = await File.ReadAllTextAsync(path);
                RecoveryFigures = "This log is in the older format, shown as it was written.";
                RecoveryIsLegacy = true;
                HasRecoveryLog = true;
                return;
            }

            var read = await Task.Run(() => RecoveryLogReader.Read(path));

            _allOperations = read.Operations.ToList();
            RecoveryFigures = Describe(read);
            HasRecoveryLog = true;
            ApplyFilter();

            // There is something to export when the run had a failure.
            CanExportFailed = _allOperations.Any(o => o.Failed);
        }
        catch (Exception ex)
        {
            _log.Error($"The recovery log could not be read: {ex.Message}");
            RecoveryFigures = $"The log could not be read: {ex.Message}";
        }
        finally
        {
            IsReading = false;
        }
    }

    /// <summary>The log's own figures — nothing is recomputed.</summary>
    private static string Describe(RecoveryLogRead read)
    {
        var lines = new List<string>
        {
            $"RECOVERY RUN   {read.Started:yyyy-MM-dd HH:mm}",
            $"  Filtered set       {read.Shown:N0} pair(s)",
            $"  Recoverable        {read.Recoverable:N0}",
            $"  Recovered          {read.Recovered:N0}",
            $"  Failed             {read.Failed:N0}",
            $"  Not recovered      {read.NotRecovered:N0}",
            $"  Downloaded         {Bytes(read.DownloadedBytes)}",
            $"  Replaced           {read.Replaced:N0} file(s)",
        };

        if (read.MalformedLines > 0)
        {
            lines.Add($"  MALFORMED          {read.MalformedLines:N0} line(s) could not be read");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// A number of bytes, in the unit that says something — a run that moved 24 MB does
    /// not read as "0.0 GB".
    /// </summary>
    private static string Bytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:N1} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0:N1} MB";
        }

        return $"{bytes:N0} bytes";
    }

    /// <summary>Fills the list, honouring the filter.</summary>
    private void ApplyFilter()
    {
        Operations.Clear();

        if (_allOperations is null)
        {
            return;
        }

        foreach (var operation in _allOperations)
        {
            if (ShowFailuresOnly && !operation.Failed)
            {
                continue;
            }

            Operations.Add(operation);
        }
    }

    /// <summary>
    /// Writes the run's failures as a CSV — and the reader chooses where it goes.
    /// </summary>
    /// <remarks>
    /// The Save File dialog is the reader's: the browser does not choose a folder and
    /// does not write a file nobody asked for.
    /// </remarks>
    private void ExportFailed()
    {
        var failures = _allOperations?.Where(o => o.Failed).ToList() ?? [];

        if (failures.Count == 0)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export the failed operations",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"{_currentLogName} failures.csv",
            InitialDirectory = Directory.Exists(LogDirectory) ? LogDirectory : null,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // UTF-8 WITH a BOM: Excel needs it to read a UTF-8 file's paths correctly.
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));

            var written = RecoveryFailureCsv.Write(writer, failures);

            Status = $"{written:N0} failure(s) written to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            _log.Error($"The failures could not be exported: {ex.Message}");
            Status = $"The failures could not be exported: {ex.Message}";
        }
    }

    /// <summary>Reads an app log and shows it, as it was written.</summary>
    private async Task LoadAppAsync(AppLogFile file)
    {
        IsReading = true;
        AppText = string.Empty;
        HasAppLog = false;

        try
        {
            AppText = await File.ReadAllTextAsync(file.Path);
            HasAppLog = true;
        }
        catch (Exception ex)
        {
            _log.Error($"The app log could not be read: {ex.Message}");
            AppText = $"The log could not be read: {ex.Message}";
        }
        finally
        {
            IsReading = false;
        }
    }

    /// <summary>The day a recovery log belongs to — the date in its own name.</summary>
    private static string DayOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        // "Recover 2026-10-31_20-48-59" -> "2026-10-31"
        var stamp = name.StartsWith("Recover ", StringComparison.Ordinal)
            ? name["Recover ".Length..]
            : name;

        var dash = stamp.IndexOf('_');
        return dash > 0 ? stamp[..dash] : stamp;
    }

    private static DateTime FileLastWritten(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }
}
