using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Utils;
using Skua.Manager.Avalonia.Services;
using Skua.Shared.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;

namespace Skua.Manager.Avalonia.ViewModels;

public partial class LauncherViewModel : BotControlViewModelBase, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IDispatcherService _dispatcherService;
    public RangedObservableCollection<ProcessInfo> SkuaProcesses { get; } = new();
    private Timer _timer;
    private bool _disposed;

    public LauncherViewModel(ISettingsService settingsService, IDispatcherService dispatcherService)
        : base("Launcher")
    {
        StrongReferenceMessenger.Default.Register<LauncherViewModel, UpdateStartedMessage>(this, TerminateProcesses);
        StrongReferenceMessenger.Default.Register<LauncherViewModel, AddProcessMessage>(this, AddProcessWithName);
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;

        _timer = new Timer
        {
            Interval = 1000
        };
        _timer.Elapsed += RemoveStoppedCurrentProcess!;
        _timer.AutoReset = true;
        _timer.Start();
    }

    private async void TerminateProcesses(LauncherViewModel recipient, UpdateStartedMessage message)
    {
        foreach (ProcessInfo? procInfo in recipient.SkuaProcesses.ToList())
        {
            await StopProcess(procInfo);
        }

        message.Reply(recipient.SkuaProcesses.Count == 0);
    }

    private void AddProcessWithName(LauncherViewModel recipient, AddProcessMessage message)
    {
        if (message.Process != null)
        {
            try
            {
                ProcessInfo procInfo = new(message.Process, message.AccountName ?? $"Bonxie #{message.Process.Id}");
                _dispatcherService.Invoke(() =>
                {
                    try
                    {
                        recipient.SkuaProcesses.Add(procInfo);
                    }
                    catch { }
                });
            }
            catch { }
        }
    }

    [RelayCommand]
    public async Task LaunchSkua()
    {
        await Task.Run(() =>
        {
            try
            {
                Process? proc = new ClientLaunch
                {
                    Server = _settingsService.Get("LastServer", "Twilly"),
                    Theme = _settingsService.Get("syncTheme", false) ? _settingsService.Get("CurrentTheme", "no-theme") : null,
                    GitHubToken = _settingsService.Get("UserGitHubToken", string.Empty)
                }.Start();
                if (proc != null)
                {
                    ProcessInfo procInfo = new(proc, $"Bonxie #{proc.Id}");
                    _dispatcherService.Invoke(() => SkuaProcesses.Add(procInfo));
                }
            }
            catch
            {
                IDialogService dialogService = Ioc.Default.GetService<IDialogService>()!;

                dialogService.ShowMessageBox($"Failed to launch Bonxie. Executable path: {SkuaExecutableLocator.ExecutablePath}", "Error");
            }
        });
    }

    public Task KillAllSkuaProcesses()
    {
        // Snapshot on the caller's (UI) thread: IDispatcherService.Invoke only posts, so reading the
        // collection through it from the background task returned an empty list and closed nothing.
        List<ProcessInfo> processes = SkuaProcesses.ToList();

        return Task.Run(async () =>
        {
            await Task.WhenAll(processes.Select(async procInfo =>
            {
                try
                {
                    await ProcessCloser.CloseAsync(procInfo.Process, ProcessCloser.DefaultTimeout);
                    _dispatcherService.Invoke(() => SkuaProcesses.Remove(procInfo));
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to stop process {procInfo.Process.Id}: {ex.Message}");
                }
            }));
        });
    }

    private void RemoveStoppedCurrentProcess(object source, System.Timers.ElapsedEventArgs e)
    {
        foreach (ProcessInfo? procInfo in SkuaProcesses.ToList())
        {
            if (procInfo.HasExited)
            {
                _dispatcherService.Invoke(() => SkuaProcesses.Remove(procInfo));
            }
        }
    }

    [RelayCommand]
    public async Task StopProcess(object? proc)
    {
        ProcessInfo? procInfo = proc as ProcessInfo;
        if (procInfo == null && proc is Process process)
        {
            // For backwards compatibility, find ProcessInfo by Process
            procInfo = SkuaProcesses.FirstOrDefault(p => p.Process == process);
        }

        if (procInfo == null)
            return;

        await Task.Run(async () =>
        {
            try
            {
                await ProcessCloser.CloseAsync(procInfo.Process, ProcessCloser.DefaultTimeout);
                _dispatcherService.Invoke(() => SkuaProcesses.Remove(procInfo));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to stop process: {ex}");
            }
        });
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Stop and dispose timer
                _timer?.Stop();
                _timer.Elapsed -= RemoveStoppedCurrentProcess!;
                _timer?.Dispose();

                // Unregister from messenger
                StrongReferenceMessenger.Default.Unregister<UpdateStartedMessage>(this);
                StrongReferenceMessenger.Default.Unregister<AddProcessMessage>(this);

                // Close any remaining processes, killing those that don't exit in time
                List<ProcessInfo> remaining = SkuaProcesses.ToList();
                try
                {
                    Task.Run(() => Task.WhenAll(remaining.Select(async procInfo =>
                    {
                        try
                        {
                            await ProcessCloser.CloseAsync(procInfo.Process, ProcessCloser.DefaultTimeout);
                        }
                        catch { }
                    }))).Wait(ProcessCloser.DefaultTimeout + TimeSpan.FromSeconds(2));
                }
                catch { }
                SkuaProcesses?.Clear();
            }
            _disposed = true;
        }
    }
}
