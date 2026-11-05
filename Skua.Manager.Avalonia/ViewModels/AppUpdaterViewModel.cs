using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using System.Diagnostics;
using System;
using Skua.Core.Interfaces;

namespace Skua.Manager.Avalonia.ViewModels;

public partial class AppUpdaterViewModel : ObservableObject
{
    private readonly IDispatcherService _dispatcherService;

    public AppUpdaterViewModel(IDispatcherService dispatcherService)
    {
        _dispatcherService = dispatcherService;
    }

    [ObservableProperty]
    private string _updateStatus = "Ready to check for updates.";

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private int _progressValue;

    private UpdateInfo? _updateInfo;
    private UpdateManager? _updateManager;

    public async Task CheckForUpdateAsync()
    {
        _updateInfo = null;
        _dispatcherService.Invoke(() =>
        {
            IsChecking = true;
            UpdateStatus = "Checking for updates...";
            ProgressValue = 0;
        });
        try
        {
            var locator = VelopackLocator.CreateDefaultForPlatform(null, null);
            _updateManager = new UpdateManager(new GithubSource(Skua.Core.Services.ClientUpdateService.UpdateRepository, null, false), new UpdateOptions { ExplicitChannel = Skua.Core.Services.ClientUpdateService.Channel }, locator);
            if (!_updateManager.IsInstalled)
            {
                _dispatcherService.Invoke(() =>
                {
                    UpdateStatus = "You have the latest version (Portable)";
                    UpdateAvailable = false;
                });
                return;
            }
            _updateInfo = await _updateManager.CheckForUpdatesAsync();

            _dispatcherService.Invoke(() =>
            {
                if (_updateInfo == null)
                {
                    UpdateStatus = "You are up to date!";
                    UpdateAvailable = false;
                }
                else
                {
                    UpdateStatus = $"Update {_updateInfo.TargetFullRelease.Version} available!";
                    UpdateAvailable = true;
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcherService.Invoke(() =>
            {
                UpdateStatus = $"Error checking updates: {ex.Message}";
                UpdateAvailable = false;
            });
        }
        finally
        {
            _dispatcherService.Invoke(() => IsChecking = false);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdate()
    {
        await CheckForUpdateAsync();
    }

    public async Task DownloadAndInstallAsync()
    {
        if (_updateInfo == null) return;

        _dispatcherService.Invoke(() =>
        {
            IsChecking = true;
            UpdateStatus = "Downloading update...";
        });
        try
        {
            if (_updateManager == null)
            {
                var locator = VelopackLocator.CreateDefaultForPlatform(null, null);
                _updateManager = new UpdateManager(new GithubSource(Skua.Core.Services.ClientUpdateService.UpdateRepository, null, false), new UpdateOptions { ExplicitChannel = Skua.Core.Services.ClientUpdateService.Channel }, locator);
            }

            Action<int> progressObj = (progress) => 
            {
                _dispatcherService.Invoke(() =>
                {
                    ProgressValue = progress;
                    UpdateStatus = $"Downloading... {progress}%";
                });
            };

            await _updateManager.DownloadUpdatesAsync(_updateInfo, progressObj);

            _dispatcherService.Invoke(() => UpdateStatus = "Installing update and restarting...");
            await Task.Run(() => _updateManager.ApplyUpdatesAndRestart(_updateInfo));
        }
        catch (Exception ex)
        {
            _dispatcherService.Invoke(() => UpdateStatus = $"Error updating: {ex.Message}");
        }
        finally
        {
            _dispatcherService.Invoke(() => IsChecking = false);
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstall()
    {
        await DownloadAndInstallAsync();
    }
}
