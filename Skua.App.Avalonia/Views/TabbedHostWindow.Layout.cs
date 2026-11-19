using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Material.Icons;
using Material.Icons.Avalonia;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.AppStartup;
using Skua.App.Avalonia.Services;
using static Skua.App.Avalonia.Services.TabHostMessages;
using Skua.Shared.Avalonia.ViewModels;
using Skua.Shared.Avalonia.ViewModels.ScriptRepo;
using Skua.Shared.Avalonia.ViewModels.Dialogs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Views;

// Grid view, tab strip scrolling and positioning of the embedded client windows.
public partial class TabbedHostWindow
{
    public void EnterGridView()
    {
        if (_tabs.Count <= 1 || _isTogglingGridView) return;

        _isTogglingGridView = true;
        try
        {
            _lastGridViewToggleTime = DateTime.UtcNow;
            _isGridViewEnabled = true;
            UpdateGridViewBorderColor();

            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PostMessage(info.ChildHwnd, WM_SKUA_GRIDVIEW, new IntPtr(1), IntPtr.Zero);
                }
            }

            if (InstancesTabControl.SelectedItem is TabItem currentTab)
                _lastSelectedTab = currentTab;
            _lastSelectedTab ??= InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();

            _ignoreSelectionChange = true;
            InstancesTabControl.SelectedItem = null;
            _ignoreSelectionChange = false;

