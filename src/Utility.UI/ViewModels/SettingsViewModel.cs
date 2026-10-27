using System.Threading.Tasks;
using Utility.DropBox.Options;
using Utility.Server;
using Utility.Server.Options;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// Edits the configuration both libraries consume.
/// </summary>
/// <remarks>
/// <para>
/// The values live in the Windows registry, not in a file: the SQL settings at
/// <c>HKCU\Software\BbcUtility\Sql</c> and the Dropbox ones at
/// <c>HKCU\Software\BbcUtility\DropBox</c>. The registry is the source of truth, and the
/// Settings page writes to it — so what the page shows is what the app will use.
/// </para>
/// <para>
/// A value that is not in the registry keeps its default: the timeouts and the retry flag
/// on <see cref="ServerOptions"/>, the root path and the retry budget on
/// <see cref="DropBoxOptions"/>.
/// </para>
/// </remarks>
public sealed class SettingsViewModel(
    ISqlRegistrySettings sql,
    IDropBoxRegistrySettings dropBox,
    IDatabaseProbe databaseProbe,
    ILogSink log) : ViewModelBase
{
    private AsyncRelayCommand? _save;
    private AsyncRelayCommand? _testDatabase;
    private AsyncRelayCommand? _reload;

    private string _connectionString = string.Empty;
    private string _dropBoxAppKey = string.Empty;
    private string _dropBoxAppSecret = string.Empty;
    private string _dropBoxRefreshToken = string.Empty;
    private int _dropBoxMaxRetries = 3;

    public AsyncRelayCommand SaveCommand => _save ??= new AsyncRelayCommand(SaveAsync);

    public AsyncRelayCommand TestDatabaseCommand => _testDatabase ??= new AsyncRelayCommand(TestDatabaseAsync);

    public AsyncRelayCommand ReloadCommand => _reload ??= new AsyncRelayCommand(LoadAsync);

    public string ConnectionString
    {
        get => _connectionString;
        set => SetProperty(ref _connectionString, value);
    }

    public string DropBoxAppKey
    {
        get => _dropBoxAppKey;
        set => SetProperty(ref _dropBoxAppKey, value);
    }

    public string DropBoxAppSecret
    {
        get => _dropBoxAppSecret;
        set => SetProperty(ref _dropBoxAppSecret, value);
    }

    public string DropBoxRefreshToken
    {
        get => _dropBoxRefreshToken;
        set => SetProperty(ref _dropBoxRefreshToken, value);
    }

    /// <summary>The SDK's retry budget for server errors — 0 to 10.</summary>
    public int DropBoxMaxRetries
    {
        get => _dropBoxMaxRetries;
        set => SetProperty(ref _dropBoxMaxRetries, value);
    }

    public Task LoadAsync()
    {
        // The registry is read through the same readers the app uses at startup, so the
        // page shows the values that are actually in force.
        var server = new ServerOptions();
        sql.TryApplyTo(server);

        var dropbox = new DropBoxOptions();
        dropBox.TryApplyTo(dropbox);

        ConnectionString = server.ConnectionString;
        DropBoxAppKey = dropbox.AppKey;
        DropBoxAppSecret = dropbox.AppSecret ?? string.Empty;
        DropBoxRefreshToken = dropbox.RefreshToken ?? string.Empty;
        DropBoxMaxRetries = dropbox.MaxRetriesOnError;

        SetStatus("Loaded current settings.");
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            // The registry is read FIRST, so the values the page does not show — the
            // timeouts, the retry flag, the app secret — keep the values they have
            // instead of being written back as defaults.
            var server = new ServerOptions();
            sql.TryApplyTo(server);
            server.ConnectionString = ConnectionString;

            sql.Save(server);

            var dropbox = new DropBoxOptions();
            dropBox.TryApplyTo(dropbox);

            dropbox.AppKey = DropBoxAppKey;
            dropbox.AppSecret = DropBoxAppSecret;
            dropbox.RefreshToken = DropBoxRefreshToken;
            dropbox.MaxRetriesOnError = DropBoxMaxRetries;

            dropBox.Save(dropbox);

            SetStatus("Settings saved. Restart the app to apply.");
            log.Info("Settings saved to the registry.");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
            log.Error($"Saving settings failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task TestDatabaseAsync()
    {
        IsBusy = true;
        try
        {
            var result = await databaseProbe.ProbeAsync();
            SetStatus(result.Display);
            log.Info(result.Success ? "SQL probe succeeded." : $"SQL probe failed: {result.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
