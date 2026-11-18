using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Avalonia.ViewModels.Options;
using Skua.App.Avalonia.Services;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Scripts;
using Skua.Core.Utils;
using Skua.Shared.Avalonia.ViewModels.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Skua.App.Avalonia.AppStartup;

internal class Options
{
    private readonly record struct MugetsuSettingsSnapshot(
        int GraphicsIdx,
        string Performance,
        int PerformanceIdx,
        bool Interpolation,
        bool Uncapped,
        string FpsLimit,
        int FpsLimitIdx,
        string MemoryLimit,
        int MemoryLimitIdx,
        bool ShowFps);

    private static readonly object MugetsuSyncGate = new();
    private static readonly Dictionary<string, string> LastAppliedMugetsuSettings = new(StringComparer.Ordinal);
    private static FileSystemWatcher? _mugetsuSettingsWatcher;
    private static DispatcherTimer? _mugetsuSyncTimer;
    private static WeakReference<CBOChoiceOptionItemViewModel>? _graphicsChoiceRef;
    private static WeakReference<CBOChoiceOptionItemViewModel>? _perfChoiceRef;
    private static WeakReference<CommandOptionItemViewModel<bool>>? _interpOptionRef;
    private static WeakReference<CBOChoiceOptionItemViewModel>? _pacingChoiceRef;
    private static WeakReference<CBOChoiceOptionItemViewModel>? _fpsLimitChoiceRef;
    private static WeakReference<CBOChoiceOptionItemViewModel>? _memLimitChoiceRef;
    private static WeakReference<CommandOptionItemViewModel<bool>>? _showFpsOptionRef;
    private static bool _isSyncingFromDisk;

