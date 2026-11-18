using Avalonia.Controls;
using Avalonia.Platform;
using System;
#if !IS_WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Skua.App.Avalonia.Flash;
#endif

namespace Skua.App.Avalonia.Controls;

#if IS_WINDOWS
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

/// <summary>
/// Hosts the game player's window. The player (another process) is started as a child of <see cref="WindowHandle"/>,
/// a holder created once and moved between native parents, so the game keeps running when Avalonia recreates the
/// native control (e.g. when the window moves between tabs).
/// </summary>
public class GameHost : NativeControlHost
{
    private readonly Holder _holder = new() { Dock = WinForms.DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(20, 20, 20) };
    private WinForms.Panel? _panel;

    public nint WindowHandle => _holder.Handle;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _panel = new WinForms.Panel { Dock = WinForms.DockStyle.Fill };
        _panel.Controls.Add(_holder);
        return new PlatformHandle(_panel.Handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_panel is not null)
        {
            _panel.Controls.Clear();
            _panel.Dispose();
            _panel = null;
        }
    }

    /// <summary>Keeps the player's window filling the holder, from when it is created on.</summary>
    private sealed class Holder : WinForms.Control
    {
        private const int WM_PARENTNOTIFY = 0x0210;
        private const int WM_CREATE = 0x0001;
        private const uint GW_CHILD = 5;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern nint GetWindow(nint hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        protected override void WndProc(ref WinForms.Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PARENTNOTIFY && (m.WParam.ToInt64() & 0xFFFF) == WM_CREATE)
                FitPlayer();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitPlayer();
        }

        private void FitPlayer()
        {
            nint player = GetWindow(Handle, GW_CHILD);
            if (player != 0)
                SetWindowPos(player, 0, 0, 0, Width, Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }
}
#else
internal static partial class BonxieX11Native
{
    private const string Lib = "bonxie_x11";

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_start")]
    public static partial IntPtr Start(uint childPid, uint parentXid, uint width, uint height);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_stop")]
    public static partial void Stop(IntPtr host);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_resize")]
    public static partial void Resize(IntPtr host, uint width, uint height);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_set_click_callback")]
    public static partial void SetClickCallback(IntPtr host, IntPtr cb, nuint userData);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_focus")]
    public static partial void FocusWindow(uint xid);

    [LibraryImport(Lib, EntryPoint = "bonxie_x11_state")]
    public static partial int GetState(IntPtr host);
}

/// <summary>
/// X11 NativeControlHost on Linux. Creates a child X11 window positioned strictly inside
/// <see cref="GameHost"/>'s layout bounds and reparents Mugetsu's X11 window directly into it
/// via <c>libbonxie_x11.so</c> (no offscreen DMA-BUF copy).
/// </summary>
public class GameHost : NativeControlHost
{
    private FlashUtil? _flash;
    private IntPtr _x11Host;
    private uint _childXid;
    private int _attachedPid;
    private Window? _ownerWindow;
    private GCHandle _selfHandle;
    private DispatcherTimer? _resizeDebounceTimer;
    private DispatcherTimer? _attachMonitorTimer;
    private int _attachRetryCount;
    private int _pendingResizeW;
    private int _pendingResizeH;
    private int _lastSentWidth;
    private int _lastSentHeight;

    public nint WindowHandle => (nint)_childXid;

    public GameHost()
    {
        Focusable = true;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle handle = base.CreateNativeControlCore(parent);
        if (OperatingSystem.IsLinux())
        {
            _childXid = (uint)handle.Handle;
            EnsureX11HostStarted();
            SendResizeIfNeeded();
        }
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopX11Host();
        _childXid = 0;
        base.DestroyNativeControlCore(control);
    }

    public void AttachFlash(FlashUtil flash)
    {
        _flash = flash;
        EnsureX11HostStarted();
        UpdateHostedVisibility();
    }

    private void EnsureX11HostStarted()
    {
        if (!OperatingSystem.IsLinux())
            return;

        int pid = _flash?.PlayerPid ?? 0;
        if (_childXid == 0 || pid <= 0)
            return;

        if (pid == _attachedPid && _x11Host != IntPtr.Zero)
        {
            try
            {
                if (BonxieX11Native.GetState(_x11Host) >= 0)
                    return;
            }
            catch
            {
                return;
            }
        }

        StopX11Host();

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int bw = (int)Math.Round(Bounds.Width * scale);
        int bh = (int)Math.Round(Bounds.Height * scale);
        if (bw <= 2 && _ownerWindow != null)
        {
            bw = (int)Math.Round(_ownerWindow.ClientSize.Width * scale);
            bh = (int)Math.Round(Math.Max(240, (_ownerWindow.ClientSize.Height - 64) * scale));
        }
        uint w = (uint)Math.Max(320, bw > 2 ? bw : _lastSentWidth);
        uint h = (uint)Math.Max(240, bh > 2 ? bh : _lastSentHeight);

        bool startThrew = false;
        try
        {
            _x11Host = BonxieX11Native.Start((uint)pid, _childXid, w, h);
        }
        catch (Exception ex)
        {
            startThrew = true;
            System.Diagnostics.Trace.WriteLine($"Skua.X11Host failed to start: {ex}");
            _x11Host = IntPtr.Zero;
            ShowEmbedErrorDialog();
        }

        if (_x11Host != IntPtr.Zero)
        {
            _attachedPid = pid;
            _lastSentWidth = -1;
            _lastSentHeight = -1;
            if (!_selfHandle.IsAllocated)
                _selfHandle = GCHandle.Alloc(this);
            unsafe
            {
                delegate* unmanaged[Cdecl]<nuint, void> cb = &OnX11Clicked;
                BonxieX11Native.SetClickCallback(_x11Host, (IntPtr)cb, (nuint)(IntPtr)_selfHandle);
            }
            StartAttachMonitor();
        }
        else if (!startThrew)
        {
            System.Diagnostics.Trace.WriteLine($"Skua.X11Host failed to start for pid {pid}: BonxieX11Native.Start returned null.");
            if (_attachRetryCount < 3)
            {
                _attachRetryCount++;
                StartAttachMonitor();
            }
            else
            {
                _attachRetryCount = 0;
                ShowEmbedErrorDialog();
            }
        }
    }

    private static void ShowEmbedErrorDialog()
    {
        CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Skua.Core.Interfaces.IDialogService>()?
            .ShowMessageBox("Failed to embed the Mugetsu game window into Bonxie.", "Game Host Error");
    }

    private void StartAttachMonitor()
    {
        _attachMonitorTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _attachMonitorTimer.Tick -= OnAttachMonitorTick;
        _attachMonitorTimer.Tick += OnAttachMonitorTick;
        _attachMonitorTimer.Start();
    }

    private void OnAttachMonitorTick(object? sender, EventArgs e)
    {
        if (_x11Host == IntPtr.Zero)
        {
            _attachMonitorTimer?.Stop();
            EnsureX11HostStarted();
            return;
        }
        int state;
        try { state = BonxieX11Native.GetState(_x11Host); }
        catch { _attachMonitorTimer?.Stop(); return; }

        if (state == 1)
        {
            _attachRetryCount = 0;
            _attachMonitorTimer?.Stop();
        }
        else if (state < 0)
        {
            _attachMonitorTimer?.Stop();
            if (_attachRetryCount < 3)
            {
                _attachRetryCount++;
                EnsureX11HostStarted();
            }
            else
            {
                _attachRetryCount = 0;
                ShowEmbedErrorDialog();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnX11Clicked(nuint userData)
    {
        if (userData == 0)
            return;
        if (GCHandle.FromIntPtr((IntPtr)userData).Target is GameHost host)
        {
            Dispatcher.UIThread.Post(() => host.Focus());
        }
    }

    private void StopX11Host()
    {
        _attachMonitorTimer?.Stop();
        if (_x11Host != IntPtr.Zero)
        {
            IntPtr h = _x11Host;
            _x11Host = IntPtr.Zero;
            try { BonxieX11Native.SetClickCallback(h, IntPtr.Zero, 0); } catch { }
            try { BonxieX11Native.Stop(h); } catch { }
        }
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
        _attachedPid = 0;
    }

    public void DisposeResources()
    {
        _resizeDebounceTimer?.Stop();
        _flash = null;
        StopX11Host();
    }

    public void FocusHostWindow()
    {
        if (!OperatingSystem.IsLinux())
            return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle is nint h && h != 0)
                BonxieX11Native.FocusWindow((uint)h);
        }
        catch { }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            UpdateHostedVisibility();
    }

    private void OnOwnerWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty)
            UpdateHostedVisibility();
    }

    private bool IsHostEffectivelyVisible =>
        IsVisible && (_ownerWindow is null || (_ownerWindow.IsVisible && _ownerWindow.WindowState != WindowState.Minimized));

    private void UpdateHostedVisibility()
    {
        if (IsHostEffectivelyVisible)
            SendResizeIfNeeded();
        else
            _resizeDebounceTimer?.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DetachOwnerWindow();
        _ownerWindow = TopLevel.GetTopLevel(this) as Window;
        if (_ownerWindow != null)
            _ownerWindow.PropertyChanged += OnOwnerWindowPropertyChanged;
        EnsureX11HostStarted();
        UpdateHostedVisibility();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachOwnerWindow();
        _resizeDebounceTimer?.Stop();
        StopX11Host();
    }

    private void DetachOwnerWindow()
    {
        if (_ownerWindow != null)
        {
            _ownerWindow.PropertyChanged -= OnOwnerWindowPropertyChanged;
            _ownerWindow = null;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SendResizeIfNeeded();
    }

    private void SendResizeIfNeeded()
    {
        if (!OperatingSystem.IsLinux() || _flash is null || !IsHostEffectivelyVisible)
            return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = (int)Math.Round(Bounds.Width * scale);
        int h = (int)Math.Round(Bounds.Height * scale);
        if (w <= 2 || h <= 2)
            return;

        if (_lastSentWidth <= 0 || _lastSentHeight <= 0)
        {
            _lastSentWidth = w;
            _lastSentHeight = h;
            if (_x11Host != IntPtr.Zero)
                BonxieX11Native.Resize(_x11Host, (uint)w, (uint)h);
            return;
        }

        _pendingResizeW = w;
        _pendingResizeH = h;
        _resizeDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _resizeDebounceTimer.Tick -= OnResizeDebounceTick;
        _resizeDebounceTimer.Tick += OnResizeDebounceTick;
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    private void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounceTimer?.Stop();
        if (!OperatingSystem.IsLinux() || _flash is null || !IsHostEffectivelyVisible)
            return;
        int w = _pendingResizeW;
        int h = _pendingResizeH;
        if (w <= 2 || h <= 2 || (w == _lastSentWidth && h == _lastSentHeight))
            return;
        _lastSentWidth = w;
        _lastSentHeight = h;
        if (_x11Host != IntPtr.Zero)
            BonxieX11Native.Resize(_x11Host, (uint)w, (uint)h);
    }
}
#endif
