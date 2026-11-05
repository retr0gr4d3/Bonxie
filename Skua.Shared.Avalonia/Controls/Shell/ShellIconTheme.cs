using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using System;

namespace Skua.Shared.Avalonia.Controls.Shell;

/// <summary>
/// Taskbar, alt-tab and tray icons sit on the OS's own chrome, so they follow the OS theme rather than the
/// app theme: white bird on a dark taskbar, dark bird on a light one.
/// </summary>
public static class ShellIconTheme
{
    private static Application? _app;
    private static WindowIcon? _darkThemeIcon;
    private static WindowIcon? _lightThemeIcon;

    /// <summary>The icon for the current OS theme; null until <see cref="Track"/> runs.</summary>
    public static WindowIcon? Current { get; private set; }

    public static void Track(Application app, string darkThemeIcoUri)
    {
        _app = app;
        _darkThemeIcon = Load(darkThemeIcoUri);
        _lightThemeIcon = Load("avares://Skua.Shared.Avalonia/Assets/SkuaIconLightTheme.ico");
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => window.Icon = Current);
        if (app.PlatformSettings is { } settings)
            settings.ColorValuesChanged += (_, values) => Dispatcher.UIThread.Post(() => Apply(values.ThemeVariant));
        Apply(app.PlatformSettings?.GetColorValues().ThemeVariant ?? PlatformThemeVariant.Dark);
    }

    private static void Apply(PlatformThemeVariant osTheme)
    {
        Current = osTheme == PlatformThemeVariant.Light ? _lightThemeIcon : _darkThemeIcon;
        if (_app?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (Window window in desktop.Windows)
                window.Icon = Current;
        if (_app is not null && TrayIcon.GetIcons(_app) is { } trays)
            foreach (TrayIcon tray in trays)
                tray.Icon = Current;
    }

    private static WindowIcon Load(string avaresUri)
    {
        using var stream = AssetLoader.Open(new Uri(avaresUri));
        return new WindowIcon(stream);
    }
}
