using System.Threading.Tasks;
using Utility.DropBox.Options;
using Utility.Server;
using Utility.Server.Options;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// Edits the configuration both libraries consume. Values are written to a per-user
/// override file; the shipped appsettings.json is left untouched.
/// </summary>
public sealed class SettingsViewModel(
    ISettingsService settings,
    IDatabaseProbe databaseProbe,
    ILogSink log) : ViewModelBase
{
    private AsyncRelayCommand? _save;
    private AsyncRelayCommand? _testDatabase;
    private AsyncRelayCommand? _reload;

    private string _connectionString = string.Empty;
    private string _dropBoxAppKey = string.Empty;
    private string _dropBoxRefreshToken = string.Empty;
    private string _dropBoxAccessToken = string.Empty;
    private string _dropBoxRootPath = string.Empty;

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

    public string DropBoxRefreshToken
    {
        get => _dropBoxRefreshToken;
        set => SetProperty(ref _dropBoxRefreshToken, value);
    }

    public string DropBoxAccessToken
    {
        get => _dropBoxAccessToken;
        set => SetProperty(ref _dropBoxAccessToken, value);
    }

    public string DropBoxRootPath
    {
        get => _dropBoxRootPath;
        set => SetProperty(ref _dropBoxRootPath, value);
    }

    public string SettingsFilePath => settings.UserSettingsPath;

    public Task LoadAsync()
    {
        ConnectionString = settings.GetValue($"{ServerOptions.SectionName}:ConnectionString");

        DropBoxAppKey = settings.GetValue($"{DropBoxOptions.SectionName}:AppKey");
        DropBoxRefreshToken = settings.GetValue($"{DropBoxOptions.SectionName}:RefreshToken");
        DropBoxAccessToken = settings.GetValue($"{DropBoxOptions.SectionName}:AccessToken");
        DropBoxRootPath = settings.GetValue($"{DropBoxOptions.SectionName}:RootPath");

        SetStatus("Loaded current settings.");
        return Task.CompletedTask;
    }

    public async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            settings.SetValue($"{ServerOptions.SectionName}:ConnectionString", ConnectionString);

            settings.SetValue($"{DropBoxOptions.SectionName}:AppKey", DropBoxAppKey);
            settings.SetValue($"{DropBoxOptions.SectionName}:RefreshToken", DropBoxRefreshToken);
            settings.SetValue($"{DropBoxOptions.SectionName}:AccessToken", DropBoxAccessToken);
            settings.SetValue($"{DropBoxOptions.SectionName}:RootPath", DropBoxRootPath);

            SetStatus($"Saved to {settings.UserSettingsPath}. Restart the app to apply.");
            log.Info("Settings saved to user overrides.");
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