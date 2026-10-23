using System.ComponentModel;
using System.Runtime.CompilerServices;
using Utility.Server;
using Utility.Server.ML.Services;

namespace Utility.UI.Services;

/// <summary>
/// Application-wide status, surfaced in the window status bar.
/// <para>
/// A singleton on purpose: the dashboard page is transient, so status held on a view model
/// would be re-read from the database every time the user navigated to the page. Facts that
/// change slowly belong here and are loaded once.
/// </para>
/// </summary>
public interface IAppStatus : INotifyPropertyChanged
{
    bool IsDatabaseOk { get; }

    bool IsDropBoxOk { get; }

    string DatabaseDetail { get; }

    string DropBoxDetail { get; }

    long MediaCount { get; }

    string MediaCountDisplay { get; }

    string SystemId { get; }

    string LibraryPath { get; }

    bool IsLoaded { get; }

    /// <summary>
    /// Reads every status fact. Called once at startup and by the status bar's own
    /// refresh - never on page navigation.
    /// </summary>
    Task LoadAsync(CancellationToken ct = default);
}

public sealed class AppStatus(
    IDatabaseProbe databaseProbe,
    IMlDataService ml,
    Utility.DropBox.Services.IDropBoxDataService dropBox,
    ILogSink log) : IAppStatus
{
    private bool _isDatabaseOk;
    private bool _isDropBoxOk;
    private string _databaseDetail = "not checked";
    private string _dropBoxDetail = "not checked";
    private long _mediaCount;
    private string _systemId = "—";
    private string _libraryPath = "—";
    private bool _isLoaded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsDatabaseOk
    {
        get => _isDatabaseOk;
        private set => Set(ref _isDatabaseOk, value);
    }

    public bool IsDropBoxOk
    {
        get => _isDropBoxOk;
        private set => Set(ref _isDropBoxOk, value);
    }

    public string DatabaseDetail
    {
        get => _databaseDetail;
        private set => Set(ref _databaseDetail, value);
    }

    public string DropBoxDetail
    {
        get => _dropBoxDetail;
        private set => Set(ref _dropBoxDetail, value);
    }

    public long MediaCount
    {
        get => _mediaCount;
        private set
        {
            if (Set(ref _mediaCount, value))
            {
                Raise(nameof(MediaCountDisplay));
            }
        }
    }

    public string MediaCountDisplay => MediaCount.ToString("N0");

    public string SystemId
    {
        get => _systemId;
        private set => Set(ref _systemId, value);
    }

    public string LibraryPath
    {
        get => _libraryPath;
        private set => Set(ref _libraryPath, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await LoadDatabaseAsync(ct);
        await LoadDropBoxAsync(ct);
        IsLoaded = true;
    }

    private async Task LoadDatabaseAsync(CancellationToken ct)
    {
        try
        {
            var probe = await databaseProbe.ProbeAsync(ct);
            IsDatabaseOk = probe.Success;
            DatabaseDetail = probe.Success ? "Connected" : probe.Message;

            if (!probe.Success)
            {
                return;
            }

            // Sequential, not WhenAll: both read through one scoped MlDbContext and EF
            // forbids concurrent operations on a single context instance.
            MediaCount = await ml.CountMediaAsync();

            var settings = await ml.GetSettingsAsync();
            LibraryPath = string.IsNullOrWhiteSpace(settings?.MediaLibraryLocalPath)
                ? "—"
                : settings!.MediaLibraryLocalPath!;
            SystemId = settings?.SystemId.ToString() ?? "—";

            log.Info($"Status: SQL Server connected, {MediaCountDisplay} media items.");
        }
        catch (Exception ex)
        {
            IsDatabaseOk = false;
            DatabaseDetail = ex.Message;
            log.Error($"Status: database check failed: {ex.Message}");
        }
    }

    private async Task LoadDropBoxAsync(CancellationToken ct)
    {
        if (!dropBox.IsConfigured)
        {
            IsDropBoxOk = false;
            DropBoxDetail = "not configured";
            return;
        }

        try
        {
            var account = await dropBox.GetAccountAsync(ct);
            IsDropBoxOk = true;
            DropBoxDetail = string.IsNullOrWhiteSpace(account.DisplayName)
                ? account.Email ?? "connected"
                : account.DisplayName!;
            log.Info($"Status: Dropbox connected as {account.Email}.");
        }
        catch (Exception ex)
        {
            IsDropBoxOk = false;
            DropBoxDetail = ex.Message;
            log.Error($"Status: Dropbox check failed: {ex.Message}");
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}