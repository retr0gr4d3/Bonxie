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

// Creating a tab with its header and client process, and closing it.
public partial class TabbedHostWindow
{
    #region Tab Management

    private void CloseTab(TabItem tab)
    {
        if (!_tabs.ContainsKey(tab) && !InstancesTabControl.Items.Contains(tab))
            return;

        if (_tabs.TryGetValue(tab, out TabInfo? info))
        {
            CompleteTabLaunch(info, false);
            _tabs.Remove(tab);
            Task.Run(() =>
            {
                try
                {
                    if (info.ChildHwnd != IntPtr.Zero)
                        ShowWindow(info.ChildHwnd, SW_HIDE);
                    if (info.Process != null && !info.Process.HasExited)
                        info.Process.Kill();
                    if (info.Process != null)
                    {
                        try { File.Delete(TabHostFiles.UsernameOf(info.Process.Id)); } catch { }
                        try { File.Delete(TabHostFiles.LocationOf(info.Process.Id)); } catch { }
                        try { File.Delete(Path.Combine(Path.GetTempPath(), "BonxieTabs", $"{info.Process.Id}.remote.txt")); } catch { }
                    }
                }
                catch { }
            });
        }
        InstancesTabControl.Items.Remove(tab);
        UpdateTabScrollButtons();

        if (_tabs.Count == 0 || !InstancesTabControl.Items.OfType<TabItem>().Any())
        {
            _lastSelectedTab = null;
            _lastActiveHwnd = IntPtr.Zero;
            _isGridViewEnabled = false;

            // Army Queue passes this flag so its monitored host process exits after the
            // final account finishes. Without it, keep the normal reusable tab host open.
            if (_initialArgs.Contains("--exit-on-script-stop", StringComparer.OrdinalIgnoreCase))
            {
                Close();
                return;
            }

            // Keep the normal host alive when the final account tab closes so the user
            // can open another tab without restarting Bonxie.
            if (!IsVisible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            ScheduleReposition();
            return;
        }

        if (_isGridViewEnabled)
        {
            if (_tabs.Count <= 1)
            {
                ExitGridView();
                return;
            }

            DoReposition();
            return;
        }

        var remaining = InstancesTabControl.Items.OfType<TabItem>().FirstOrDefault();
        if (remaining != null && InstancesTabControl.SelectedItem == null)
            InstancesTabControl.SelectedItem = remaining;
    }

    public void AddNewInstance(string rawArgs)
    {
        AddNewInstance(ParseRawCommandLine(rawArgs));
    }

    public bool AddNewInstance(string[]? clientArgs = null, Action<bool>? launchCompletion = null)
    {
        clientArgs ??= Array.Empty<string>();

        int availableId = 1;
        var currentTitles = InstancesTabControl.Items.OfType<TabItem>()
            .Select(t => t.Header as StackPanel)
            .Where(p => p != null)
            .Select(p => p!.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Text != "✕")?.Text)
            .Where(t => t != null && (t.StartsWith("User ") || t.StartsWith("Skua ")))
            .Select(t =>
            {
                string numStr = t!.StartsWith("User ") ? t.Substring(5) : t.Substring(5);
                int.TryParse(numStr, out int id);
                return id;
            })
            .ToList();

        while (currentTitles.Contains(availableId))
        {
            availableId++;
        }

        string tabName = "User " + availableId;
        string? accountUsername = null;
        for (int i = 0; i < clientArgs.Length; i++)
        {
            if ((clientArgs[i] == "-u" || clientArgs[i] == "--user") && i + 1 < clientArgs.Length)
            {
                if (!string.IsNullOrWhiteSpace(clientArgs[i + 1]))
                {
                    accountUsername = clientArgs[i + 1].Trim();
                    tabName = accountUsername;
                }
                break;
            }
        }

        FileStream? accountReservation = null;
        if (!string.IsNullOrWhiteSpace(accountUsername))
        {
            if (FindExistingAccountTab(accountUsername) is not null || IsExternalAccountRunning(accountUsername))
            {
                launchCompletion?.Invoke(false);
                return false;
            }

            accountReservation = TryAcquireAccountReservation(accountUsername);
            if (accountReservation is null)
            {
                launchCompletion?.Invoke(false);
                return false;
            }
        }

        #region Tab Header UI

        StackPanel headerPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        MaterialIcon tabIcon = new MaterialIcon
        {
            Kind = MaterialIconKind.ShieldAccountOutline,
            Width = 13,
            Height = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        tabIcon[!MaterialIcon.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaAccentBrush");

        TextBox editTitle = new TextBox
        {
            Text = tabName,
            IsVisible = false,
            MinWidth = 60,
            MaxWidth = 180,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        editTitle[!TextBox.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaSurfaceAltBrush");
        editTitle[!TextBox.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");
        editTitle[!TextBox.CaretBrushProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");
        editTitle[!TextBox.BorderBrushProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaBorderBrush");

        TextBlock title = new TextBlock
        {
            Text = tabName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            FontWeight = FontWeight.SemiBold,
            FontSize = 12
        };
        title[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaTextBrush");

        Border closeBtn = new Border
        {
            Background = Brushes.Transparent,
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center
        };
        TextBlock closeTxt = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeTxt[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaMutedTextBrush");
        closeBtn.Child = closeTxt;

        headerPanel.Children.Add(tabIcon);
        headerPanel.Children.Add(editTitle);
        headerPanel.Children.Add(title);
        headerPanel.Children.Add(closeBtn);

        closeBtn.PointerEntered += (s, ev) => { closeTxt.Foreground = Brushes.White; closeBtn.Background = new SolidColorBrush(Color.Parse("#EF4444")); };
        closeBtn.PointerExited += (s, ev) => { closeTxt[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaMutedTextBrush"); closeBtn.Background = Brushes.Transparent; };

        TabInfo info = new()
        {
            Username = accountUsername,
            LaunchCompletion = launchCompletion,
            AccountReservation = accountReservation
        };

        Action closeEditMode = () =>
        {
            if (!editTitle.IsVisible) return;
            string newName = string.IsNullOrWhiteSpace(editTitle.Text) ? title.Text : editTitle.Text;
            if (title.Text != newName)
            {
                title.Text = newName;
                info.IsCustomNamed = true;
            }
            title.IsVisible = true;
            editTitle.IsVisible = false;
        };

        title.DoubleTapped += (s, ev) =>
        {
            editTitle.Text = title.Text;
            title.IsVisible = false;
            editTitle.IsVisible = true;

            Dispatcher.UIThread.Post(() =>
            {
                editTitle.Focus();
                editTitle.SelectAll();
            }, DispatcherPriority.Input);

            ev.Handled = true;
        };

        editTitle.LostFocus += (s, ev) => closeEditMode();

        editTitle.KeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                closeEditMode();
                ev.Handled = true;
            }
            else if (ev.Key == Key.Escape)
            {
                editTitle.Text = title.Text; // revert
                closeEditMode();
                ev.Handled = true;
            }
        };

        #endregion Tab Header UI

        TabItem newTab = new TabItem { Header = headerPanel };
        DragDrop.SetAllowDrop(newTab, true);

        InstancesTabControl.Items.Add(newTab);
        if (InstancesTabControl.SelectedItem == null)
        {
            InstancesTabControl.SelectedItem = newTab;
        }
        Dispatcher.UIThread.Post(() =>
        {
            TabScrollViewer.Offset = new Vector(Math.Max(0, TabScrollViewer.Extent.Width - TabScrollViewer.Viewport.Width), 0);
            UpdateTabScrollButtons();
        }, DispatcherPriority.Background);

        _tabs[newTab] = info;

        EnqueueSpawn(() =>
        {
            if (_isClosing || !_tabs.ContainsKey(newTab))
            {
                info.AccountReservation?.Dispose();
                info.AccountReservation = null;
                CompleteTabLaunch(info, false);
                _isSpawning = false;
                ProcessNextSpawn();
                return;
            }

            Process p = new Process();
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || exePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                p.StartInfo.FileName = exePath;
                p.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Bonxie.dll"));
            }
            else
            {
                p.StartInfo.FileName = exePath;
            }
            p.StartInfo.ArgumentList.Add("--embed");
            p.StartInfo.ArgumentList.Add(_hostHwnd.ToInt64().ToString());
            p.StartInfo.ArgumentList.Add("--host-pid");
            p.StartInfo.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString());
            if (!string.IsNullOrWhiteSpace(App.HostInstanceId))
            {
                p.StartInfo.ArgumentList.Add("--host-instance");
                p.StartInfo.ArgumentList.Add(App.HostInstanceId);
            }

            // The password and token go to the tab's process in its environment, not on its command line.
            foreach (string arg in Skua.Core.Utils.StartupSecrets.MoveToEnvironment(clientArgs, p.StartInfo.Environment))
            {
                if (!string.IsNullOrEmpty(arg))
                    p.StartInfo.ArgumentList.Add(arg);
            }

            p.EnableRaisingEvents = true;
            p.Exited += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    CompleteTabLaunch(info, false);
                    info.AccountReservation?.Dispose();
                    info.AccountReservation = null;
                    if (!_isClosing)
                        CloseTab(newTab);
                    UpdateQuickLaunchStatuses();
                });
            };

            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                p.Dispose();
                CompleteTabLaunch(info, false);
                info.AccountReservation?.Dispose();
                info.AccountReservation = null;
                _isSpawning = false;
                Trace.WriteLine($"Bonxie failed to start child process for tab '{tabName}': {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_tabs.ContainsKey(newTab))
                        CloseTab(newTab);
                    ProcessNextSpawn();
                }, DispatcherPriority.Background);
                return;
            }

            info.Process = p;

            // A client that never reports a logged-in identity must not hold the account
            // reservation forever. Terminate the stalled child so a retry can acquire it.
            if (!string.IsNullOrWhiteSpace(accountUsername))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(3));
                    if (p.HasExited || !string.IsNullOrWhiteSpace(GetProcessLoggedInUsername(info)))
                        return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (p.HasExited || !string.IsNullOrWhiteSpace(GetProcessLoggedInUsername(info)))
                            return;

                        CompleteTabLaunch(info, false);
                        try { p.Kill(entireProcessTree: true); } catch { }
                    });
                });
            }

            try
            {
                EnsureChildJobObject();
                if (_childJobHandle != IntPtr.Zero)
                    AssignProcessToJobObject(_childJobHandle, p.Handle);
            }
            catch { }
            HotKeys.RegisterSkuaPid((uint)p.Id);

            int pid = p.Id;
            Task.Run(async () =>
            {
                bool childWindowFound = false;

                for (int attempt = 0; attempt < 120 && !p.HasExited && info.ChildHwnd == IntPtr.Zero && !_isClosing; attempt++)
                {
                    await Task.Delay(50);

                    if (info.ChildHwnd != IntPtr.Zero)
                    {
                        childWindowFound = true;
                        break;
                    }

                    IntPtr childHwnd = FindWindowByProcessId(pid);
                    if (childHwnd != IntPtr.Zero)
                    {
                        childWindowFound = true;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!_isClosing && info.ChildHwnd == IntPtr.Zero)
                                AttachChildWindow(info, childHwnd);
                        });
                        break;
                    }
                }

                // AttachChildWindow advances the spawn queue itself. Do not advance it again
                // when a window was found but its UI-thread attachment has not run yet.
                if (!childWindowFound && info.ChildHwnd == IntPtr.Zero)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (info.ChildHwnd == IntPtr.Zero)
                        {
                            _isSpawning = false;
                            ProcessNextSpawn();
                        }
                    });
                }
            });
        });

        #region Tab Interactions

        closeBtn.PointerPressed += (s, ev) =>
        {
            CloseTab(newTab);
            ev.Handled = true;
        };

        headerPanel.PointerReleased += (s, ev) =>
        {
            if (ev.InitialPressMouseButton == MouseButton.Middle)
                CloseTab(newTab);
        };

        ContextMenu ctx = new ContextMenu();
        MenuItem closeItem = new MenuItem { Header = "Close Tab" };
        closeItem.Click += (s, ev) => CloseTab(newTab);
        MenuItem closeOthersItem = new MenuItem { Header = "Close Other Tabs" };
        closeOthersItem.Click += (s, ev) =>
        {
            var toRemove = InstancesTabControl.Items.OfType<TabItem>().Where(t => t != newTab && t.Header is StackPanel).ToList();
            foreach (var tab in toRemove) CloseTab(tab);
        };
        MenuItem closeRightItem = new MenuItem { Header = "Close Tabs to the Right" };
        closeRightItem.Click += (s, ev) =>
        {
            int idx = InstancesTabControl.Items.IndexOf(newTab);
            var toRemove = InstancesTabControl.Items.OfType<TabItem>().Where((t, i) => i > idx && t.Header is StackPanel).ToList();
            foreach (var tab in toRemove) CloseTab(tab);
        };
        ctx.Items.Add(closeItem);
        ctx.Items.Add(closeOthersItem);
        ctx.Items.Add(closeRightItem);
        ctx.Items.Add(new Separator());

        MenuItem addToGroupItem = new MenuItem
        {
            Header = "Add to Group",
            Icon = new MaterialIcon { Kind = MaterialIconKind.AccountMultiplePlus, Width = 14, Height = 14 }
        };
        ctx.Items.Add(addToGroupItem);

        void UpdateGroupSubMenu()
        {
            PopulateAddToGroupMenu(addToGroupItem, newTab, info, title.Text);
        }

        UpdateGroupSubMenu();
        headerPanel.ContextRequested += (s, ev) => UpdateGroupSubMenu();
        newTab.ContextRequested += (s, ev) => UpdateGroupSubMenu();

        headerPanel.ContextMenu = ctx;
        newTab.ContextMenu = ctx;

        Point startPoint = new Point();
        PointerPressedEventArgs? dragStartEvent = null;
        headerPanel.PointerPressed += (s, ev) =>
        {
            var prop = ev.GetCurrentPoint(headerPanel).Properties;
            if (prop.IsRightButtonPressed)
            {
                InstancesTabControl.SelectedItem = newTab;
                return;
            }

            if (!prop.IsLeftButtonPressed)
                return;

            startPoint = ev.GetPosition(null);
            dragStartEvent = ev;
            InstancesTabControl.SelectedItem = newTab;

            if (_isGridViewEnabled)
            {
                _isGridViewEnabled = false;
                UpdateGridViewBorderColor();
                foreach (var tInfo in _tabs.Values)
                {
                    if (tInfo.ChildHwnd != IntPtr.Zero)
                    {
                        PostMessage(tInfo.ChildHwnd, WM_SKUA_GRIDVIEW, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                DoReposition();
            }
            ev.Handled = true;
        };

        newTab.PointerPressed += (s, ev) =>
        {
            if (ev.GetCurrentPoint(newTab).Properties.IsRightButtonPressed)
            {
                InstancesTabControl.SelectedItem = newTab;
            }
        };

        headerPanel.PointerReleased += (s, ev) => dragStartEvent = null;

        headerPanel.PointerMoved += async (s, ev) =>
        {
            if (editTitle.IsVisible) return;

            if (ev.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            {
                Point mousePos = ev.GetPosition(null);
                Vector diff = startPoint - mousePos;
                if ((Math.Abs(diff.X) > 4 || Math.Abs(diff.Y) > 4) && dragStartEvent is { } dragStart)
                {
                    dragStartEvent = null;
                    DataTransfer dragData = new();
                    dragData.Add(DataTransferItem.Create(TabItemDataFormat, newTab));
                    await DragDrop.DoDragDropAsync(dragStart, dragData, DragDropEffects.Move);
                }
            }
        };

        newTab.AddHandler(DragDrop.DropEvent, (s, ev) =>
        {
            if (ev.DataTransfer.TryGetValue(TabItemDataFormat) is TabItem droppedTab && droppedTab != newTab)
            {
                int targetIndex = InstancesTabControl.Items.IndexOf(newTab);
                InstancesTabControl.Items.Remove(droppedTab);
                InstancesTabControl.Items.Insert(targetIndex, droppedTab);
                InstancesTabControl.SelectedItem = droppedTab;
            }
        });

        #endregion Tab Interactions
        return true;
    }

    #region Remote Control Gateway Helpers

    public int GetActiveChildPid()
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return Dispatcher.UIThread.Invoke(() => GetActiveChildPid());
            }

            string dir = TabHostFiles.TabsDirectory;

            bool IsTabAuthenticated(int pid)
            {
                try
                {
                    string userFile = Path.Combine(dir, $"{pid}.txt");
                    if (File.Exists(userFile))
                    {
                        string u = File.ReadAllText(userFile).Trim();
                        if (!string.IsNullOrWhiteSpace(u) && !u.StartsWith("User ") && !u.StartsWith("Skua "))
                            return true;
                    }
                    string locFile = Path.Combine(dir, $"{pid}.location.json");
                    if (File.Exists(locFile))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(locFile));
                        if (doc.RootElement.TryGetProperty("LoggedIn", out var li) && li.GetBoolean())
                            return true;
                    }
                }
                catch { }
                return false;
            }

            // 1. If currently selected tab is alive and has an active login session, use it
            if (InstancesTabControl.SelectedItem is TabItem selTab &&
                _tabs.TryGetValue(selTab, out TabInfo? info) &&
                info.Process != null && !info.Process.HasExited &&
                IsTabAuthenticated(info.Process.Id))
            {
                return info.Process.Id;
            }

            // 2. Check all tabs for any that has an active logged-in character
            foreach (var tabInfo in _tabs.Values)
            {
                if (tabInfo.Process != null && !tabInfo.Process.HasExited && IsTabAuthenticated(tabInfo.Process.Id))
                {
                    return tabInfo.Process.Id;
                }
            }

            // 3. Fallback to currently selected tab even if unauthenticated
            if (InstancesTabControl.SelectedItem is TabItem selTab2 &&
                _tabs.TryGetValue(selTab2, out TabInfo? info2) &&
                info2.Process != null && !info2.Process.HasExited)
            {
                return info2.Process.Id;
            }

            // 4. Fallback to any alive tab
            foreach (var tabInfo in _tabs.Values)
            {
                if (tabInfo.Process != null && !tabInfo.Process.HasExited)
                    return tabInfo.Process.Id;
            }
        }
        catch { }

        return 0;
    }

    public void SelectTabByPid(int pid)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var kvp in _tabs)
            {
                if (kvp.Value?.Process != null && kvp.Value.Process.Id == pid)
                {
                    InstancesTabControl.SelectedItem = kvp.Key;
                    break;
                }
            }
        });
    }

    public void FocusTabByPid(int pid)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SelectTabByPid(pid);
            try
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Show();
                Activate();
                Topmost = true;
                Topmost = false;
                Focus();
            }
            catch { }
        });
    }

    public bool CloseTabByPid(int pid)
    {
        return Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var kvp in _tabs)
            {
                if (kvp.Value?.Process != null && kvp.Value.Process.Id == pid)
                {
                    CloseTab(kvp.Key);
                    return true;
                }
            }
            return false;
        });
    }

    public void LaunchAccount(string username, string? password = null, string? server = null, Action<bool>? onCompleted = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (FindExistingAccountTab(username) is TabItem existingTab)
            {
                FocusAccountTab(existingTab);
                onCompleted?.Invoke(true);
                return;
            }

            ISettingsService settings = Ioc.Default.GetRequiredService<ISettingsService>();
            settings.ReloadSettings();
            Dictionary<string, AccountData>? accs = settings.Get<Dictionary<string, AccountData>>("ManagedAccounts");
            string pass = password ?? string.Empty;
            if (string.IsNullOrEmpty(pass) && accs != null && accs.TryGetValue(username, out var acc))
            {
                pass = acc.Password ?? string.Empty;
            }
            if (!string.IsNullOrEmpty(pass) && pass.StartsWith("enc:"))
            {
                try
                {
                    pass = Skua.Core.Utils.CryptoUtils.Decrypt(pass);
                }
                catch { }
            }
            string srv = !string.IsNullOrEmpty(server) ? server : (settings.Get("LastServer", "Twilly") ?? "Twilly");

            string[] args = { "-u", username, "-p", pass, "-s", srv };
            AddNewInstance(args, onCompleted);
        });
    }

    public void LaunchGroup(string groupName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ISettingsService settings = Ioc.Default.GetRequiredService<ISettingsService>();
            settings.ReloadSettings();
            List<GroupData> groups = settings.Get<List<GroupData>>("AccountGroups") ?? new List<GroupData>();
            GroupData? group = groups.FirstOrDefault(item =>
                string.Equals(item.Name, groupName, StringComparison.OrdinalIgnoreCase));
            if (group?.Accounts != null)
                LaunchAccountBatch(group.Accounts);
        });
    }

    public void StartAllScripts()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                    PostMessage(info.ChildHwnd, (uint)WM_SKUA_START_SCRIPT, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    public void StopAllScripts()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                    PostMessage(info.ChildHwnd, (uint)WM_SKUA_STOP_SCRIPT, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    public void HeadlessAll(bool enabled)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Forward option 2 (Headless) with value
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                    PostMessage(info.ChildHwnd, (uint)WM_SKUA_SET_OPTION, new IntPtr(2), new IntPtr(enabled ? 1 : 0));
            }
        });
    }

    public void ReloginAll()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var info in _tabs.Values)
            {
                if (info.ChildHwnd != IntPtr.Zero)
                    PostMessage(info.ChildHwnd, (uint)WM_SKUA_LOGIN, IntPtr.Zero, IntPtr.Zero);
            }
        });
    }

    public void BroadcastArmyScheduler(string playlistJson)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(playlistJson) && playlistJson.Trim().StartsWith("["))
            {
                var normalizedList = new List<ScriptSchedulerViewModel.SavedScriptItem>();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(playlistJson);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            string sPath = string.Empty;
                            string sName = string.Empty;
                            if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                sPath = el.GetString() ?? string.Empty;
                            }
                            else if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (el.TryGetProperty("Path", out var pProp) || el.TryGetProperty("path", out pProp))
                                    sPath = pProp.GetString() ?? string.Empty;
                                if (el.TryGetProperty("Name", out var nProp) || el.TryGetProperty("name", out nProp))
                                    sName = nProp.GetString() ?? string.Empty;
                            }

                            if (!string.IsNullOrWhiteSpace(sPath))
                            {
                                if (string.IsNullOrWhiteSpace(sName))
                                    sName = System.IO.Path.GetFileName(sPath);
                                normalizedList.Add(new ScriptSchedulerViewModel.SavedScriptItem
                                {
                                    Path = sPath,
                                    Name = sName,
                                    Id = Guid.NewGuid()
                                });
                            }
                        }
                    }
                }
                catch { }

                string tempFile = TabHostFiles.Playlist;
                if (normalizedList.Count > 0)
                {
                    System.IO.File.WriteAllText(tempFile, System.Text.Json.JsonSerializer.Serialize(normalizedList));
                }
                else
                {
                    System.IO.File.WriteAllText(tempFile, playlistJson);
                }
            }
            foreach (var info in _tabs.Values)
                if (info.ChildHwnd != IntPtr.Zero)
                    PostMessage(info.ChildHwnd, WM_SKUA_ARMY_SCHEDULER, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    public void StopArmyScheduler()
    {
        try
        {
            foreach (var info in _tabs.Values)
                if (info.ChildHwnd != IntPtr.Zero)
                    PostMessage(info.ChildHwnd, WM_SKUA_ARMY_SCHEDULER_STOP, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    public List<object> GetTabsInfoList()
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return Dispatcher.UIThread.Invoke(() => GetTabsInfoList());
            }

            var list = new List<object>();
            int activePid = GetActiveChildPid();
            string dir = TabHostFiles.TabsDirectory;

            int tabIndex = 1;
            foreach (var kvp in _tabs)
            {
                if (kvp.Value?.Process == null || kvp.Value.Process.HasExited)
                    continue;

                int pid = kvp.Value.Process.Id;
                string tabTitle = GetTabTitle(kvp.Key);
                string username = !string.IsNullOrWhiteSpace(tabTitle) ? tabTitle : $"Skua {tabIndex}";
                try
                {
                    string userFile = Path.Combine(dir, $"{pid}.txt");
                    if (File.Exists(userFile))
                    {
                        string u = File.ReadAllText(userFile).Trim();
                        if (!string.IsNullOrWhiteSpace(u) && !u.StartsWith("Tab ", StringComparison.OrdinalIgnoreCase))
                            username = u;
                    }
                }
                catch { }
                tabIndex++;

                string map = "";
                string cell = "";
                string pad = "";
                string server = "";
                int hp = 0;
                int maxHp = 0;
                int mp = 0;
                int maxMp = 0;
                bool isScriptRunning = false;
                string scriptName = "";

                string locFile = Path.Combine(dir, $"{pid}.location.json");
                if (File.Exists(locFile))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(locFile));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("Map", out var mpProp)) map = mpProp.GetString() ?? "";
                        if (root.TryGetProperty("Cell", out var clProp)) cell = clProp.GetString() ?? "";
                        if (root.TryGetProperty("Pad", out var pdProp)) pad = pdProp.GetString() ?? "";
                        if (root.TryGetProperty("Server", out var svProp)) server = svProp.GetString() ?? "";
                        if (root.TryGetProperty("Hp", out var hProp)) hp = hProp.GetInt32();
                        if (root.TryGetProperty("MaxHp", out var mhProp)) maxHp = mhProp.GetInt32();
                        if (root.TryGetProperty("Mp", out var mProp)) mp = mProp.GetInt32();
                        if (root.TryGetProperty("MaxMp", out var mmProp)) maxMp = mmProp.GetInt32();
                        if (root.TryGetProperty("ScriptRunning", out var srProp)) isScriptRunning = srProp.GetBoolean();
                        if (root.TryGetProperty("ScriptName", out var snProp)) scriptName = snProp.GetString() ?? "";
                    }
                    catch { }
                }

                list.Add(new
                {
                    pid = pid,
                    username = username,
                    is_active = (pid == activePid),
                    map = map,
                    cell = cell,
                    pad = pad,
                    server = server,
                    hp = hp,
                    max_hp = maxHp,
                    mp = mp,
                    max_mp = maxMp,
                    script_running = isScriptRunning,
                    script_name = scriptName
                });
            }
            return list;
        }
        catch
        {
            return new List<object>();
        }
    }

    #endregion Remote Control Gateway Helpers

    #endregion Tab Management
}
