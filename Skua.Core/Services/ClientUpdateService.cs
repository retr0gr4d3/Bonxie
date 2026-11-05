using Skua.Core.Interfaces;
using Skua.Core.Models;
using Velopack;
using Velopack.Sources;

namespace Skua.Core.Services;

public class ClientUpdateService : IClientUpdateService
{
    public const string UpdateRepository = "https://github.com/retr0gr4d3/Bonxie";

    /// <summary>Velopack channel; must match <c>--channel</c> in release.yml / package-linux.sh.</summary>
    public static string Channel => OperatingSystem.IsWindows() ? "win" : "linux-x64";

    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private UpdateManager? _updateManager;
    private Velopack.UpdateInfo? _pendingUpdate;
    private bool? _managerNightly;

    public ClientUpdateService(IDialogService dialogService, ISettingsService settingsService)
    {
        _dialogService = dialogService;
        _settingsService = settingsService;
    }

    public string? LatestVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    public bool UpdateAvailable => _pendingUpdate is not null;

    public bool UsingNightlyChannel => _settingsService.Get("UseNightlyBuilds", false);

    public async Task CheckForUpdateAsync()
    {
        try
        {
            _updateManager = GetUpdateManager();
            if (_updateManager is null || !_updateManager.IsInstalled)
            {
                _pendingUpdate = null;
                return;
            }
            Velopack.UpdateInfo? update = await _updateManager.CheckForUpdatesAsync();
            _pendingUpdate = ShouldOfferUpdate(update) ? update : null;
        }
        catch
        {
            _pendingUpdate = null;
        }
    }

    public async Task DownloadUpdateAsync(IProgress<string>? progress)
    {
        try
        {
            _updateManager = GetUpdateManager();
            if (_updateManager is null || !_updateManager.IsInstalled)
            {
                progress?.Report("App is running in portable/uninstalled mode.");
                return;
            }

            if (_pendingUpdate is null)
            {
                Velopack.UpdateInfo? update = await _updateManager.CheckForUpdatesAsync();
                _pendingUpdate = ShouldOfferUpdate(update) ? update : null;
            }

            if (_pendingUpdate is null)
            {
                progress?.Report("You have the latest version.");
                return;
            }

            progress?.Report($"Downloading {_pendingUpdate.TargetFullRelease.Version}...");
            await _updateManager.DownloadUpdatesAsync(
                _pendingUpdate,
                value => progress?.Report($"Downloading... {value}%"));

            progress?.Report("Installing update and restarting...");
            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
        }
        catch (Exception e)
        {
            progress?.Report("Error while updating.");
            _dialogService.ShowMessageBox($"Error Message:\r\n{e.Message}", "Update Error");
        }
    }

    private bool ShouldOfferUpdate(Velopack.UpdateInfo? update)
    {
        if (update is null)
            return false;

        if (!UsingNightlyChannel)
            return true;

        return !string.Equals(
            update.TargetFullRelease.Version.ToString(),
            ClientFileSources.AssemblyVersion,
            StringComparison.Ordinal);
    }

    private UpdateManager GetUpdateManager()
    {
        bool nightly = UsingNightlyChannel;
        if (_updateManager is not null && _managerNightly == nightly)
            return _updateManager;

        _managerNightly = nightly;
        _pendingUpdate = null;

        if (!nightly)
            return new UpdateManager(
                new GithubSource(UpdateRepository, null, true),
                new UpdateOptions
                {
                    ExplicitChannel = Channel,
                    AllowVersionDowngrade = ClientFileSources.IsNightly
                });

        return new UpdateManager(
            new GithubSource(UpdateRepository, null, true),
            new UpdateOptions
            {
                ExplicitChannel = OperatingSystem.IsWindows() ? "nightly" : "nightly-linux-x64",
                AllowVersionDowngrade = true
            });
    }
}
