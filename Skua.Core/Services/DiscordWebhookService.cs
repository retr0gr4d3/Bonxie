using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;

namespace Skua.Core.Services;

public class DiscordWebhookService : IDiscordWebhookService, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IScreenshotService _screenshotService;
    private readonly IServiceProvider _serviceProvider;
    private readonly HttpClient _httpClient;
    private System.Threading.Timer? _pingTimer;
    private bool _initialized;
    private DateTime? _scriptStartTime;
    public bool SuppressDefaultNotifications { get; set; }

    // Queue system
    private readonly ConcurrentQueue<Func<Task>> _messageQueue = new();
    private readonly SemaphoreSlim _queueSemaphore = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _queueLock = new();
    private readonly Task _queueProcessorTask;
    private bool _disposed;

    // Snapshot state
    private int _startGold;
    private int _startExp;
    private int _startLevel;

    public DiscordWebhookService(ISettingsService settingsService, IScreenshotService screenshotService, IServiceProvider serviceProvider)
    {
        _settingsService = settingsService;
        _screenshotService = screenshotService;
        _serviceProvider = serviceProvider;
        _httpClient = new HttpClient();

        // Start background queue processor
        _queueProcessorTask = ProcessQueueAsync();
        Initialize();
    }

    public void Initialize()
    {
        if (_initialized) return;

        StrongReferenceMessenger.Default.Register<DiscordWebhookService, ScriptStartedMessage, int>(this, (int)MessageChannels.ScriptStatus, (r, m) => r.OnScriptStarted(m));
        StrongReferenceMessenger.Default.Register<DiscordWebhookService, ScriptStoppedMessage, int>(this, (int)MessageChannels.ScriptStatus, (r, m) => r.OnScriptStopped(m));
        StrongReferenceMessenger.Default.Register<DiscordWebhookService, ScriptErrorMessage, int>(this, (int)MessageChannels.ScriptStatus, (r, m) => r.OnScriptError(m));
        StrongReferenceMessenger.Default.Register<DiscordWebhookService, ReloginTriggeredMessage, int>(this, (int)MessageChannels.GameEvents, (r, m) => r.OnRelogin(m));
        StrongReferenceMessenger.Default.Register<DiscordWebhookService, ItemDroppedMessage, int>(this, (int)MessageChannels.GameEvents, (r, m) => r.OnItemDropped(m));

        _initialized = true;
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                await _queueSemaphore.WaitAsync(_cts.Token);
                if (!_messageQueue.TryDequeue(out Func<Task>? action) || action is null)
                    continue;

                try
                {
                    await action();

                    // Rate limit: Max 2 messages per second
                    await Task.Delay(500, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Discord webhook queue action failed: {ex}");
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected when the service is shutting down.
        }
    }

    private void EnqueueAction(Func<Task> action)
    {
        lock (_queueLock)
        {
            if (_disposed)
                return;

            _messageQueue.Enqueue(action);
            _queueSemaphore.Release();
        }
    }

    private string GetBotName()
    {
        var bot = _serviceProvider.GetService<IScriptInterface>();
        if (bot != null && bot.Player != null)
        {
            if (!string.IsNullOrWhiteSpace(bot.Player.Username) && bot.Player.Username != "loginInfo.strUsername")
                return bot.Player.Username;
            if (bot.Servers != null && !string.IsNullOrWhiteSpace(bot.Servers.CachedUsername))
                return bot.Servers.CachedUsername!;
        }
        return "Bonxie Bot";
    }

    private void OnScriptStarted(ScriptStartedMessage msg)
    {
        _scriptStartTime = DateTime.Now;
        var bot = _serviceProvider.GetService<IScriptInterface>();

        if (bot != null && bot.Player.Playing)
        {
            _startGold = bot.Player.Gold;
            _startExp = bot.Player.XP;
            _startLevel = bot.Player.Level;
        }
        else
        {
            _startGold = 0;
            _startExp = 0;
            _startLevel = 0;
        }

        string scriptName = "a script";
        if (bot != null && !string.IsNullOrWhiteSpace(bot.Manager?.LoadedScript))
        {
            scriptName = System.IO.Path.GetFileNameWithoutExtension(bot.Manager.LoadedScript);
        }

        var settings = _settingsService.GetClient();
        if (settings != null && settings.WebhookNotifyStarted && !SuppressDefaultNotifications)
        {
            EnqueueAction(async () => await SendEmbedAsync("Script Started", $"**{GetBotName()}** has begun execution of **{scriptName}**.", 0x00FF00)); // Green
        }

        // The first dashboard snapshot goes to Skua Nexus when it is set up; Discord gets one only when the user
        // turned Health Pings on.
        if (!EnqueueHealthPing(settings?.NexusWebhookUrl, "is running.") && settings is { WebhookNotifyHealthPing: true })
            EnqueueHealthPing(settings.DiscordWebhookUrl, "is running.");

        StartPingTimer();
    }

    /// <summary>Queues a "Health Ping" embed with the dashboard snapshot for <paramref name="url"/>.</summary>
    /// <returns>False when <paramref name="url"/> is not set, so nothing was queued.</returns>
    private bool EnqueueHealthPing(string? url, string status, int color = 0x808080)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        string target = url.Trim();
        EnqueueAction(() => SendEmbedToUrlAsync(target, "Health Ping", $"**{GetBotName()}** {status}", color, BuildDashboardSnapshotFields()));
        return true;
    }

    private void OnScriptStopped(ScriptStoppedMessage msg)
    {
        var settings = _settingsService.GetClient();
        if (settings is { WebhookNotifyStopped: true } && !SuppressDefaultNotifications)
            EnqueueSessionSummary();

        EnqueueHealthPing(settings?.NexusWebhookUrl, "has stopped.");

        _scriptStartTime = null;
        StopPingTimer();
    }

    private void EnqueueSessionSummary()
    {
        var bot = _serviceProvider.GetService<IScriptInterface>();
        if (bot != null && bot.Player.Playing)
        {
            if (_scriptStartTime.HasValue)
            {
                var timeElapsed = DateTime.Now - _scriptStartTime.Value;
                int goldEarned = bot.Player.Gold - _startGold;
                int expEarned = bot.Player.XP - _startExp;

                string scriptName = "a script";
                if (bot.Manager.LoadedScript != null)
                {
                    scriptName = System.IO.Path.GetFileNameWithoutExtension(bot.Manager.LoadedScript);
                }

                string botName = GetBotName();

                var fields = new List<object>();

                if (goldEarned != 0) fields.Add(new { name = "Gold Earned", value = goldEarned > 0 ? $"{goldEarned:N0}" : "0", inline = true });
                if (expEarned != 0) fields.Add(new { name = "XP Earned", value = expEarned > 0 ? $"{expEarned:N0}" : "0", inline = true });
                fields.Add(new { name = "Time Elapsed", value = $"{timeElapsed.Hours:D2}:{timeElapsed.Minutes:D2}:{timeElapsed.Seconds:D2}", inline = false });

                EnqueueAction(async () => await SendEmbedAsync("Farming Session Concluded", $"**{botName}** has stopped execution of **{scriptName}**.", 0xFF0000, fields));
            }
        }
    }

    private void OnScriptError(ScriptErrorMessage msg)
    {
        var settings = _settingsService.GetClient();
        if (settings != null && settings.WebhookNotifyCrashed)
        {
            var actualEx = msg.Exception is System.Reflection.TargetInvocationException && msg.Exception.InnerException != null
                ? msg.Exception.InnerException
                : msg.Exception;

            var fields = new List<object>
            {
                new { name = "Exception", value = $"```{actualEx.Message}```", inline = false }
            };
            EnqueueAction(async () => await SendEmbedAsync("Script Error", $"**{GetBotName()}** crashed unexpectedly.", 0xFF0000, fields)); // Red
        }

        EnqueueHealthPing(settings?.NexusWebhookUrl, "crashed.", 0xFF0000);

        StopPingTimer();
    }

    private void OnRelogin(ReloginTriggeredMessage msg)
    {
        var settings = _settingsService.GetClient();
        if (settings != null && settings.WebhookNotifyRelogged)
        {
            string title = msg.WasKicked ? "Kicked & Relogging" : "Disconnected & Relogging";
            EnqueueAction(async () => await SendEmbedAsync(title, $"**{GetBotName()}** is attempting to reconnect.", 0xFFA500)); // Orange
        }
    }

    private void StartPingTimer()
    {
        var settings = _settingsService.GetClient();
        if (settings == null) return;

        bool hasNexus = !string.IsNullOrWhiteSpace(settings.NexusWebhookUrl);
        bool sendDiscord = !string.IsNullOrWhiteSpace(settings.DiscordWebhookUrl) && settings.WebhookNotifyHealthPing;

        if (!hasNexus && !sendDiscord)
        {
            StopPingTimer();
            return;
        }

        int intervalMinutes = settings.WebhookPingInterval > 0 
            ? settings.WebhookPingInterval 
            : (sendDiscord ? 10 : 1);

        _pingTimer?.Dispose();
        int intervalMs = (int)TimeSpan.FromMinutes(intervalMinutes).TotalMilliseconds;
        _pingTimer = new System.Threading.Timer(OnPingTimerElapsed, null, intervalMs, intervalMs);
    }

    private void StopPingTimer()
    {
        _pingTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnPingTimerElapsed(object? state)
    {
        var settings = _settingsService.GetClient();
        if (settings == null)
        {
            StopPingTimer();
            return;
        }

        bool hasNexus = !string.IsNullOrWhiteSpace(settings.NexusWebhookUrl);
        bool sendDiscord = !string.IsNullOrWhiteSpace(settings.DiscordWebhookUrl) && settings.WebhookNotifyHealthPing;

        if (!hasNexus && !sendDiscord)
        {
            StopPingTimer();
            return;
        }

        if (sendDiscord)
            EnqueueHealthPing(settings.DiscordWebhookUrl, "is running flawlessly.");
        EnqueueHealthPing(settings.NexusWebhookUrl, "is running flawlessly.");
    }

    /// <summary>
    /// Builds a compact snapshot for the local dashboard using the existing Bonxie scripting interfaces.
    /// </summary>
    /// <returns>Webhook fields containing current character, equipment, and active quest data.</returns>
    private List<object> BuildDashboardSnapshotFields()
    {
        List<object> fields = new();
        IScriptInterface? bot = _serviceProvider.GetService<IScriptInterface>();
        if (bot?.Player == null || !bot.Player.Playing)
        {
            fields.Add(new { name = "Character", value = GetBotName(), inline = true });
            fields.Add(new { name = "Status", value = "Idle / Not in World", inline = true });
            return fields;
        }

        fields.Add(new { name = "Character", value = GetBotName(), inline = true });

        // Include location and script state so the relay can populate the dashboard's existing fields.
        string mapName = bot.Map?.FullName ?? string.Empty;
        fields.Add(new { name = "Map", value = string.IsNullOrWhiteSpace(mapName) ? "Unknown" : mapName, inline = true });
        fields.Add(new { name = "Room", value = bot.Map != null ? $"{bot.Player.Cell}-{bot.Player.Pad} (Room {bot.Map.RoomID})" : "Unknown", inline = true });

        string scriptName = string.IsNullOrWhiteSpace(bot.Manager?.LoadedScript)
            ? "None"
            : System.IO.Path.GetFileNameWithoutExtension(bot.Manager.LoadedScript);
        fields.Add(new { name = "Script", value = scriptName, inline = true });

        fields.Add(new { name = "Level", value = bot.Player.Level.ToString(), inline = true });
        fields.Add(new { name = "Gold", value = bot.Player.Gold.ToString("N0"), inline = true });
        fields.Add(new { name = "AC", value = bot.Player.Coins.ToString("N0"), inline = true });
        fields.Add(new { name = "HP", value = $"{bot.Player.Health}/{bot.Player.MaxHealth}", inline = true });
        fields.Add(new { name = "MP", value = $"{bot.Player.Mana}/{bot.Player.MaxMana}", inline = true });

        string equipment = string.Join("\n", bot.Inventory.Items
            .Where(item => item.Equipped)
            .Select(item => item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)));

        if (string.IsNullOrWhiteSpace(equipment))
            equipment = "No equipped items reported.";
        if (equipment.Length > 1000)
            equipment = equipment[..1000];

        string quests = string.Join("\n", bot.Quests.Active
            .Where(quest => quest != null && !string.IsNullOrWhiteSpace(quest.Name))
            .Select(quest => quest.Name));

        if (string.IsNullOrWhiteSpace(quests))
            quests = "No active quests reported.";
        if (quests.Length > 1000)
            quests = quests[..1000];

        fields.Add(new { name = "Equipment", value = equipment, inline = false });

        // Report the actual item count; the public inventory interface does not expose total bag slots.
        fields.Add(new { name = "Inventory", value = $"{bot.Inventory.Items.Count} items", inline = true });
        fields.Add(new { name = "Quest", value = quests, inline = false });

        // These are the live counters displayed in Combat > Stats in Skua.
        // Dashboard "Quests" represents quests completed and turned in.
        fields.Add(new { name = "Kills", value = bot.Stats.Kills.ToString(), inline = true });
        fields.Add(new { name = "Drops", value = bot.Stats.Drops.ToString(), inline = true });
        fields.Add(new { name = "Quests", value = bot.Stats.QuestsCompleted.ToString(), inline = true });
        fields.Add(new { name = "Deaths", value = bot.Stats.Deaths.ToString(), inline = true });
        fields.Add(new { name = "Relogins", value = bot.Stats.Relogins.ToString(), inline = true });
        return fields;
    }

    private List<string> GetConfiguredWebhookUrls()
    {
        var list = new List<string>(2);
        var clientSettings = _settingsService.GetClient();
        if (clientSettings != null)
        {
            if (!string.IsNullOrWhiteSpace(clientSettings.DiscordWebhookUrl))
                list.Add(clientSettings.DiscordWebhookUrl.Trim());
            if (!string.IsNullOrWhiteSpace(clientSettings.NexusWebhookUrl))
                list.Add(clientSettings.NexusWebhookUrl.Trim());
        }
        return list;
    }

    public Task TestWebhookAsync()
    {
        string? url = _settingsService.GetClient()?.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
            return Task.CompletedTask;

        EnqueueAction(async () =>
        {
            List<object> fields = BuildDashboardSnapshotFields();
            await SendEmbedToUrlAsync(url, "Discord Webhook Test Successful!", "Your Bonxie Discord Webhooks are perfectly configured.", 0x00FF00, fields);
        });

        return Task.CompletedTask;
    }

    public Task TestNexusWebhookAsync()
    {
        string? url = _settingsService.GetClient()?.NexusWebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
            return Task.CompletedTask;

        EnqueueAction(async () =>
        {
            List<object> fields = BuildDashboardSnapshotFields();
            await SendEmbedToUrlAsync(url, "Skua Nexus Webhook Test Successful!", "Your Bonxie Nexus Webhooks are perfectly configured.", 0x00FF00, fields);
        });

        return Task.CompletedTask;
    }

    public Task SendMessageAsync(string message)
    {
        EnqueueAction(() => PostWebhookAsync(new { content = message }));
        return Task.CompletedTask;
    }

    public Task SendScreenshotAsync(string message)
    {
        EnqueueAction(async () =>
        {
            try
            {
                List<string> urls = GetConfiguredWebhookUrls();
                if (urls.Count == 0)
                    return;

                byte[] screenshotBytes = await _screenshotService.TakeScreenshotAsync();

                var embed = new
                {
                    description = message,
                    color = 0xFFD700, // Gold
                    image = new { url = "attachment://screenshot.png" }
                };

                string payloadJson = JsonSerializer.Serialize(new { embeds = new[] { embed } });

                foreach (string url in urls)
                    await PostMultipartToUrlAsync(url, payloadJson, screenshotBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"SendScreenshotAsync failed: {ex.Message}");
            }
        });

        return Task.CompletedTask;
    }

    public async Task SendEmbedAsync(string title, string description, int color, List<object>? fields = null)
    {
        var embed = new
        {
            title = title,
            description = description,
            color = color,
            fields = fields?.ToArray()
        };

        var payload = new { embeds = new[] { embed } };
        await PostWebhookAsync(payload);
    }

    private async Task PostWebhookAsync(object payload)
    {
        var urls = GetConfiguredWebhookUrls();
        if (urls.Count == 0) return;

        var json = JsonSerializer.Serialize(payload);
        foreach (var url in urls)
        {
            await PostJsonToUrlAsync(url, json);
        }
    }

    private Task PostJsonToUrlAsync(string url, string json)
    {
        return PostWithRetryAsync(url, () => new StringContent(json, Encoding.UTF8, "application/json"));
    }

    /// <summary>
    /// Posts to a webhook, trying up to three times while it answers 429 (rate limited). Any other answer, good or
    /// bad, ends it; a webhook that can't be reached is logged and never stops the bot.
    /// </summary>
    private async Task PostWithRetryAsync(string url, Func<HttpContent> createContent)
    {
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using HttpContent content = createContent();
                using var response = await _httpClient.PostAsync(url, content);
                if ((int)response.StatusCode != 429)
                    break;

                await Task.Delay(1500 * (attempt + 1));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Webhook post failed: {ex.Message}");
        }
    }

    private async Task SendEmbedToUrlAsync(string url, string title, string description, int color, List<object>? fields = null)
    {
        var embed = new
        {
            title = title,
            description = description,
            color = color,
            fields = fields?.ToArray()
        };

        var payload = new { embeds = new[] { embed } };
        var json = JsonSerializer.Serialize(payload);
        await PostJsonToUrlAsync(url, json);
    }

    private Task PostMultipartToUrlAsync(string url, string payloadJson, byte[]? screenshotBytes)
    {
        return PostWithRetryAsync(url, () =>
        {
            var formData = new MultipartFormDataContent();
            formData.Add(new StringContent(payloadJson), "payload_json");

            if (screenshotBytes != null && screenshotBytes.Length > 0)
            {
                var imageContent = new ByteArrayContent(screenshotBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                formData.Add(imageContent, "file", "screenshot.png");
            }

            return formData;
        });
    }

    private void OnItemDropped(ItemDroppedMessage msg)
    {
        var settings = _settingsService.GetClient();
        if (settings == null || !settings.WebhookNotifyItemDrops) return;

        string[] dropList = settings.WebhookNotifyItemDropsList?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        foreach (string item in dropList)
        {
            if (!string.IsNullOrWhiteSpace(item) && msg.Item.Name.Contains(item, StringComparison.OrdinalIgnoreCase))
            {
                string wikiUrl = "https://aqwwiki.wikidot.com/" + Uri.EscapeDataString(msg.Item.Name);
                _ = SendScreenshotAsync($"🏆 **Rare Drop!** - **{GetBotName()}** just got **[{msg.Item.Name}]({wikiUrl})** x{msg.Item.Quantity}!");
                break;
            }
        }
    }

    public void Dispose()
    {
        lock (_queueLock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        StrongReferenceMessenger.Default.UnregisterAll(this);
        _pingTimer?.Dispose();
        _pingTimer = null;
        _cts.Cancel();

        // Let an in-flight webhook finish before disposing resources it may still use.
        _ = DisposeResourcesAsync();
        GC.SuppressFinalize(this);
    }

    private async Task DisposeResourcesAsync()
    {
        try
        {
            await _queueProcessorTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Webhook queue shutdown failed: {ex}");
        }
        finally
        {
            _queueSemaphore.Dispose();
            _httpClient.Dispose();
            _cts.Dispose();
        }
    }
}
