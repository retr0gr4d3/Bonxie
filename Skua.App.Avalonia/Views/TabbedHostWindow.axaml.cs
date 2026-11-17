using Avalonia;
using Avalonia.Controls;
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
using System.Threading;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Views;

public partial class TabbedHostWindow : Window
{
    #region Tab State

    private class TabInfo
    {
        public Process Process { get; set; } = null!;
        public IntPtr ChildHwnd { get; set; } = IntPtr.Zero;
        public bool IsThrottled { get; set; } = false;
        public bool IsCustomNamed { get; set; } = false;
    }

    private readonly string[] _initialArgs;
    private bool _initialInstanceStarted = false;
    private readonly Dictionary<TabItem, TabInfo> _tabs = new();
    private bool _needsReposition = false;
    private bool _isRepositioning = false;
    private bool _isTogglingGridView = false;
    private DateTime _lastGridViewToggleTime = DateTime.MinValue;
    private readonly TimeSpan _gridViewToggleCooldown = TimeSpan.FromMilliseconds(300);
    private bool _isClosing = false;
    private readonly CancellationTokenSource _pipeCts = new();
    private bool _isGridViewEnabled = false;
    private TabItem? _lastSelectedTab;
    private IntPtr _hostHwnd = IntPtr.Zero;
    private int _lastX = -99999, _lastY = -99999, _lastW = -99999, _lastH = -99999;
    private IntPtr _lastActiveHwnd = IntPtr.Zero;
    private bool _lastGridViewState = false;
    private int _childOffsetX = 0;
    private int _childOffsetY = 0;
    private int _childWidth = 0;
    private int _childHeight = 0;
    private int _lastFastX = -99999;
    private int _lastFastY = -99999;
    private bool _isDraggingWindow = false;

    private struct GridTile
    {
        public IntPtr Hwnd;
        public int OffsetX;
        public int OffsetY;
        public int Width;
        public int Height;
    }

    private readonly List<GridTile> _gridTiles = new();

    private Queue<Action> _spawnQueue = new Queue<Action>();
    private bool _isSpawning = false;

    private void EnqueueSpawn(Action spawnAction)
    {
        _spawnQueue.Enqueue(spawnAction);
        ProcessNextSpawn();
    }

    private void ProcessNextSpawn()
    {
        if (_isSpawning || _spawnQueue.Count == 0 || _isClosing) return;

        _isSpawning = true;
        Action next = _spawnQueue.Dequeue();
        try
        {
            next.Invoke();
        }
        catch (Exception ex)
        {
            _isSpawning = false;
            Trace.WriteLine($"Bonxie child spawn failed: {ex}");
            Dispatcher.UIThread.Post(ProcessNextSpawn, DispatcherPriority.Background);
        }
    }

    private void RecordClientLocation(int pid, ArmyClientLocation location)
    {
        _latestClientLocations[pid] = location;
        string signature = $"{location.Map}|{location.Room}";
        if (_lastRecordedLocationByPid.TryGetValue(pid, out string? previousSignature)
            && previousSignature.Equals(signature, StringComparison.OrdinalIgnoreCase))
            return;

        _lastRecordedLocationByPid[pid] = signature;
        _jumpMapHistory.RemoveAll(item =>
            item.Map.Equals(location.Map, StringComparison.OrdinalIgnoreCase)
            && item.Room.Equals(location.Room, StringComparison.OrdinalIgnoreCase));

        _jumpMapHistory.Insert(0, new ArmyJumpDestination
        {
            Name = location.Username,
            Map = location.Map,
            Room = location.Room,
            Cell = string.IsNullOrWhiteSpace(location.Cell) ? "Enter" : location.Cell,
            Pad = string.IsNullOrWhiteSpace(location.Pad) ? "Spawn" : location.Pad
        });

        if (_jumpMapHistory.Count > 15)
            _jumpMapHistory.RemoveRange(15, _jumpMapHistory.Count - 15);

        SaveArmyJumpSettings();
    }

    #endregion Tab State

    public TabbedHostWindow() : this(Array.Empty<string>())
    {
    }

    public TabbedHostWindow(string rawInitialArgs) : this(ParseRawCommandLine(rawInitialArgs))
    {
    }