            DoReposition();
        }
        finally
        {
            _isTogglingGridView = false;
        }
    }

    public void ExitGridView()
    {
        if (_isTogglingGridView) return;

        _isTogglingGridView = true;
        try
        {
            _lastGridViewToggleTime = DateTime.UtcNow;
            _isGridViewEnabled = false;
            UpdateGridViewBorderColor();

            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                {
                    PostMessage(info.ChildHwnd, WM_SKUA_GRIDVIEW, IntPtr.Zero, IntPtr.Zero);
                }
            }

            _ignoreSelectionChange = true;
            if (_lastSelectedTab != null && InstancesTabControl.Items.Contains(_lastSelectedTab))
            {
                InstancesTabControl.SelectedItem = _lastSelectedTab;
            }
            else
            {
                var firstTab = InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();
                if (firstTab != null)
                    InstancesTabControl.SelectedItem = firstTab;
            }
            _ignoreSelectionChange = false;

            DoReposition();
        }
        finally
        {
            _isTogglingGridView = false;
        }
    }

    private void GridViewBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;

        if (DateTime.UtcNow - _lastGridViewToggleTime < _gridViewToggleCooldown)
            return;

        if (_isGridViewEnabled)
        {
            ExitGridView();
        }
        else
        {
            if (_tabs.Count <= 1)
            {
                // If only 1 account, Grid View has nothing to tile; guarantee normal tab view
                ExitGridView();
                return;
            }
            EnterGridView();
        }
    }

    private void GridViewBorder_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_isGridViewEnabled)
            GridViewBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaNavHoverBrush");
    }

    private void GridViewBorder_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!_isGridViewEnabled)
            GridViewBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSurfaceAltBrush");
    }

    private void AddTabButton_Click(object? sender, RoutedEventArgs e)
    {
        AddNewInstance();
    }

    private void AccountLaunchToggle_Click(object? sender, RoutedEventArgs e)
    {
        RefreshQuickLaunchAccounts();
    }

    private void AccountQuickLaunchSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        FilterQuickLaunchAccounts();
    }

    private void AccountQuickLaunchSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        string search = AccountQuickLaunchSearch.Text?.Trim() ?? string.Empty;
        foreach ((string Username, string DisplayName) account in _quickLaunchAccounts)
        {
            if (string.IsNullOrWhiteSpace(search)
                || account.Username.Contains(search, StringComparison.OrdinalIgnoreCase)
                || account.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                _quickLaunchSelection.Add(account.Username);
        }

        FilterQuickLaunchAccounts();
    }

    private void AccountQuickLaunchClear_Click(object? sender, RoutedEventArgs e)
    {
        _quickLaunchSelection.Clear();
        FilterQuickLaunchAccounts();
    }

    private void AccountQuickLaunchLaunch_Click(object? sender, RoutedEventArgs e)
    {
        LaunchSelectedQuickAccounts();
    }

    private void LaunchSelectedQuickAccounts()
    {
        List<string> selectedUsernames = _quickLaunchAccounts
            .Where(account => _quickLaunchSelection.Contains(account.Username))
            .Select(account => account.Username)
            .ToList();
        if (selectedUsernames.Count == 0)
            return;

        LaunchAccountBatch(selectedUsernames);
        _quickLaunchSelection.Clear();
        FilterQuickLaunchAccounts();
    }

    private void AccountQuickLaunchNewWindow_Click(object? sender, RoutedEventArgs e)
    {
        LaunchSelectedQuickAccountsInNewWindow();
    }

    private void LaunchSelectedQuickAccountsInNewWindow()
    {
        List<string> selectedUsernames = _quickLaunchAccounts
            .Where(account => _quickLaunchSelection.Contains(account.Username))
            .Select(account => account.Username)
            .ToList();
        if (selectedUsernames.Count == 0)
            return;

        LaunchAccountsInNewWindow(selectedUsernames);
        _quickLaunchSelection.Clear();
        FilterQuickLaunchAccounts();
    }

    private void AccountQuickLaunchGroupLaunch_Click(object? sender, RoutedEventArgs e)
    {
        if (AccountQuickLaunchGroupCombo.SelectedItem is not ComboBoxItem selectedItem
            || selectedItem.Tag is not string groupName)
            return;

        GroupData? group = _quickLaunchGroups.FirstOrDefault(item =>
            string.Equals(item.Name, groupName, StringComparison.OrdinalIgnoreCase));
        if (group?.Accounts != null)
            LaunchAccountBatch(group.Accounts);
    }

    private void AccountQuickLaunchGroupNewWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (AccountQuickLaunchGroupCombo.SelectedItem is not ComboBoxItem selectedItem
            || selectedItem.Tag is not string groupName)
            return;

        GroupData? group = _quickLaunchGroups.FirstOrDefault(item =>
            string.Equals(item.Name, groupName, StringComparison.OrdinalIgnoreCase));
        if (group?.Accounts != null)
            LaunchAccountsInNewWindow(group.Accounts);
    }

    private void LaunchAccountsInNewWindow(IEnumerable<string> usernames)
    {
        List<string> accounts = usernames
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Select(username => username.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accounts.Count == 0)
            return;

        _quickLaunchFailed.Clear();

        try
        {
            ISettingsService settings = Ioc.Default.GetRequiredService<ISettingsService>();
            settings.ReloadSettings();
            Dictionary<string, AccountData>? savedAccounts = settings.Get<Dictionary<string, AccountData>>("ManagedAccounts");
            string defaultServer = settings.Get("LastServer", "Twilly") ?? "Twilly";

            List<string[]> launchArguments = [];
            int alreadyRunning = 0;
            int unavailable = 0;

            foreach (string username in accounts)
            {
                // Never duplicate an account that is already running in any Bonxie host.
                if (FindExistingAccountTab(username) is not null
                    || IsExternalAccountRunning(username))
                {
                    alreadyRunning++;
                    continue;
                }

                if (savedAccounts is null || !savedAccounts.TryGetValue(username, out AccountData? account) || account is null)
                {
                    unavailable++;
                    continue;
                }

                string password = account.Password ?? string.Empty;
                if (password.StartsWith("enc:", StringComparison.Ordinal))
                {
                    try
                    {
                        password = Skua.Core.Utils.CryptoUtils.Decrypt(password);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Could not decrypt saved password for '{username}': {ex.Message}");
                        unavailable++;
                        continue;
                    }
                }

                launchArguments.Add(["-u", username, "-p", password, "-s", defaultServer]);
            }

            if (launchArguments.Count == 0)
            {
                AccountQuickLaunchProgressPanel.IsVisible = true;
                AccountQuickLaunchProgressBar.Value = 100;
                AccountQuickLaunchProgressText.Text = alreadyRunning > 0
                    ? $"No new window needed · {alreadyRunning} already running · {unavailable} unavailable"
                    : $"No accounts available to launch · {unavailable} unavailable";
                AccountQuickLaunchRetryButton.IsVisible = false;
                return;
            }

            string hostInstanceId = Guid.NewGuid().ToString("N");
            string executablePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("Could not locate the Bonxie executable.");

            ProcessStartInfo startInfo = new();
            if (executablePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                || executablePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = executablePath;
                startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Bonxie.dll"));
            }
            else
            {
                startInfo.FileName = executablePath;
            }

            startInfo.ArgumentList.Add("--host-instance");
            startInfo.ArgumentList.Add(hostInstanceId);
            // The password goes to the new window in its environment, not on its command line.
            foreach (string argument in Skua.Core.Utils.StartupSecrets.MoveToEnvironment(launchArguments[0], startInfo.Environment))
                startInfo.ArgumentList.Add(argument);

            // Kept (not disposed here) so the background watcher below can tell when the new window closes.
            Process newHost = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not return a process for the new Bonxie window.");

            AccountQuickLaunchProgressPanel.IsVisible = true;
            AccountQuickLaunchProgressBar.Value = 0;
            AccountQuickLaunchProgressText.Text = "Opening separate Bonxie window...";
            AccountQuickLaunchRetryButton.IsVisible = false;

            _ = Task.Run(async () =>
            {
                using Process hostProcess = newHost;
                int queued = 1;
                for (int i = 1; i < launchArguments.Count; i++)
                {
                    // Stops retrying the pipe as soon as the new window exits.
                    if (!await SendClientArgsToHostInstanceAsync(hostInstanceId, launchArguments[i], hostProcess))
                        break;
                    queued++;
                }

                List<string> queuedUsernames = launchArguments
                    .Take(queued)
                    .Select(arguments => arguments[1])
                    .ToList();
                List<string> undeliveredUsernames = launchArguments
                    .Skip(queued)
                    .Select(arguments => arguments[1])
                    .ToList();
                // Provisional until the end: undelivered accounts only count as failed if the window is still open.
                int failed = unavailable + undeliveredUsernames.Count;

                Dispatcher.UIThread.Post(() =>
                {
                    foreach (string username in queuedUsernames)
                        _quickLaunchPending.Add(username);
                    UpdateQuickLaunchStatuses();
                });

                bool hostClosed = false;
                int loggedIn = 0;
                HashSet<string> runningAccounts = new(StringComparer.OrdinalIgnoreCase);
                for (int elapsedSeconds = 0; elapsedSeconds < 180 && loggedIn < queuedUsernames.Count; elapsedSeconds++)
                {
                    // Its accounts can't log in once the new window is gone, so stop waiting for them.
                    if (HasProcessExited(hostProcess))
                    {
                        hostClosed = true;
                        break;
                    }

                    runningAccounts = GetRunningAccountUsernames();
                    loggedIn = queuedUsernames.Count(runningAccounts.Contains);
                    int currentLoggedIn = loggedIn;
                    int currentStarting = queuedUsernames.Count - currentLoggedIn;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_isClosing)
                            return;

                        UpdateQuickLaunchStatuses();
                        AccountQuickLaunchProgressBar.Value = queuedUsernames.Count == 0
                            ? 100
                            : currentLoggedIn * 100d / queuedUsernames.Count;
                        AccountQuickLaunchProgressText.Text = currentStarting == 0
                            ? $"New window ready · {currentLoggedIn} accounts running · {alreadyRunning} already running · {failed} unavailable/failed"
                            : $"New window opened · {queued} queued · {currentLoggedIn}/{queuedUsernames.Count} logged in · {currentStarting} still starting · {alreadyRunning} already running · {failed} unavailable/failed";
                    });

                    if (currentStarting > 0)
                        await Task.Delay(1000);
                }

                // The window can also exit after the last scan (e.g. right after every account logged in), so check
                // once more and report from a fresh scan rather than a snapshot taken while it was still open.
                if (hostClosed || HasProcessExited(hostProcess))
                {
                    hostClosed = true;
                    runningAccounts = GetRunningAccountUsernames();
                    loggedIn = queuedUsernames.Count(runningAccounts.Contains);
                    failed = unavailable;
                }

                List<string> timedOutUsernames = queuedUsernames
                    .Where(username => !runningAccounts.Contains(username))
                    .ToList();
                int notStarted = timedOutUsernames.Count + (hostClosed ? undeliveredUsernames.Count : 0);
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (string username in queuedUsernames)
                        CompleteQuickLaunchPending(username, runningAccounts.Contains(username));
                    // Decided once, here: a closed window's accounts are "Not running", not failed.
                    foreach (string username in timedOutUsernames.Concat(undeliveredUsernames))
                    {
                        if (hostClosed)
                            _quickLaunchFailed.Remove(username);
                        else
                            _quickLaunchFailed.Add(username);
                    }

                    UpdateQuickLaunchStatuses();
                    AccountQuickLaunchProgressBar.Value = 100;
                    AccountQuickLaunchProgressText.Text = hostClosed
                        ? $"New window closed · {loggedIn} running · {notStarted} not started · {alreadyRunning} already running · {failed} unavailable/undelivered"
                        : timedOutUsernames.Count > 0
                        ? $"New window launch timed out · {loggedIn} running · {timedOutUsernames.Count} timed out · {alreadyRunning} already running · {failed} unavailable/undelivered"
                        : $"New window ready · {loggedIn} accounts running · {alreadyRunning} already running · {failed} unavailable/undelivered";
                    AccountQuickLaunchRetryButton.IsVisible = _quickLaunchFailed.Count > 0;
                });
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to open a separate Bonxie window: {ex}");
            foreach (string username in accounts)
                _quickLaunchFailed.Add(username);
            AccountQuickLaunchProgressPanel.IsVisible = true;
            AccountQuickLaunchProgressBar.Value = 100;
            AccountQuickLaunchProgressText.Text = "Could not open a separate Bonxie window. All requested accounts failed.";
            AccountQuickLaunchRetryButton.IsVisible = _quickLaunchFailed.Count > 0;
        }
    }

    private static async Task<bool> SendClientArgsToHostInstanceAsync(string hostInstanceId, string[] clientArgs, Process? hostProcess = null)
    {
        string pipeName = $"BonxieTabHostPipe-{hostInstanceId}";
        string payload = $"LAUNCH|{JsonSerializer.Serialize(clientArgs)}";

        for (int attempt = 0; attempt < 30; attempt++)
        {
            // No point retrying a pipe whose window has exited.
            if (hostProcess is not null && HasProcessExited(hostProcess))
                return false;

            try
            {
                using NamedPipeClientStream pipeClient = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(500);
                using StreamWriter writer = new(pipeClient, System.Text.Encoding.UTF8, 1024, leaveOpen: true);
                await writer.WriteLineAsync(payload);
                await writer.FlushAsync();

                using StreamReader reader = new(pipeClient, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                string? acknowledgement = await reader.ReadLineAsync();
                return string.Equals(acknowledgement, "ACK", StringComparison.Ordinal);
            }
            catch (TimeoutException)
            {
                await Task.Delay(200);
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
        }

        return false;
    }

    private void AccountQuickLaunchRetry_Click(object? sender, RoutedEventArgs e)
    {
        if (_quickLaunchFailed.Count > 0)
            LaunchAccountBatch(_quickLaunchFailed.ToArray());
    }

    private void LaunchAccountBatch(IEnumerable<string> usernames)
    {
        List<string> accounts = usernames
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Select(username => username.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (accounts.Count == 0)
            return;

        try
        {
            ISettingsService settings = Ioc.Default.GetRequiredService<ISettingsService>();
            settings.ReloadSettings();
            Dictionary<string, AccountData>? savedAccounts = settings.Get<Dictionary<string, AccountData>>("ManagedAccounts");

            // Retry applies only to failures from this batch.
            _quickLaunchFailed.Clear();

            int batchId = ++_quickLaunchBatchId;
            _quickLaunchBatchTotal = accounts.Count;
            _quickLaunchBatchCompleted = 0;
            _quickLaunchBatchAlreadyOpen = 0;
            _quickLaunchBatchFailed = 0;

            foreach (string username in accounts)
            {
                Action<bool> batchCompletion = success =>
                {
                    if (batchId != _quickLaunchBatchId)
                    {
                        UpdateQuickLaunchStatuses();
                        return;
                    }

                    _quickLaunchBatchCompleted++;
                    if (success)
                        _quickLaunchFailed.Remove(username);
                    else
                    {
                        _quickLaunchFailed.Add(username);
                        _quickLaunchBatchFailed++;
                    }

                    UpdateQuickLaunchStatuses();
                    UpdateQuickLaunchBatchProgress();
                };

                // Join an in-flight request before checking running state. The original
                // launch owns completion, so this batch must wait for its final result.
                if (_quickLaunchPending.Contains(username))
                {
                    if (!_quickLaunchPendingCallbacks.TryGetValue(username, out List<Action<bool>>? callbacks))
                    {
                        callbacks = [];
                        _quickLaunchPendingCallbacks[username] = callbacks;
                    }
                    callbacks.Add(batchCompletion);
                    continue;
                }

                TabItem? existingTab = FindExistingAccountTab(username);
                if (existingTab != null)
                {
                    FocusAccountTab(existingTab);
                    _quickLaunchBatchAlreadyOpen++;
                    _quickLaunchBatchCompleted++;
                    continue;
                }

                if (savedAccounts == null || !savedAccounts.ContainsKey(username))
                {
                    _quickLaunchFailed.Add(username);
                    _quickLaunchBatchFailed++;
                    _quickLaunchBatchCompleted++;
                    continue;
                }

                if (IsExternalAccountRunning(username))
                {
                    _quickLaunchBatchAlreadyOpen++;
                    _quickLaunchBatchCompleted++;
                    continue;
                }

                _quickLaunchPending.Add(username);
                _quickLaunchPendingCallbacks[username] = [batchCompletion];
                LaunchAccount(username, onCompleted: success => CompleteQuickLaunchPending(username, success));
            }
            UpdateQuickLaunchStatuses();
            UpdateQuickLaunchBatchProgress();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Quick Account Launch batch failed: {ex}");
            foreach (string username in accounts)
                _quickLaunchFailed.Add(username);
            _quickLaunchBatchTotal = accounts.Count;
            _quickLaunchBatchCompleted = accounts.Count;
            _quickLaunchBatchAlreadyOpen = 0;
            _quickLaunchBatchFailed = accounts.Count;
            UpdateQuickLaunchBatchProgress();
        }
    }

    private static bool HasProcessExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void CompleteQuickLaunchPending(string username, bool success)
    {
        _quickLaunchPending.Remove(username);
        if (_quickLaunchPendingCallbacks.Remove(username, out List<Action<bool>>? callbacks))
        {
            foreach (Action<bool> callback in callbacks)
                callback(success);
        }

        UpdateQuickLaunchStatuses();
    }

    private void UpdateQuickLaunchBatchProgress()
    {
        if (_quickLaunchBatchTotal <= 0)
        {
            AccountQuickLaunchProgressPanel.IsVisible = false;
            return;
        }

        AccountQuickLaunchProgressPanel.IsVisible = true;
        int completed = Math.Min(_quickLaunchBatchCompleted, _quickLaunchBatchTotal);
        AccountQuickLaunchProgressBar.Value = completed * 100d / _quickLaunchBatchTotal;
        AccountQuickLaunchProgressText.Text = completed >= _quickLaunchBatchTotal
            ? $"Finished: {_quickLaunchBatchTotal - _quickLaunchBatchAlreadyOpen - _quickLaunchBatchFailed} launched · {_quickLaunchBatchAlreadyOpen} already running · {_quickLaunchBatchFailed} failed"
            : $"Launching: {completed}/{_quickLaunchBatchTotal} complete · {_quickLaunchBatchAlreadyOpen} already running · {_quickLaunchBatchFailed} failed";
        AccountQuickLaunchRetryButton.IsVisible = _quickLaunchBatchFailed > 0;
    }

    private void OpenQuickLaunch()
    {
        if (_isClosing)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing)
                return;

            // A global hotkey can be triggered while an embedded game window owns
            // keyboard focus. Activate the host before opening and focusing the popup.
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();

            RefreshQuickLaunchAccounts();
            AccountLaunchToggle.IsChecked = true;

            // Popup content is created after IsOpen changes, so focus it on the
            // following dispatcher pass rather than leaving focus in the game client.
            Dispatcher.UIThread.Post(() =>
            {
                if (_isClosing || !AccountQuickLaunchPopup.IsOpen)
                    return;

                Activate();
                Topmost = true;
                Topmost = false;
                AccountQuickLaunchSearch.Focus();
            }, DispatcherPriority.Input);
        }, DispatcherPriority.Input);
    }

    private void AccountQuickLaunchSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_quickLaunchSelection.Count > 0)
                LaunchSelectedQuickAccounts();

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseQuickLaunchPopup();
            e.Handled = true;
        }
    }

    private void CloseQuickLaunchPopup()
    {
        AccountQuickLaunchPopup.IsOpen = false;
        AccountLaunchToggle.IsChecked = false;
        AccountLaunchToggle.Focus();
    }

    private void FocusAccountTab(TabItem tab)
    {
        InstancesTabControl.SelectedItem = tab;
        ScrollTabIntoView(tab);
    }

    private TabItem? FindExistingAccountTab(string username)
    {
        foreach (KeyValuePair<TabItem, TabInfo> tabEntry in _tabs)
        {
            TabInfo tabInfo = tabEntry.Value;
            if (tabInfo.Process == null || tabInfo.Process.HasExited)
                continue;

            // A tab title or launch argument is not proof of an authenticated account.
            string? loggedInUsername = GetProcessLoggedInUsername(tabInfo);
            if (string.Equals(loggedInUsername, username, StringComparison.OrdinalIgnoreCase))
            {
                tabInfo.Username = loggedInUsername.Trim();
                return tabEntry.Key;
            }
        }

        return null;
    }

    private string GetQuickLaunchAccountStatus(string username)
    {
        foreach (TabInfo tabInfo in _tabs.Values)
        {
            if (tabInfo.Process == null || tabInfo.Process.HasExited)
                continue;

            string? loggedInUsername = GetProcessLoggedInUsername(tabInfo);
            if (string.Equals(loggedInUsername, username, StringComparison.OrdinalIgnoreCase))
                return "Running";
            if (tabInfo.LaunchCompletion != null
                && string.Equals(tabInfo.Username, username, StringComparison.OrdinalIgnoreCase))
                return "Launching";
        }

        if (IsExternalAccountRunning(username))
            return "Running";

        if (_quickLaunchPending.Contains(username))
            return "Launching";
        if (_quickLaunchFailed.Contains(username))
            return "Failed";
        return "Not running";
    }

    /// <summary>
    /// Finds a logged-in account running in another Bonxie host by checking
    /// the per-process username files shared through the temporary tabs directory.
    /// </summary>
    private static bool IsExternalAccountRunning(string username)
    {
        return GetRunningAccountUsernames().Contains(username);
    }

    /// <summary>
    /// Reads the shared per-process username records for every active game client.
    /// These records allow Quick Launch to detect accounts hosted by other windows.
    /// </summary>
    private static HashSet<string> GetRunningAccountUsernames()
    {
        HashSet<string> runningAccounts = new(StringComparer.OrdinalIgnoreCase);
        string directory = TabHostFiles.TabsDirectory;
        if (!Directory.Exists(directory))
            return runningAccounts;

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.txt"))
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(fileName, out int processId) || processId == Environment.ProcessId)
                    continue;

                try
                {
                    using Process process = Process.GetProcessById(processId);
                    if (process.HasExited
                        || !string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // A stale username file can outlive its process and be mistaken for a
                    // new process after Windows reuses the PID.
                    DateTime processStartUtc = process.StartTime.ToUniversalTime();
                    if (File.GetLastWriteTimeUtc(file) < processStartUtc)
                        continue;

                    string locationFile = Path.Combine(directory, $"{processId}.location.json");
                    if (File.Exists(locationFile))
                    {
                        using JsonDocument location = JsonDocument.Parse(File.ReadAllText(locationFile));
                        if (location.RootElement.TryGetProperty("LoggedIn", out JsonElement loggedIn)
                            && loggedIn.ValueKind == JsonValueKind.False)
                            continue;
                    }

                    string loggedInUsername = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrWhiteSpace(loggedInUsername))
                        runningAccounts.Add(loggedInUsername);
                }
                catch (ArgumentException)
                {
                    // The process exited after its username file was enumerated.
                }
                catch (IOException)
                {
                    // The game client may be updating its username file.
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore files that cannot be read.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // The process may exit or deny access while its identity is checked.
                }
                catch (JsonException)
                {
                    // Ignore malformed location records.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The shared directory may be cleaned up while scanning it.
        }
        catch (IOException)
        {
            // Ignore transient directory access failures.
        }
        catch (UnauthorizedAccessException)
        {
            // The shared directory is not accessible.
        }

        return runningAccounts;
    }

    private void UpdateQuickLaunchStatuses()
    {
        foreach (KeyValuePair<string, (Border Bubble, TextBlock Text)> entry in _quickLaunchStatusBubbles)
            SetQuickLaunchStatusBubble(entry.Value.Bubble, entry.Value.Text, GetQuickLaunchAccountStatus(entry.Key));
    }

    private static void SetQuickLaunchStatusBubble(Border bubble, TextBlock text, string status)
    {
        (string Background, string Foreground) colors = status switch
        {
            "Running" => ("#174B35", "#8DE2B5"),
            "Launching" => ("#493A18", "#F5D58A"),
            "Failed" => ("#542A2A", "#FFAAAA"),
            _ => ("#343943", "#C4CAD4")
        };

        text.Text = status;
        text.Foreground = new SolidColorBrush(Color.Parse(colors.Foreground));
        bubble.Background = new SolidColorBrush(Color.Parse(colors.Background));
    }

    private void RefreshQuickLaunchAccounts()
    {
        try
        {
            ISettingsService settings = Ioc.Default.GetRequiredService<ISettingsService>();
            settings.ReloadSettings();
            Dictionary<string, AccountData>? savedAccounts = settings.Get<Dictionary<string, AccountData>>("ManagedAccounts");

            _quickLaunchAccounts.Clear();
            _quickLaunchGroups.Clear();
            AccountQuickLaunchGroupCombo.Items.Clear();

            List<GroupData> savedGroups = settings.Get<List<GroupData>>("AccountGroups") ?? new List<GroupData>();
            foreach (GroupData group in savedGroups)
            {
                if (string.IsNullOrWhiteSpace(group.Name) || group.Accounts == null)
                    continue;

                _quickLaunchGroups.Add(group);
                AccountQuickLaunchGroupCombo.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group.Name });
            }

            AccountQuickLaunchGroupCombo.SelectedIndex = _quickLaunchGroups.Count > 0 ? 0 : -1;
            AccountQuickLaunchGroupButton.IsEnabled = _quickLaunchGroups.Count > 0;
            AccountQuickLaunchGroupNewWindowButton.IsEnabled = _quickLaunchGroups.Count > 0;

            if (savedAccounts != null)
            {
                foreach (KeyValuePair<string, AccountData> account in savedAccounts)
                {
                    if (string.IsNullOrWhiteSpace(account.Key))
                        continue;

                    string displayName = string.IsNullOrWhiteSpace(account.Value?.DisplayName)
                        ? account.Key
                        : account.Value.DisplayName;
                    _quickLaunchAccounts.Add((account.Key, displayName));
                }
            }

            _quickLaunchSelection.RemoveWhere(username =>
                !_quickLaunchAccounts.Any(account => string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase)));

            AccountQuickLaunchCountText.Text = _quickLaunchAccounts.Count == 1
                ? "1 saved Manager account"
                : $"{_quickLaunchAccounts.Count} saved Manager accounts";
            FilterQuickLaunchAccounts();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load Manager accounts for Quick Account Launch: {ex}");
            _quickLaunchAccounts.Clear();
            _quickLaunchSelection.Clear();
            AccountQuickLaunchCountText.Text = "Manager accounts unavailable";
            AccountQuickLaunchRows.Children.Clear();
            AccountQuickLaunchRows.IsVisible = false;
            AccountQuickLaunchEmptyText.Text = "Could not load Manager accounts.";
            AccountQuickLaunchEmptyText.IsVisible = true;
            UpdateQuickLaunchSelectionSummary();
        }
    }

    private void FilterQuickLaunchAccounts()
    {
        string search = AccountQuickLaunchSearch.Text?.Trim() ?? string.Empty;
        List<(string Username, string DisplayName)> visibleAccounts = _quickLaunchAccounts
            .Where(account => string.IsNullOrWhiteSpace(search)
                || account.Username.Contains(search, StringComparison.OrdinalIgnoreCase)
                || account.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        AccountQuickLaunchRows.Children.Clear();
        _quickLaunchStatusBubbles.Clear();
        foreach ((string Username, string DisplayName) account in visibleAccounts)
        {
            StackPanel accountNameContent = new()
            {
                Orientation = Orientation.Vertical,
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock displayName = new()
            {
                Text = account.DisplayName,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            displayName[!TextBlock.ForegroundProperty] =
                new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");
            accountNameContent.Children.Add(displayName);

            if (!string.Equals(account.DisplayName, account.Username, StringComparison.OrdinalIgnoreCase))
            {
                TextBlock username = new()
                {
                    Text = account.Username,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center
                };
                username[!TextBlock.ForegroundProperty] =
                    new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaMutedTextBrush");
                accountNameContent.Children.Add(username);
            }

            StackPanel accountContent = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            accountContent.Children.Add(accountNameContent);

            TextBlock statusText = new()
            {
                FontSize = 9,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Border statusBubble = new()
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = statusText
            };
            SetQuickLaunchStatusBubble(statusBubble, statusText, GetQuickLaunchAccountStatus(account.Username));
            _quickLaunchStatusBubbles[account.Username] = (statusBubble, statusText);
            accountContent.Children.Add(statusBubble);

            CheckBox checkBox = new()
            {
                Content = accountContent,
                IsChecked = _quickLaunchSelection.Contains(account.Username),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 3)
            };
            checkBox.Click += (sender, args) =>
            {
                if (checkBox.IsChecked == true)
                    _quickLaunchSelection.Add(account.Username);
                else
                    _quickLaunchSelection.Remove(account.Username);

                UpdateQuickLaunchSelectionSummary();
            };
            checkBox.KeyDown += (sender, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    LaunchSelectedQuickAccounts();
                    args.Handled = true;
                }
                else if (args.Key == Key.Escape)
                {
                    CloseQuickLaunchPopup();
                    args.Handled = true;
                }
            };

            AccountQuickLaunchRows.Children.Add(checkBox);
        }

        AccountQuickLaunchRows.IsVisible = visibleAccounts.Count > 0;
        AccountQuickLaunchEmptyText.IsVisible = visibleAccounts.Count == 0;
        AccountQuickLaunchEmptyText.Text = _quickLaunchAccounts.Count == 0
            ? "No Manager accounts saved yet."
            : "No accounts match your search.";
        UpdateQuickLaunchSelectionSummary();
        UpdateQuickLaunchStatuses();
    }

    private void UpdateQuickLaunchSelectionSummary()
    {
        int selectedCount = _quickLaunchSelection.Count;
        AccountQuickLaunchSelectionText.Text = $"{selectedCount} selected";
        AccountQuickLaunchLaunchText.Text = selectedCount == 0
            ? "Launch Selected"
            : $"Launch {selectedCount} {(selectedCount == 1 ? "Account" : "Accounts")}";
        AccountQuickLaunchLaunchButton.IsEnabled = selectedCount > 0;
        AccountQuickLaunchNewWindowButton.IsEnabled = selectedCount > 0;
    }

    private void ScrollTabsLeftButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TabScrollViewer == null) return;
        var newOffset = Math.Max(0, TabScrollViewer.Offset.X - 160);
        TabScrollViewer.Offset = new Vector(newOffset, 0);
        UpdateTabScrollButtons();
    }

    private void ScrollTabsRightButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TabScrollViewer == null) return;
        double maxScroll = Math.Max(0, TabScrollViewer.Extent.Width - TabScrollViewer.Viewport.Width);
        var newOffset = Math.Min(maxScroll, TabScrollViewer.Offset.X + 160);
        TabScrollViewer.Offset = new Vector(newOffset, 0);
        UpdateTabScrollButtons();
    }

    private void UpdateTabScrollButtons()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (TabScrollViewer == null || ScrollTabsLeftButton == null || ScrollTabsRightButton == null)
                return;

            double maxScroll = Math.Max(0, TabScrollViewer.Extent.Width - TabScrollViewer.Viewport.Width);
            if (maxScroll > 2)
            {
                ScrollTabsLeftButton.IsVisible = TabScrollViewer.Offset.X > 1;
                ScrollTabsRightButton.IsVisible = TabScrollViewer.Offset.X < maxScroll - 1;
            }
            else
            {
                ScrollTabsLeftButton.IsVisible = false;
                ScrollTabsRightButton.IsVisible = false;
            }
        }, DispatcherPriority.Normal);
    }

    private void ScrollTabIntoView(TabItem? tab)
    {
        if (tab == null || TabScrollViewer == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var tabBounds = tab.Bounds;
                var currentOffset = TabScrollViewer.Offset.X;
                var viewportWidth = TabScrollViewer.Viewport.Width;

                if (viewportWidth <= 0 || tabBounds.Width <= 0) return;

                if (tabBounds.Left < currentOffset)
                {
                    TabScrollViewer.Offset = new Vector(Math.Max(0, tabBounds.Left), 0);
                }
                else if (tabBounds.Right > currentOffset + viewportWidth)
                {
                    var target = tabBounds.Right - viewportWidth;
                    TabScrollViewer.Offset = new Vector(Math.Max(0, target), 0);
                }
            }
            catch { }
        }, DispatcherPriority.Loaded);
    }

    private void InstancesTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source != InstancesTabControl || _ignoreSelectionChange) return;

        if (_isGridViewEnabled && InstancesTabControl.SelectedItem != null)
        {
            // ExitGridView restores _lastSelectedTab, so point it at the tab that was just clicked.
            if (InstancesTabControl.SelectedItem is TabItem clickedTab)
                _lastSelectedTab = clickedTab;
            ExitGridView();
            return;
        }

        DoReposition();
        UpdateTabScrollButtons();
        if (InstancesTabControl.SelectedItem is TabItem selTab)
        {
            ScrollTabIntoView(selTab);
        }
    }

    private void GlobeBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        global::Avalonia.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(GlobeBorder);
    }

    private void GlobeBorder_PointerEntered(object? sender, PointerEventArgs e)
    {
        GlobeBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaNavHoverBrush");
    }

    private void GlobeBorder_PointerExited(object? sender, PointerEventArgs e)
    {
        GlobeBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSurfaceAltBrush");
    }

    private void UpdateGridViewBorderColor()
    {
        if (_isGridViewEnabled)
        {
            GridViewBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSelectionBrush");
            GridViewBorder[!Border.BorderBrushProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");
            if (GridViewText != null) GridViewText[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");
            if (GridViewIcon != null) GridViewIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");
        }
        else
        {
            GridViewBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSurfaceAltBrush");
            GridViewBorder[!Border.BorderBrushProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaBorderBrush");
            if (GridViewText != null) GridViewText[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");
            if (GridViewIcon != null) GridViewIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");
        }
    }

    /// <summary>
    /// Hides all embedded game windows to prevent their pixels remaining visible when the client is hidden.
    /// </summary>
    public void HideEmbeddedClients()
    {
        foreach (TabInfo info in _tabs.Values)
        {
            if (info.ChildHwnd != IntPtr.Zero)
                ShowWindow(info.ChildHwnd, SW_HIDE);
        }

        HotKeys.ActiveChildHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Restores embedded game windows after the client is shown again.
    /// </summary>
    public void ShowEmbeddedClients()
    {
        _lastX = int.MinValue;
        _lastY = int.MinValue;
        _lastW = int.MinValue;
        _lastH = int.MinValue;
        _lastActiveHwnd = IntPtr.Zero;
        ScheduleReposition();
    }

    // Places the tabs now, even mid-drag. A drag only moves them (FastReposition) at the size and offset cached when
    // it started, which are wrong once the host or a child has crossed onto a monitor with another scale.
    private void RepositionWithFreshCache()
    {
        if (_isClosing) return;

        bool dragging = _isDraggingWindow;
        _isDraggingWindow = false;
        _lastX = int.MinValue;
        _lastY = int.MinValue;
        _lastW = int.MinValue;
        _lastH = int.MinValue;
        _lastActiveHwnd = IntPtr.Zero;
        DoReposition();
        _isDraggingWindow = dragging;
    }

    private void OnWindowPositionChanged(PixelPoint newPos)
    {
        if (!IsLoaded || _isClosing || WindowState == WindowState.Minimized || !IsVisible)
            return;

        if (_isRepositioning)
            return;

        if (_childWidth <= 1 || _childHeight <= 1)
        {
            DoReposition();
            return;
        }

        if (newPos.X == _lastFastX && newPos.Y == _lastFastY)
            return;

        _lastFastX = newPos.X;
        _lastFastY = newPos.Y;

        FastReposition(newPos);
    }

    private void FastReposition(PixelPoint newPos)
    {
        if (_childWidth <= 1 || _childHeight <= 1)
            return;

        if (_isGridViewEnabled)
        {
            for (int i = 0; i < _gridTiles.Count; i++)
            {
                var tile = _gridTiles[i];
                if (tile.Hwnd != IntPtr.Zero)
                {
                    SetWindowPos(tile.Hwnd, IntPtr.Zero,
                        newPos.X + tile.OffsetX,
                        newPos.Y + tile.OffsetY,
                        tile.Width,
                        tile.Height,
                        SWP_NOACTIVATE | SWP_NOZORDER);
                    ShowWindow(tile.Hwnd, SW_SHOW);
                }
            }
            return;
        }

        IntPtr activeChildHwnd = _lastActiveHwnd;
        if (activeChildHwnd == IntPtr.Zero)
        {
            if (InstancesTabControl.SelectedItem is TabItem tab && _tabs.TryGetValue(tab, out var info))
                activeChildHwnd = info.ChildHwnd;
            if (activeChildHwnd == IntPtr.Zero && _tabs.Count > 0)
                activeChildHwnd = _tabs.Values.FirstOrDefault(t => t.ChildHwnd != IntPtr.Zero)?.ChildHwnd ?? IntPtr.Zero;
            _lastActiveHwnd = activeChildHwnd;
        }

        if (activeChildHwnd != IntPtr.Zero)
        {
            int targetX = newPos.X + _childOffsetX;
            int targetY = newPos.Y + _childOffsetY;

            SetWindowPos(activeChildHwnd, IntPtr.Zero,
                targetX,
                targetY,
                _childWidth,
                _childHeight,
                SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOZORDER);
        }
    }

    private bool _repositionScheduled = false;

    private void ScheduleReposition()
    {
        _needsReposition = true;
        if (_repositionScheduled || _isClosing) return;

        _repositionScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _repositionScheduled = false;
            if (_isClosing || !IsLoaded) return;

            _needsReposition = false;
            DoReposition();
        }, DispatcherPriority.Render);
    }

    private void DoReposition()
    {
        if (!IsLoaded || _isClosing || _isDraggingWindow) return;

        if (_isRepositioning)
        {
            _needsReposition = true;
            return;
        }

        _isRepositioning = true;
        try
        {
            TabItem? selectedTab = InstancesTabControl.SelectedItem as TabItem;
            if (!_isGridViewEnabled && selectedTab == null)
            {
                selectedTab = (_lastSelectedTab != null && InstancesTabControl.Items.Contains(_lastSelectedTab))
                    ? _lastSelectedTab
                    : InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();

                if (selectedTab != null)
                {
                    _ignoreSelectionChange = true;
                    InstancesTabControl.SelectedItem = selectedTab;
                    _ignoreSelectionChange = false;
                }
            }

            if (!_isGridViewEnabled && selectedTab == null) return;

            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                // Let Windows handle visibility for owned windows when the host is hidden.
                // Explicitly hiding the embedded Flash HWND can leave the child renderer black
                // after restoration, so do not send SW_HIDE to child windows here.
                return;
            }

            PixelPoint screenTL;
            PixelPoint screenBR;
            try
            {
                screenTL = HostContainer.PointToScreen(new Point(0, 0));
                screenBR = HostContainer.PointToScreen(new Point(HostContainer.Bounds.Width, HostContainer.Bounds.Height));
            }
            catch
            {
                return;
            }

            int x = screenTL.X;
            int y = screenTL.Y;
            int w = Math.Max(screenBR.X - screenTL.X, 1);
            int h = Math.Max(screenBR.Y - screenTL.Y, 1);

            if (w <= 1 || h <= 1) return;

            _childOffsetX = x - Position.X;
            _childOffsetY = y - Position.Y;
            _childWidth = w;
            _childHeight = h;
            _lastFastX = Position.X;
            _lastFastY = Position.Y;

            // Do not include SWP_SHOWWINDOW in queued position requests. A stale async request
            // can otherwise re-show a child after the host has been hidden from the tray.
            uint baseFlags = SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_NOZORDER;

            var activeWindows = InstancesTabControl.Items.OfType<TabItem>()
                .Where(t => _tabs.ContainsKey(t))
                .Select(t => _tabs[t])
                .Where(v => v.ChildHwnd != IntPtr.Zero)
                .ToList();

            bool isLoading = false;
            if (_isGridViewEnabled)
            {
                isLoading = _tabs.Values.Any(v => v.ChildHwnd == IntPtr.Zero);
            }
            else
            {
                if (selectedTab != null && _tabs.TryGetValue(selectedTab, out TabInfo? activeInfo) && activeInfo.ChildHwnd == IntPtr.Zero)
                    isLoading = true;
            }
            if (LoadingIndicator != null) LoadingIndicator.IsVisible = isLoading;

            if (activeWindows.Count == 0) return;

            if (_isGridViewEnabled)
            {
                int n = activeWindows.Count;
                int cols = (int)Math.Ceiling(Math.Sqrt(n));
                int rows = (int)Math.Ceiling((double)n / cols);
                int cellW = w / cols;
                int cellH = h / rows;

                _gridTiles.Clear();
                for (int i = 0; i < n; i++)
                {
                    int r = i / cols;
                    int c = i % cols;
                    _gridTiles.Add(new GridTile
                    {
                        Hwnd = activeWindows[i].ChildHwnd,
                        OffsetX = _childOffsetX + (c * cellW),
                        OffsetY = _childOffsetY + (r * cellH),
                        Width = cellW,
                        Height = cellH
                    });
                    SetWindowPos(activeWindows[i].ChildHwnd, IntPtr.Zero, x + c * cellW, y + r * cellH, cellW, cellH, baseFlags);
                    ShowWindow(activeWindows[i].ChildHwnd, SW_SHOW);
                    if (activeWindows[i].IsThrottled)
                    {
                        activeWindows[i].IsThrottled = false;
                        PostMessage(activeWindows[i].ChildHwnd, WM_SKUA_THROTTLE, new IntPtr(0), IntPtr.Zero);
                    }
                }
                _lastGridViewState = true;
                _lastActiveHwnd = IntPtr.Zero;
                if (selectedTab != null && _tabs.TryGetValue(selectedTab, out TabInfo? selInfo) && selInfo.ChildHwnd != IntPtr.Zero)
                    HotKeys.ActiveChildHwnd = selInfo.ChildHwnd;
                else if (activeWindows.Count > 0)
                    HotKeys.ActiveChildHwnd = activeWindows[0].ChildHwnd;
            }
            else
            {
                TabInfo? activeInfo = null;
                IntPtr activeChildHwnd = IntPtr.Zero;
                if (selectedTab != null && _tabs.TryGetValue(selectedTab, out activeInfo) && activeInfo.ChildHwnd != IntPtr.Zero)
                {
                    activeChildHwnd = activeInfo.ChildHwnd;
                }
                if (activeChildHwnd == IntPtr.Zero && _tabs.Count > 0)
                {
                    activeChildHwnd = _tabs.Values.FirstOrDefault(t => t.ChildHwnd != IntPtr.Zero)?.ChildHwnd ?? IntPtr.Zero;
                }
                HotKeys.ActiveChildHwnd = activeChildHwnd;

                if (x == _lastX && y == _lastY && w == _lastW && h == _lastH &&
                    activeChildHwnd == _lastActiveHwnd && _lastGridViewState == _isGridViewEnabled)
                {
                    return;
                }

                _lastX = x;
                _lastY = y;
                _lastW = w;
                _lastH = h;
                _lastActiveHwnd = activeChildHwnd;
                _lastGridViewState = false;

                if (activeChildHwnd != IntPtr.Zero)
                {
                    SetWindowPos(activeChildHwnd, IntPtr.Zero, x, y, w, h, baseFlags);
                    ShowWindow(activeChildHwnd, SW_SHOW);
                    if (activeInfo != null && activeInfo.IsThrottled)
                    {
                        activeInfo.IsThrottled = false;
                        PostMessage(activeChildHwnd, WM_SKUA_THROTTLE, new IntPtr(0), IntPtr.Zero);
                    }
                }

                foreach (var kvp in _tabs)
                {
                    if (kvp.Key == selectedTab) continue;

                    TabInfo info = kvp.Value;
                    if (info.ChildHwnd == IntPtr.Zero) continue;

                    if (!info.IsThrottled)
                    {
                        info.IsThrottled = true;
                        PostMessage(info.ChildHwnd, WM_SKUA_THROTTLE, new IntPtr(1), IntPtr.Zero);
                    }

                    // Keep inactive tabs hidden instead of moving them off-screen with an async
                    // SWP_SHOWWINDOW request that could race with tray hide/show.
                    ShowWindow(info.ChildHwnd, SW_HIDE);
                }
            }
        }
        finally
        {
            _isRepositioning = false;
            if (_needsReposition)
            {
                _needsReposition = false;
                Dispatcher.UIThread.Post(DoReposition, DispatcherPriority.Render);
            }

            // ShowWindow(SW_SHOW) above may lift a game window over an open host dialog.
            if (IsHostModalOpen)
                KeepGameWindowsBelowHostWindows();
        }
    }
}
