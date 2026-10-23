using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Utility.DropBox.Services;
using Utility.Server;
using Utility.Server.ML.Entities;
using Utility.Server.ML.Services;
using Utility.UI.Mvvm;

namespace Utility.UI.ViewModels;

/// <summary>
/// Read-only overview: database reachability, the Dropbox account, and a few
/// aggregate figures read from the ml. schema.
/// </summary>
public sealed class DashboardViewModel(
    IDatabaseProbe databaseProbe,
    IMlDataService ml,
    IDropBoxDataService dropBox,
    ILogSink log) : ViewModelBase
{
    private AsyncRelayCommand? _refresh;

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

    public AsyncRelayCommand RefreshCommand => _refresh ??= new AsyncRelayCommand(RefreshAsync);

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
        }
        finally
        {
            IsBusy = false;
        }
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