    public TabbedHostWindow(string[]? initialArgs)
    {
        _initialArgs = initialArgs ?? Array.Empty<string>();
        InitializeComponent();
        LoadArmyJumpSettings();
        Title = "Bonxie Beta 7";
        MainTitleBar.TitleText = Title;

        try
        {
            IThemeService? themeService = Ioc.Default.GetService<IThemeService>();
            Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetArtworkPath(
                Ioc.Default.GetRequiredService<ISettingsService>().GetShared().TitleBarArtworkPath);

            if (themeService is Skua.Shared.Avalonia.Services.AvaloniaThemeService avaloniaTheme)
            {
                MainTitleBar.UseAccentBackground = avaloniaTheme.UseAccentOnMainTitleBar;
                Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService.IsDarkTheme,
                    avaloniaTheme.UseAccentOnMainTitleBar ? avaloniaTheme.PrimaryColor : (Color?)null,
                    avaloniaTheme.UseAccentOnMainTitleBar ? avaloniaTheme.PrimaryForegroundColor : (Color?)null);
            }
            else
            {
                MainTitleBar.UseAccentBackground = false;
                Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService?.IsDarkTheme);
            }
        }
        catch { }

        var options = Ioc.Default.GetRequiredService<IScriptOption>();
        MiscOptionsMenu.DataContext = options;
        options.PropertyChanged += Options_PropertyChanged;

        Opened += TabbedHostWindow_Opened;
        PositionChanged += (s, e) => OnWindowPositionChanged(e.Point);
        SizeChanged += (s, e) => { ScheduleReposition(); UpdateTabScrollButtons(); };
        ScalingChanged += (s, e) => Dispatcher.UIThread.Post(RepositionWithFreshCache, DispatcherPriority.Loaded);

        PropertyChanged += (s, e) => { if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty) ScheduleReposition(); };
        Activated += (s, e) => { ScheduleReposition(); UpdateTabScrollButtons(); };
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;

        StrongReferenceMessenger.Default.Register<TabbedHostWindow, LoadScriptMessage, int>(this, (int)MessageChannels.ScriptStatus, (r, m) => r.BroadcastScriptAction(m.Path, WM_SKUA_LOAD_SCRIPT));
        StrongReferenceMessenger.Default.Register<TabbedHostWindow, ScriptSchedulerViewModel.ArmySchedulerMessage>(this, (r, m) => r.OnArmySchedulerStart(m));
        StrongReferenceMessenger.Default.Register<TabbedHostWindow, ScriptSchedulerViewModel.ArmySchedulerStopMessage>(this, (r, m) => r.OnArmySchedulerStop(m));

        StartPipeServer();

        TabScrollViewer.PointerWheelChanged += (s, e) =>
        {
            if (e.Delta.Y != 0)
            {
                var newOffset = TabScrollViewer.Offset.X - (e.Delta.Y * 80);
                TabScrollViewer.Offset = new Vector(Math.Max(0, newOffset), 0);
                UpdateTabScrollButtons();
                e.Handled = true;
            }
        };

        TabScrollViewer.PropertyChanged += (s, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty ||
                e.Property == ScrollViewer.ExtentProperty ||
                e.Property == ScrollViewer.ViewportProperty)
            {
                UpdateTabScrollButtons();
            }
        };

        var titleSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        titleSyncTimer.Tick += (s, e) => SyncTabTitles();
        titleSyncTimer.Start();

        GridViewBorder.PointerEntered += GridViewBorder_PointerEntered;
        GridViewBorder.PointerExited += GridViewBorder_PointerExited;
        GlobeBorder.PointerEntered += GlobeBorder_PointerEntered;
        GlobeBorder.PointerExited += GlobeBorder_PointerExited;
    }

    private void SyncTabTitles()
    {
        if (_isClosing || _tabs.Count == 0) return;

        var entries = _tabs.Select(kvp => (Tab: kvp.Key, Pid: kvp.Value?.Process?.Id ?? 0)).ToList();

        Task.Run(() =>
        {
            string dir = TabHostFiles.TabsDirectory;
            if (!Directory.Exists(dir)) return;

            var updates = new List<(TabItem Tab, string Username)>();
            var locations = new List<(int Pid, ArmyClientLocation Location)>();

            foreach (var (tab, pid) in entries)
            {
                if (pid == 0) continue;
                try
                {
                    string file = Path.Combine(dir, $"{pid}.txt");
                    if (File.Exists(file))
                    {
                        string username = File.ReadAllText(file).Trim();
                        if (!string.IsNullOrWhiteSpace(username))
                            updates.Add((tab, username));
                    }

                    string locationFile = Path.Combine(dir, $"{pid}.location.json");
                    if (File.Exists(locationFile))
                    {
                        ArmyClientLocation? location = JsonSerializer.Deserialize<ArmyClientLocation>(File.ReadAllText(locationFile));
                        if (location != null
                            && !string.IsNullOrWhiteSpace(location.Map)
                            && location.UpdatedUtc >= DateTime.UtcNow.AddSeconds(-5))
                            locations.Add((pid, location));
                    }
                }
                catch { }
            }

            if ((updates.Count > 0 || locations.Count > 0) && !_isClosing)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isClosing) return;

                    foreach (var (pid, location) in locations)
                        RecordClientLocation(pid, location);

                    foreach (var (tab, username) in updates)
                    {
                        if (_tabs.TryGetValue(tab, out TabInfo? tInfo) && tInfo.IsCustomNamed)
                            continue;

                        if (tab.Header is StackPanel headerPanel)
                        {
                            var titleBlock = headerPanel.Children.OfType<TextBlock>().FirstOrDefault(tb => tb.Text != "✕");
                            var editBox = headerPanel.Children.OfType<TextBox>().FirstOrDefault();
                            if (titleBlock != null && (titleBlock.Text.StartsWith("User ") || titleBlock.Text.StartsWith("Skua ") || titleBlock.Text != username))
                            {
                                titleBlock.Text = username;
                                if (editBox != null) editBox.Text = username;
                            }
                        }
                    }
                }, DispatcherPriority.Background);
            }
        });
    }

    #region Lifecycle

    private void TabbedHostWindow_Opened(object? sender, EventArgs e)
    {
        _hostHwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_hostHwnd != IntPtr.Zero)
        {
            HotKeys.HostHwnd = _hostHwnd;
            _subclassProc = HostWindowSubclassProc;
            SetWindowSubclass(_hostHwnd, _subclassProc, 1, IntPtr.Zero);
        }

        HotKeys.RegisterSkuaPid((uint)Environment.ProcessId);
        HotKeys.GetChildHwndForPid = (pid) =>
        {
            var tab = _tabs.Values.FirstOrDefault(t => t.Process != null && (uint)t.Process.Id == pid);
            return tab?.ChildHwnd ?? IntPtr.Zero;
        };

        try
        {
            var themeService = Ioc.Default.GetService<IThemeService>();
            if (themeService != null)
            {
                void RefreshNativeTitleBar()
                {
                    if (themeService is Skua.Shared.Avalonia.Services.AvaloniaThemeService currentTheme)
                    {
                        MainTitleBar.UseAccentBackground = currentTheme.UseAccentOnMainTitleBar;
                        Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService.IsDarkTheme,
                            currentTheme.UseAccentOnMainTitleBar ? currentTheme.PrimaryColor : (Color?)null,
                            currentTheme.UseAccentOnMainTitleBar ? currentTheme.PrimaryForegroundColor : (Color?)null);
                    }
                    else
                    {
                        MainTitleBar.UseAccentBackground = false;
                        Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService.IsDarkTheme);
                    }
                }

                // Preset changes and single-scheme edits use separate events. Listen to both so
                // changing a custom primary/foreground color cannot leave the native caption stale.
                themeService.ThemeChanged += _ => Dispatcher.UIThread.Post(RefreshNativeTitleBar, DispatcherPriority.Background);
                themeService.SchemeChanged += (_, _) => Dispatcher.UIThread.Post(RefreshNativeTitleBar, DispatcherPriority.Background);
                if (themeService is Skua.Shared.Avalonia.Services.AvaloniaThemeService avaloniaTheme)
                    Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService.IsDarkTheme,
                            avaloniaTheme.UseAccentOnMainTitleBar ? avaloniaTheme.PrimaryColor : (Color?)null,
                            avaloniaTheme.UseAccentOnMainTitleBar ? avaloniaTheme.PrimaryForegroundColor : (Color?)null);
                else
                    Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, themeService.IsDarkTheme);
            }
        }
        catch { }

        try
        {
            Ioc.Default.GetRequiredService<IHotKeyService>().Reload();
        }
        catch { }

        // Avalonia can raise Opened again after a hidden window is shown. Only launch
        // the startup instance once; subsequent tray restores must reuse existing tabs.
        if (!_initialInstanceStarted)
        {
            _initialInstanceStarted = true;
            AddNewInstance(_initialArgs);
        }
    }

    private IntPtr HostWindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == HotKeys.WM_SKUA_RELOAD_HOTKEYS)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    Ioc.Default.GetRequiredService<IHotKeyService>().Reload();
                }
                catch { }
            });
            return IntPtr.Zero;
        }

        if (uMsg == HotKeys.WM_SKUA_LOAD_SCRIPT_ALL)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var windowService = Ioc.Default.GetRequiredService<IWindowService>();
                    windowService.RegisterManagedWindow("Script Repo", Ioc.Default.GetRequiredService<ScriptRepoViewModel>());
                    windowService.ShowManagedWindow("Script Repo");
                }
                catch { }
            });
            return IntPtr.Zero;
        }

        if (uMsg == 0x001A /* WM_SETTINGCHANGE */ || uMsg == 0x031A /* WM_THEMECHANGED */ || uMsg == 0x0320 /* WM_DWMCOLORIZATIONCOLORCHANGED */)
        {
            IntPtr res = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var themeService = Ioc.Default.GetService<IThemeService>();
                    bool isDark = themeService?.IsDarkTheme ?? true;
                    if (themeService is Skua.Shared.Avalonia.Services.AvaloniaThemeService avaloniaTheme)
                        Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, isDark,
                            avaloniaTheme.UseAccentOnMainTitleBar ? avaloniaTheme.PrimaryColor : (Color?)null,
                            avaloniaTheme.UseAccentOnMainTitleBar ? avaloniaTheme.PrimaryForegroundColor : (Color?)null);
                    else
                        Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this, isDark);
                }
                catch { }
            });
            return res;
        }

        if (uMsg == WM_NCACTIVATE)
        {
            if (wParam == IntPtr.Zero)
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(fg, out uint fgPid);
                    uint currentPid = (uint)Environment.ProcessId;
                    if (fgPid == currentPid || _tabs.Values.Any(t => t.Process != null && !t.Process.HasExited && (uint)t.Process.Id == fgPid))
                    {
                        return DefSubclassProc(hWnd, uMsg, (IntPtr)1, lParam);
                    }
                }
            }
        }

        if (uMsg == WM_SKUA_CHILD_READY)
        {
            IntPtr childHwnd = wParam;
            int pid = lParam.ToInt32();

            Dispatcher.UIThread.Post(() =>
            {
                if (_isClosing || childHwnd == IntPtr.Zero) return;

                TabInfo? targetInfo = _tabs.Values.FirstOrDefault(t => t.Process != null && t.Process.Id == pid);
                if (targetInfo != null)
                {
                    AttachChildWindow(targetInfo, childHwnd);
                }
            });
            return IntPtr.Zero;
        }

        // A child moved onto a monitor with another scale may have resized itself. Its new size is invisible to the
        // position cache, so place it again.
        if (uMsg == WM_SKUA_CHILD_DPICHANGED)
        {
            Dispatcher.UIThread.Post(RepositionWithFreshCache, DispatcherPriority.Loaded);
            return IntPtr.Zero;
        }

        if (uMsg == WM_ENTERSIZEMOVE)
        {
            IntPtr res = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            _isDraggingWindow = false;
            _lastX = -99999;
            _lastY = -99999;
            DoReposition();
            _isDraggingWindow = true;
            return res;
        }

        if (uMsg == WM_EXITSIZEMOVE || uMsg == 0x0215 /* WM_CAPTURECHANGED */)
        {
            IntPtr res = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            _isDraggingWindow = false;
            _lastX = -99999;
            _lastY = -99999;
            DoReposition();
            return res;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void AttachChildWindow(TabInfo info, IntPtr childHwnd)
    {
        if (info.ChildHwnd != IntPtr.Zero) return;

        info.ChildHwnd = childHwnd;
        if (info.Process != null)
            HotKeys.RegisterSkuaPid((uint)info.Process.Id);
        if (HotKeys.ActiveChildHwnd == IntPtr.Zero)
            HotKeys.ActiveChildHwnd = childHwnd;

        int exStyle = GetWindowLong(childHwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(childHwnd, GWL_EXSTYLE, exStyle);
        SetWindowLongAny(childHwnd, GWL_HWNDPARENT, _hostHwnd);

        if (_isGridViewEnabled)
        {
            PostMessage(childHwnd, WM_SKUA_GRIDVIEW, new IntPtr(1), IntPtr.Zero);
        }

        DoReposition();

        _isSpawning = false;
        ProcessNextSpawn();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _isClosing = true;
        App.DisposeTrayIcon();
        try { _pipeCts.Cancel(); } catch { }

        var tabsToClose = _tabs.Values.ToList();
        _tabs.Clear();

        foreach (var info in tabsToClose)
        {
            try
            {
                if (info.ChildHwnd != IntPtr.Zero)
                    ShowWindow(info.ChildHwnd, SW_HIDE);
                if (!info.Process.HasExited)
                {
                    info.Process.Kill();
                    info.Process.WaitForExit(500);
                }
                try { File.Delete(TabHostFiles.UsernameOf(info.Process.Id)); } catch { }
                try { File.Delete(TabHostFiles.LocationOf(info.Process.Id)); } catch { }
                _latestClientLocations.Remove(info.Process.Id);
                _lastRecordedLocationByPid.Remove(info.Process.Id);
            }
            catch { }
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        App.DisposeTrayIcon();
        try
        {
            App.AppMutex?.ReleaseMutex();
            App.AppMutex?.Dispose();
            App.AppMutex = null;
        }
        catch { }

        Task.Run(async () =>
        {
            await Task.Delay(300);
            Environment.Exit(0);
        });
    }

    #endregion Lifecycle

    private static IntPtr FindWindowByProcessId(int processId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if ((int)pid == processId)
            {
                System.Text.StringBuilder sb = new(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                // Match WPF exactly: just check if it's visible and belongs to the process.
                if (IsWindowVisible(hWnd))
                {
                    result = hWnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
