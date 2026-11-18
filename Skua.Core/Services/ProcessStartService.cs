using Skua.Core.Interfaces;
using Skua.Core.Models;
using System.Diagnostics;

namespace Skua.Core.Services;

public class ProcessStartService : IProcessService
{
    public ProcessStartService(ISettingsService settingsService, IDialogService dialogService)
    {
        _settingsService = settingsService;
        _dialogService = dialogService;
    }

    private readonly string _vscPath = Path.Combine(AppContext.BaseDirectory, "VSCode", "code");
    private readonly string _scriptsPath = ClientFileSources.SkuaScriptsDIR;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;

    public void OpenLink(string link)
    {
        if (OperatingSystem.IsLinux())
        {
            if (string.IsNullOrWhiteSpace(link)
                || !Uri.TryCreate(link, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return;

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    ProcessStartInfo psi = new("xdg-open")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add(uri.AbsoluteUri);
                    if (Process.Start(psi) is { } proc && proc.WaitForExit(1500) && proc.ExitCode != 0)
                        _dialogService.ShowMessageBox($"Could not open link (xdg-open exited with code {proc.ExitCode}).", "Open Link Error");
                }
                catch (Exception ex)
                {
                    _dialogService.ShowMessageBox($"Could not open link:\r\n{ex.Message}", "Open Link Error");
                }
            });
            return;
        }

        ProcessStartInfo ps = new(link)
        {
            UseShellExecute = true,
            Verb = "open"
        };
        Process.Start(ps);
    }

    public void OpenVSC()
    {
        if (OperatingSystem.IsLinux())
        {
            _ = System.Threading.Tasks.Task.Run(OpenVSCLinux);
            return;
        }

        if (_settingsService.Get("UseLocalVSC", false))
        {
            Process.Start(_vscPath, _scriptsPath);
            return;
        }
        try
        {
            VSCode(string.Empty);
        }
        catch
        {
            _dialogService.ShowMessageBox("VSCode was not found. If you have VSCode, reinstall it and be sure to check it to install in you PATH folder.", "VSCode not found");
        }
    }

    public void OpenVSC(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            _ = System.Threading.Tasks.Task.Run(() => OpenVSCLinux(path));
            return;
        }

        if (_settingsService.Get("UseLocalVSC", false))
        {
            Process.Start(_vscPath, new[] { _scriptsPath, path, "--reuse-window" });
            return;
        }
        try
        {
            VSCode(path);
        }
        catch
        {
            Process.Start("notepad", path);
        }
    }

    private void VSCode(string path)
    {
        ProcessStartInfo psi = new("code", $"{_scriptsPath} {path}")
        {
            UseShellExecute = true,
            CreateNoWindow = true
        };

        Process.Start(psi);
    }

    private void OpenVSCLinux()
    {
        try
        {
            if (!Directory.Exists(_scriptsPath))
                Directory.CreateDirectory(_scriptsPath);
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessageBox($"Could not create scripts directory:\r\n{ex.Message}", "Directory Error");
            return;
        }

        if (_settingsService.Get("UseLocalVSC", false) && File.Exists(_vscPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_vscPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { _scriptsPath }
                });
                return;
            }
            catch { }
        }

        var vsc = FindVSCode();
        if (vsc == null)
        {
            _dialogService.ShowMessageBox(
                "Visual Studio Code is not installed on this system or 'code' was not found in your PATH.",
                "VSCode Not Found");
            return;
        }

        try
        {
            ProcessStartInfo psi = new(vsc.Value.Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (vsc.Value.PrefixArgs != null)
            {
                foreach (string arg in vsc.Value.PrefixArgs)
                    psi.ArgumentList.Add(arg);
            }
            psi.ArgumentList.Add(_scriptsPath);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessageBox($"Failed to launch VSCode:\r\n{ex.Message}", "VSCode Launch Error");
        }
    }

    private void OpenVSCLinux(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (_settingsService.Get("UseLocalVSC", false) && File.Exists(_vscPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_vscPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { _scriptsPath, path, "--reuse-window" }
                });
                return;
            }
            catch { }
        }

        var vsc = FindVSCode();
        if (vsc != null)
        {
            try
            {
                ProcessStartInfo psi = new(vsc.Value.Exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (vsc.Value.PrefixArgs != null)
                {
                    foreach (string arg in vsc.Value.PrefixArgs)
                        psi.ArgumentList.Add(arg);
                }
                psi.ArgumentList.Add("--reuse-window");
                psi.ArgumentList.Add(path);
                Process.Start(psi);
                return;
            }
            catch { }
        }

        OpenFileInTextEditor(path);
    }

    private void OpenFileInTextEditor(string path)
    {
        try
        {
            ProcessStartInfo psi = new("xdg-open")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(path);
            if (Process.Start(psi) is { } proc && (!proc.WaitForExit(1500) || proc.ExitCode == 0))
                return;
        }
        catch { }

        string[] editors = { "gedit", "gnome-text-editor", "kate", "kwrite", "mousepad", "xed", "pluma", "geany", "leafpad" };
        foreach (string editor in editors)
        {
            if (FindInPath(editor) is { } exe)
            {
                try
                {
                    ProcessStartInfo psi = new(exe)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                    return;
                }
                catch { }
            }
        }

        _dialogService.ShowMessageBox(
            $"Neither Visual Studio Code nor a system text editor could be opened for:\r\n{path}",
            "Editor Not Found");
    }

    private static (string Exe, string[]? PrefixArgs)? FindVSCode()
    {
        string[] cmds = { "code", "codium", "code-oss" };
        foreach (string cmd in cmds)
        {
            if (FindInPath(cmd) is { } found)
                return (found, null);
        }

        string[] directPaths =
        {
            "/usr/bin/code",
            "/usr/local/bin/code",
            "/snap/bin/code",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin/code"),
            "/var/lib/flatpak/exports/bin/com.visualstudio.code",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/flatpak/exports/bin/com.visualstudio.code")
        };

        foreach (string p in directPaths)
        {
            if (File.Exists(p))
                return (p, null);
        }

        if (FindInPath("flatpak") != null)
        {
            try
            {
                using Process p = Process.Start(new ProcessStartInfo
                {
                    FileName = "flatpak",
                    ArgumentList = { "info", "com.visualstudio.code" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                })!;
                if (!p.WaitForExit(1500))
                {
                    try { p.Kill(); } catch { }
                }
                else if (p.ExitCode == 0)
                {
                    return ("flatpak", new[] { "run", "com.visualstudio.code" });
                }
            }
            catch { }
        }

        return null;
    }

    private static string? FindInPath(string binaryName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (string dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string full = Path.Combine(dir.Trim(), binaryName);
                if (File.Exists(full))
                    return full;
            }
            catch { }
        }
        return null;
    }
}