    private static string GetRuffleDir() => OperatingSystem.IsLinux()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ruffle")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ruffle");

    private static T WithMugetsuFileLock<T>(string ruffleDir, Func<T> action)
    {
        Directory.CreateDirectory(ruffleDir);
        string lockPath = Path.Combine(ruffleDir, ".mugetsu-settings.lock");
        const int maxAttempts = 40;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using FileStream lockStream = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return action();
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static string ReadUnlockedMugetsuFile(string ruffleDir, string fileName)
    {
        string path = Path.Combine(ruffleDir, fileName);
        if (!File.Exists(path))
            return string.Empty;
        try { return File.ReadAllText(path).Trim(); } catch { return string.Empty; }
    }

    private static MugetsuSettingsSnapshot ReadMugetsuSettingsSnapshot(string ruffleDir)
    {
        return WithMugetsuFileLock(ruffleDir, () =>
        {
            string graphicsVal = ReadUnlockedMugetsuFile(ruffleDir, "mugetsu-graphics.txt").ToLowerInvariant();
            if (string.IsNullOrEmpty(graphicsVal))
                graphicsVal = "gl";
            int graphicsIdx = graphicsVal switch
            {
                "vulkan" => 1,
                "auto" => 2,
                _ => 0
            };

            string perfVal = ReadUnlockedMugetsuFile(ruffleDir, "mugetsu-performance.txt").ToLowerInvariant();
            int perfIdx = perfVal switch
            {
                "balanced" => 1,
                "fast" => 2,
                _ => 0
            };
            string perfNorm = perfIdx switch
            {
                1 => "balanced",
                2 => "fast",
                _ => "off"
            };

            bool interpOn = File.Exists(Path.Combine(ruffleDir, "mugetsu-interpolation.txt"));
            bool uncappedOn = File.Exists(Path.Combine(ruffleDir, "mugetsu-uncapped.txt"));

            string fpsLimitRaw = ReadUnlockedMugetsuFile(ruffleDir, "mugetsu-fps-limit.txt").ToLowerInvariant();
            int fpsLimitIdx = fpsLimitRaw switch
            {
                "screen" => 1,
                "60" => 2,
                "120" => 3,
                "144" => 4,
                "165" => 5,
                "240" => 6,
                "360" => 7,
                _ => 0
            };
            string fpsLimitNorm = fpsLimitIdx switch
            {
                1 => "screen",
                2 => "60",
                3 => "120",
                4 => "144",
                5 => "165",
                6 => "240",
                7 => "360",
                _ => "off"
            };

            string memLimitRaw = ReadUnlockedMugetsuFile(ruffleDir, "mugetsu-memory-limit.txt");
            int memLimitIdx = memLimitRaw switch
            {
                "1024" => 1,
                "2048" => 2,
                "3072" => 3,
                "4096" => 4,
                _ => 0
            };
            string memLimitNorm = memLimitIdx switch
            {
                1 => "1024",
                2 => "2048",
                3 => "3072",
                4 => "4096",
                _ => "0"
            };

            bool showFpsOn = File.Exists(Path.Combine(ruffleDir, "mugetsu-show-fps.txt"));

            return new MugetsuSettingsSnapshot(
                graphicsIdx,
                perfNorm,
                perfIdx,
                interpOn,
                uncappedOn,
                fpsLimitNorm,
                fpsLimitIdx,
                memLimitNorm,
                memLimitIdx,
                showFpsOn);
        });
    }

    private static bool WriteMugetsuFile(string ruffleDir, string fileName, string? content)
    {
        string? tmpPath = null;
        try
        {
            return WithMugetsuFileLock(ruffleDir, () =>
            {
                string path = Path.Combine(ruffleDir, fileName);
                if (string.IsNullOrEmpty(content))
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return true;
                }
                Directory.CreateDirectory(ruffleDir);
                tmpPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tmpPath, content);
                File.Move(tmpPath, path, overwrite: true);
                return true;
            });
        }
        catch (Exception ex)
        {
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox($"Could not save Mugetsu setting ({fileName}):\r\n{ex.Message}", "Settings Save Error");
            return false;
        }
        finally
        {
            if (tmpPath != null)
            {
                try
                {
                    if (File.Exists(tmpPath))
                        File.Delete(tmpPath);
                }
                catch { }
            }
        }
    }

    private static void SendMugetsuSetting(string name, string value)
    {
        if (Ioc.Default.GetService<IFlashUtil>() is Flash.FlashUtil player)
            player.SendInput(new { input = "mugetsu_setting", name, value });
    }

    internal static void StartMugetsuSettingsSync()
    {
        List<(string Name, string Value)>? initialSettings = null;
        lock (MugetsuSyncGate)
        {
            if (_mugetsuSettingsWatcher != null)
                return;

            string ruffleDir = GetRuffleDir();
            try
            {
                MugetsuSettingsSnapshot initial = ReadMugetsuSettingsSnapshot(ruffleDir);
                initialSettings = RecordAppliedSettingsLocked(initial);
            }
            catch { }

            if (Dispatcher.UIThread.CheckAccess())
                EnsureSyncTimer();
            else
                Dispatcher.UIThread.Post(EnsureSyncTimer);

            try
            {
                Directory.CreateDirectory(ruffleDir);
                FileSystemWatcher watcher = new(ruffleDir, "mugetsu-*.txt")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
                };
                void OnChanged(object? _, FileSystemEventArgs e) => ScheduleDiskSync();
                void OnRenamed(object? _, RenamedEventArgs e) => ScheduleDiskSync();
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.EnableRaisingEvents = true;
                _mugetsuSettingsWatcher = watcher;
            }
            catch { }
        }

        if (initialSettings != null)
        {
            foreach ((string name, string value) in initialSettings)
                SendMugetsuSetting(name, value);
        }
    }

    private static void EnsureSyncTimer()
    {
        if (_mugetsuSyncTimer != null)
            return;
        _mugetsuSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _mugetsuSyncTimer.Tick += (_, _) =>
        {
            _mugetsuSyncTimer.Stop();
            SyncMugetsuSettingsFromDisk(sendLive: true);
        };
    }

    private static void ScheduleDiskSync()
    {
        Dispatcher.UIThread.Post(() =>
        {
            EnsureSyncTimer();
            _mugetsuSyncTimer!.Stop();
            _mugetsuSyncTimer.Start();
        });
    }

    private static List<(string Name, string Value)> RecordAppliedSettingsLocked(MugetsuSettingsSnapshot snapshot)
    {
        (string Name, string Value)[] desired =
        [
            ("performance", snapshot.Performance),
            ("interpolation", snapshot.Interpolation ? "true" : "false"),
            ("uncapped", snapshot.Uncapped ? "true" : "false"),
            ("fps_limit", snapshot.FpsLimit),
            ("memory_limit", snapshot.MemoryLimit),
            ("show_fps", snapshot.ShowFps ? "true" : "false")
        ];
        List<(string Name, string Value)> changed = new();
        foreach ((string name, string val) in desired)
        {
            if (!LastAppliedMugetsuSettings.TryGetValue(name, out string? existing) || existing != val)
            {
                LastAppliedMugetsuSettings[name] = val;
                changed.Add((name, val));
            }
        }
        return changed;
    }

    private static void SyncMugetsuSettingsFromDisk(bool sendLive)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SyncMugetsuSettingsFromDisk(sendLive));
            return;
        }

        MugetsuSettingsSnapshot snapshot;
        try
        {
            snapshot = ReadMugetsuSettingsSnapshot(GetRuffleDir());
        }
        catch
        {
            return;
        }

        _isSyncingFromDisk = true;
        try
        {
            if (_graphicsChoiceRef?.TryGetTarget(out CBOChoiceOptionItemViewModel? graphicsChoice) == true
                && !Equals(graphicsChoice.Value, snapshot.GraphicsIdx))
                graphicsChoice.Value = snapshot.GraphicsIdx;

            if (_perfChoiceRef?.TryGetTarget(out CBOChoiceOptionItemViewModel? perfChoice) == true
                && !Equals(perfChoice.Value, snapshot.PerformanceIdx))
                perfChoice.Value = snapshot.PerformanceIdx;

            if (_interpOptionRef?.TryGetTarget(out CommandOptionItemViewModel<bool>? interpOption) == true
                && !Equals(interpOption.Value, snapshot.Interpolation))
                interpOption.Value = snapshot.Interpolation;

            if (_pacingChoiceRef?.TryGetTarget(out CBOChoiceOptionItemViewModel? pacingChoice) == true
                && !Equals(pacingChoice.Value, snapshot.Uncapped ? 1 : 0))
                pacingChoice.Value = snapshot.Uncapped ? 1 : 0;

            if (_fpsLimitChoiceRef?.TryGetTarget(out CBOChoiceOptionItemViewModel? fpsLimitChoice) == true
                && !Equals(fpsLimitChoice.Value, snapshot.FpsLimitIdx))
                fpsLimitChoice.Value = snapshot.FpsLimitIdx;

            if (_memLimitChoiceRef?.TryGetTarget(out CBOChoiceOptionItemViewModel? memLimitChoice) == true
                && !Equals(memLimitChoice.Value, snapshot.MemoryLimitIdx))
                memLimitChoice.Value = snapshot.MemoryLimitIdx;

            if (_showFpsOptionRef?.TryGetTarget(out CommandOptionItemViewModel<bool>? showFpsOption) == true
                && !Equals(showFpsOption.Value, snapshot.ShowFps))
                showFpsOption.Value = snapshot.ShowFps;
        }
        finally
        {
            _isSyncingFromDisk = false;
        }

        List<(string Name, string Value)> changed;
        lock (MugetsuSyncGate)
        {
            changed = RecordAppliedSettingsLocked(snapshot);
        }

        if (sendLive)
        {
            foreach ((string name, string value) in changed)
                SendMugetsuSetting(name, value);
        }
    }
    internal static GameOptionsViewModel CreateGameOptions(IServiceProvider s)
    {
        IScriptOption scriptOpt = s.GetService<IScriptOption>()!;

        List<DisplayOptionItemViewModelBase> options = new()
        {
            new CommandOptionItemViewModel<bool>("Upgrade Name Colour", "1", CreateBoolCommand(b => Ioc.Default.GetRequiredService<IFlashUtil>().SetGameObject("world.myAvatar.pMC.pname.ti.textColor", b ? 0x8CD5FF : 0xFFFFFF))),
            new CommandOptionItemViewModel<bool>("Staff Name Colour", "1", CreateBoolCommand(b => Ioc.Default.GetRequiredService<IFlashUtil>().SetGameObject("world.myAvatar.pMC.pname.ti.textColor", b ? 0xFECB38 : 0xFFFFFF))),
            new CommandOptionItemViewModel<IRelayCommand>("Reload Map", "4", new RelayCommand(Ioc.Default.GetRequiredService<IScriptMap>().Reload)) { ActionText = "Reload" }
        };

        foreach (PropertyInfo pi in scriptOpt.GetType().GetProperties())
        {
            if (pi.Name is nameof(IScriptOption.OptionDictionary) or nameof(ObservableRecipient.IsActive) or nameof(IScriptOption.GuildColor) or nameof(IScriptOption.NameColor) or "IsIpcMessageProcessing" or nameof(IScriptOption.ReloginTryDelay) or nameof(IScriptOption.UseFunctionBasedSkills) or nameof(IScriptOption.ReloginServer))
                continue;
            if (pi.PropertyType == typeof(bool))
                options.Add(CreateOptionItem<bool>(s, pi.Name, "1", new RelayCommand<bool>(b => pi.SetValue(scriptOpt, b))));
            else if (pi.PropertyType == typeof(string))
                options.Add(CreateOptionItem<string>(s, pi.Name, "2", new RelayCommand<string>(value => pi.SetValue(scriptOpt, value))));
            else if (pi.PropertyType == typeof(int))
            {
                string? suffixText = pi.Name == nameof(IScriptOption.SetFPS) ? "FPS" : null;
                options.Add(CreateOptionItemWithSuffix<int>(s, pi.Name, "3", suffixText, new RelayCommand<string>(value =>
                {
                    if (!int.TryParse(value, out int result))
                        return;
                    pi.SetValue(scriptOpt, result);
                })));
            }
        }

        return new GameOptionsViewModel(options, s.GetService<IScriptServers>()!, scriptOpt);

        static RelayCommand<bool> CreateBoolCommand(Action<bool> action) => new(action);
        static BindingOptionItemViewModel<T, ScriptOption> CreateOptionItem<T>(IServiceProvider s, string binding, string tag, IRelayCommand command) => new(Label(s, binding), tag, binding, (ScriptOption)s.GetRequiredService<IScriptOption>(), command);
        // The decamelizer keeps "ACDrops" together
        static string Label(IServiceProvider s, string binding) => binding == nameof(IScriptOption.AcceptACDrops) ? "Accept AC Drops" : s.GetRequiredService<IDecamelizer>().Decamelize(binding, null);
        static BindingOptionItemViewModel<T, ScriptOption> CreateOptionItemWithSuffix<T>(IServiceProvider s, string binding, string tag, string? suffixText, IRelayCommand command)
        {
            BindingOptionItemViewModel<T, ScriptOption> item = new(s.GetRequiredService<IDecamelizer>().Decamelize(binding, null), tag, binding, (ScriptOption)s.GetRequiredService<IScriptOption>(), command)
            {
                SuffixText = suffixText
            };
            return item;
        }
    }

    internal static ApplicationOptionsViewModel CreateAppOptions(IServiceProvider s)
    {
        List<DisplayOptionItemViewModelBase> appOptions = new()
        {
            CreateSettingOptionItem<bool>("Show Username in Title", "Whether to display logged-in character name in window title", "ShowUsernameInTitle"),
            CreateSettingOptionItem<bool>("Save Logs to File", "Append the Script and Debug logs to logs/<account>_<date>.log in the Bonxie folder (repeated lines collapsed, 5 MB per file, kept 14 days)", "LogToFile"),
            new CommandOptionItemViewModel<bool>("Grandpa Mode", "Larger, easier-to-read text in logs and packet output", "GrandpaMode", new RelayCommand<bool>(enabled =>
            {
                ISettingsService settings = Ioc.Default.GetRequiredService<ISettingsService>();
                settings.Set("GrandpaMode", enabled);
                GrandpaModeState.Set(enabled);
            }), Ioc.Default.GetRequiredService<ISettingsService>().Get<bool>("GrandpaMode")),
            CreateSettingOptionItem<bool>("Auto Update Scripts", "Whether to auto update scripts when launching the Manager or the game client (the client asks first when this is off), needs \"Check for Scripts Updates\" to be true", "AutoUpdateBotScripts"),
            CreateSettingOptionItem<bool>("Check for Script Updates", "Whether to check for scripts updates when launching the Manager or the game client", "CheckBotScriptsUpdates"),
            CreateSettingOptionItem<bool>("Auto Update AdvanceSkill Sets", "Whether to auto update advance skill sets when launching the Manager, needs \"Check for AdvanceSkill Sets updates\" to be true", "AutoUpdateAdvanceSkillSetsUpdates"),
            CreateSettingOptionItem<bool>("Check for AdvanceSkill Sets Updates", "Whether to check for scripts updates when launching the Manager", "CheckAdvanceSkillSetsUpdates"),
            CreateSettingOptionItem<bool>("Auto Update Junk Items", "Whether to auto update junk items when launching the Manager, needs \"Check for Junk Items Updates\" to be true", "AutoUpdateJunkItems"),
            CreateSettingOptionItem<bool>("Check for Junk Items Updates", "Whether to check for junk items updates when launching the Manager", "CheckJunkItemsUpdates"),
            CreateSettingOptionItem<string>("Discord Webhook URL", "URL to send Discord notifications and live statistics when scripts start, stop, or crash", "DiscordWebhookUrl"),
            new CommandOptionItemViewModel<IRelayCommand>("Test Discord Webhook", new AsyncRelayCommand(async () => {
                await Ioc.Default.GetRequiredService<IDiscordWebhookService>().TestWebhookAsync();
            })) { ActionText = "Test" },
            CreateSettingOptionItem<string>("Skua Nexus Webhook URL", "URL to send Skua Nexus notifications and live bot statistics", "NexusWebhookUrl"),
            new CommandOptionItemViewModel<IRelayCommand>("Test Skua Nexus Webhook", new AsyncRelayCommand(async () => {
                await Ioc.Default.GetRequiredService<IDiscordWebhookService>().TestNexusWebhookAsync();
            })) { ActionText = "Test" },
            CreateSettingOptionItem<bool>("Notify on Script Started", "Send alerts and initial statistics when a script starts running", "WebhookNotifyStarted"),
            CreateSettingOptionItem<bool>("Notify on Script Stopped", "Send session summary and statistics when a script finishes or stops", "WebhookNotifyStopped"),
            CreateSettingOptionItem<bool>("Notify on Script Crashed", "Send alerts when a script crashes with an error", "WebhookNotifyCrashed"),
            CreateSettingOptionItem<bool>("Notify on Relog", "Send alerts when Skua disconnects and relogs", "WebhookNotifyRelogged"),
            CreateSettingOptionItem<bool>("Notify on Item Drop", "Send webhook notifications (and screenshot) when a specific item drops", "WebhookNotifyItemDrops"),
            CreateSettingOptionItem<string>("Drop List", "Comma-separated list of items to notify on drop", "WebhookNotifyItemDropsList"),
            CreateSettingOptionItem<bool>("Enable Health Pings", "Whether to send periodic health and bot statistics pings to Discord", "WebhookNotifyHealthPing"),
            new CommandOptionItemViewModel<int>("Health Ping Interval", "Minutes between health pings (e.g. 5, 10, 30)", "WebhookPingInterval", "Min", new RelayCommand<string>(value =>
            {
                if (!int.TryParse(value, out int result) || result < 1) return;
                Ioc.Default.GetRequiredService<ISettingsService>().Set("WebhookPingInterval", result);
            }), s.GetRequiredService<ISettingsService>().Get<int>("WebhookPingInterval")),
            new CommandOptionItemViewModel<int>("Client Animation Frame-rate", "Client side animation frame-rate setting", "ClientAnim", "FPS", new RelayCommand<string>(value =>
            {
                if (!int.TryParse(value, out int result) || result < 1)
                    return;
                Ioc.Default.GetRequiredService<ISettingsService>().Set("AnimationFrameRate", result);
            }), s.GetRequiredService<ISettingsService>().Get<int>("AnimationFrameRate")) { RequiresRestart = true },
            new CommandOptionItemViewModel<int>("Relogin Try Delay", "Delay in milliseconds between login attempts when disconnected", "ReloginTryDelay", "ms", new RelayCommand<string>(value =>
            {
                if (!int.TryParse(value, out int result) || result < 0)
                    return;
                Ioc.Default.GetRequiredService<ISettingsService>().Set("ReloginTryDelay", result);
            }), s.GetRequiredService<ISettingsService>().Get<int>("ReloginTryDelay")),
            CreateSettingOptionItem<bool>("Relogin Watchdog", "Watch for unexpected disconnections, even with no script running, and relog when Auto Relogin (Game Options) is on. Off leaves relogs to kicks and server logouts", "ReloginWatchdog"),
            new CommandOptionItemViewModel<IRelayCommand>("Clear Flash Cache", "ClearFlashCache", new RelayCommand(() =>
            {
                // The game player (Ruffle) keeps the game's saved data (Flash shared objects) here.
                string ruffleDirPath = OperatingSystem.IsLinux()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ruffle")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ruffle");

                foreach (string sub in new[] { "SharedObjects", "shared_objects" })
                {
                    string dir = Path.Combine(ruffleDirPath, sub);
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                if (OperatingSystem.IsLinux())
                {
                    string cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdgCache
                        && Path.IsPathRooted(xdgCache)
                        ? xdgCache
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
                    string ruffleCache = Path.Combine(cacheHome, "ruffle");
                    if (Directory.Exists(ruffleCache))
                        Directory.Delete(ruffleCache, true);
                }
            })) { ActionText = "Clear" }
        };

        StartMugetsuSettingsSync();
        string ruffleDir = GetRuffleDir();
        MugetsuSettingsSnapshot snapshot = ReadMugetsuSettingsSnapshot(ruffleDir);

        if (OperatingSystem.IsLinux())
        {
            var graphicsChoice = new CBOChoiceOptionItemViewModel(
                "* Graphics API",
                "Graphics backend for Mugetsu. Takes effect on next launch",
                "GraphicsApi",
                new List<string> { "OpenGL", "Vulkan", "Auto" },
                snapshot.GraphicsIdx);
            _graphicsChoiceRef = new(graphicsChoice);

            graphicsChoice.PropertyChanged += (_, e) =>
            {
                if (_isSyncingFromDisk || e.PropertyName != nameof(DisplayOptionItemViewModelBase.Value))
                    return;
                string toWrite = graphicsChoice.Value switch
                {
                    1 => "vulkan",
                    2 => "auto",
                    _ => "gl"
                };
                if (WriteMugetsuFile(ruffleDir, "mugetsu-graphics.txt", toWrite))
                    SyncMugetsuSettingsFromDisk(sendLive: true);
            };
            appOptions.Add(graphicsChoice);
        }

        // Performance Mode
        var perfChoice = new CBOChoiceOptionItemViewModel(
            "Performance Mode",
            "Fewer pixels: faster on weak graphics (Off, Balanced 75%, Fast 50%)",
            "MugetsuPerformanceMode",
            new List<string> { "Off", "Balanced", "Fast" },
            snapshot.PerformanceIdx);
        _perfChoiceRef = new(perfChoice);
        perfChoice.PropertyChanged += (_, e) =>
        {
            if (_isSyncingFromDisk || e.PropertyName != nameof(DisplayOptionItemViewModelBase.Value))
                return;
            string val = perfChoice.Value switch { 1 => "balanced", 2 => "fast", _ => "off" };
            if (WriteMugetsuFile(ruffleDir, "mugetsu-performance.txt", val == "off" ? null : val + "\n"))
                SyncMugetsuSettingsFromDisk(sendLive: true);
        };
        appOptions.Add(perfChoice);

        // Frame Interpolation
        var interpOption = new CommandOptionItemViewModel<bool>(
            "* Frame Interpolation",
            "Smoother motion, best on strong PCs (enabling requires client restart if render thread started without it)",
            "MugetsuFrameInterpolation",
            (IRelayCommand)new RelayCommand<bool>(on =>
            {
                if (_isSyncingFromDisk)
                    return;
                if (WriteMugetsuFile(ruffleDir, "mugetsu-interpolation.txt", on ? "on\n" : null))
                    SyncMugetsuSettingsFromDisk(sendLive: true);
            }),
            snapshot.Interpolation);
        _interpOptionRef = new(interpOption);
        appOptions.Add(interpOption);

        // Frame Pacing
        var pacingChoice = new CBOChoiceOptionItemViewModel(
            "Frame Pacing",
            "V-sync or Uncapped (for G-Sync / FreeSync when Frame Interpolation is on)",
            "MugetsuFramePacing",
            new List<string> { "V-sync", "Uncapped" },
            snapshot.Uncapped ? 1 : 0);
        _pacingChoiceRef = new(pacingChoice);
        pacingChoice.PropertyChanged += (_, e) =>
        {
            if (_isSyncingFromDisk || e.PropertyName != nameof(DisplayOptionItemViewModelBase.Value))
                return;
            bool on = pacingChoice.Value is 1;
            if (WriteMugetsuFile(ruffleDir, "mugetsu-uncapped.txt", on ? "on\n" : null))
                SyncMugetsuSettingsFromDisk(sendLive: true);
        };
        appOptions.Add(pacingChoice);

        // FPS Limit (Uncapped)
        var fpsLimitChoice = new CBOChoiceOptionItemViewModel(
            "FPS Limit (Uncapped)",
            "Caps uncapped interpolated frames (Off, Screen refresh rate, or fixed FPS)",
            "MugetsuFpsLimit",
            new List<string> { "Off", "Screen", "60", "120", "144", "165", "240", "360" },
            snapshot.FpsLimitIdx);
        _fpsLimitChoiceRef = new(fpsLimitChoice);
        fpsLimitChoice.PropertyChanged += (_, e) =>
        {
            if (_isSyncingFromDisk || e.PropertyName != nameof(DisplayOptionItemViewModelBase.Value))
                return;
            string val = fpsLimitChoice.Value switch
            {
                1 => "screen",
                2 => "60",
                3 => "120",
                4 => "144",
                5 => "165",
                6 => "240",
                7 => "360",
                _ => "off"
            };
            if (WriteMugetsuFile(ruffleDir, "mugetsu-fps-limit.txt", val == "off" ? null : val))
                SyncMugetsuSettingsFromDisk(sendLive: true);
        };
        appOptions.Add(fpsLimitChoice);

        // Memory Limit
        var memLimitChoice = new CBOChoiceOptionItemViewModel(
            "Memory Limit",
            "Mugetsu trims unused shapes, textures, and heap pages when exceeding this limit",
            "MugetsuMemoryLimitMb",
            new List<string> { "Off", "1 GB (1024 MB)", "2 GB (2048 MB)", "3 GB (3072 MB)", "4 GB (4096 MB)" },
            snapshot.MemoryLimitIdx);
        _memLimitChoiceRef = new(memLimitChoice);
        memLimitChoice.PropertyChanged += (_, e) =>
        {
            if (_isSyncingFromDisk || e.PropertyName != nameof(DisplayOptionItemViewModelBase.Value))
                return;
            string mb = memLimitChoice.Value switch
            {
                1 => "1024",
                2 => "2048",
                3 => "3072",
                4 => "4096",
                _ => "0"
            };
            if (WriteMugetsuFile(ruffleDir, "mugetsu-memory-limit.txt", mb == "0" ? null : mb))
                SyncMugetsuSettingsFromDisk(sendLive: true);
        };
        appOptions.Add(memLimitChoice);

        // Free Memory Now
        appOptions.Add(new CommandOptionItemViewModel<IRelayCommand>(
            "Free Memory Now",
            "MugetsuFreeMemoryNow",
            new RelayCommand(() => SendMugetsuSetting("free_memory", "1"))) { ActionText = "Free" });

        // Show FPS
        var showFpsOption = new CommandOptionItemViewModel<bool>(
            "Show FPS",
            "Show Mugetsu's corner FPS counter overlay",
            "MugetsuShowFps",
            (IRelayCommand)new RelayCommand<bool>(on =>
            {
                if (_isSyncingFromDisk)
                    return;
                if (WriteMugetsuFile(ruffleDir, "mugetsu-show-fps.txt", on ? "on\n" : null))
                    SyncMugetsuSettingsFromDisk(sendLive: true);
            }),
            snapshot.ShowFps);
        _showFpsOptionRef = new(showFpsOption);
        appOptions.Add(showFpsOption);

        return new ApplicationOptionsViewModel(appOptions);

        static RelayCommand<T> CreateSettingCommand<T>(string key) => new(b => Ioc.Default.GetRequiredService<ISettingsService>().Set(key, b));
        static CommandOptionItemViewModel<T> CreateSettingOptionItem<T>(string content, string description, string key) => new(content, description, key, (IRelayCommand)CreateSettingCommand<T>(key), Ioc.Default.GetRequiredService<ISettingsService>().Get<T>(key));
    }
}
