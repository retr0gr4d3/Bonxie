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
        }
    }
}
