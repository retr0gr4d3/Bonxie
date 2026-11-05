using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Skua.App.Avalonia.Flash;

/// <summary>
/// Runs skua.swf in the Skua build of Mugetsu (a fast desktop Ruffle, see github.com/retr0gr4d3/skua-mugetsu) and
/// bridges ExternalInterface to it. The player connects back over a loopback TCP socket and both sides send one JSON
/// object per line: <c>{id,name,args}</c> calls a callback of the SWF and is answered with <c>{id,r}</c>, and
/// <c>{ev,args}</c> is an <c>ExternalInterface.call</c> from the SWF.
/// </summary>
public class FlashUtil : IFlashUtil
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending = new();
    private readonly object _sendLock = new();
    private TcpListener? _listener;
    private Process? _player;
    private StreamWriter? _bridge;
    private int _nextId;

    public FlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    public event FlashCallHandler? FlashCall;

    /// <summary>Native window the player draws in as a child window; 0 gives it a window of its own.</summary>
    public nint ParentWindow { get; set; }

    private static string SwfPath => Path.Combine(AppContext.BaseDirectory, "skua.swf");

    // The normal player is built for x86-64-v3 and crashes at once without AVX2; those CPUs get the x86-64-v2 build.
    private static string PlayerPath
    {
        get
        {
            string exe = OperatingSystem.IsWindows() ? "Mugetsu.exe" : "Mugetsu";
            string avxPlayer = Path.Combine(AppContext.BaseDirectory, "player-x86-64-v2", exe);
            if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported && File.Exists(avxPlayer))
                return avxPlayer;
            return Path.Combine(AppContext.BaseDirectory, "player", exe);
        }
    }

    public void InitializeFlash()
    {
        if (!File.Exists(SwfPath))
            throw new FileNotFoundException("Missing skua.swf. Build the AS3 client first so the game content can load.");
        if (!File.Exists(PlayerPath))
            throw new FileNotFoundException($"Missing the game player ({PlayerPath}). Rebuild Bonxie so it downloads the player.");

        if (_listener is null)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        StopPlayer();
        ProcessStartInfo start = new(PlayerPath) { UseShellExecute = false };
        start.ArgumentList.Add("--skua-bridge");
        start.ArgumentList.Add($"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        if (ParentWindow != 0)
        {
            start.ArgumentList.Add("--skua-parent");
            start.ArgumentList.Add(ParentWindow.ToString(CultureInfo.InvariantCulture));
            // On Linux the parent is an X11 window; the player would otherwise prefer Wayland, where it can't be embedded.
            if (!OperatingSystem.IsWindows())
                start.Environment.Remove("WAYLAND_DISPLAY");
        }
        start.ArgumentList.Add(SwfPath);
        _player = Process.Start(start);
    }

    private void StopPlayer()
    {
        try
        {
            if (_player is { HasExited: false })
                _player.Kill();
        }
        catch { /* already gone */ }
        _player?.Dispose();
        _player = null;
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is { } listener)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => RunBridgeAsync(client));
        }
    }

    private async Task RunBridgeAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            // A restarted player connects anew; whatever the old one still owed will never come.
            Interlocked.Exchange(ref _bridge, writer)?.Dispose();
            FailPending();

            try
            {
                using StreamReader reader = new(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync() is { } line)
                    Receive(JObject.Parse(line));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
            {
                Trace.WriteLine($"Game player bridge closed: {ex.Message}");
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _bridge, null, writer) == writer)
                    FailPending();
            }
        }
    }

    private void Receive(JObject msg)
    {
        if (msg["id"] is { } id)
        {
            if (_pending.TryRemove((int)id, out TaskCompletionSource<JObject>? tcs))
                tcs.TrySetResult(msg);
            return;
        }

        string function = (string)msg["ev"]!;
        object[] args = msg["args"] is JArray arr ? arr.Select(FromJson).ToArray() : [];
        // The ActiveX player raised these on the UI thread, and handlers still expect that.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                FlashCall?.Invoke(function, args);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Flash call handler for '{function}' failed: {ex}");
            }
        });
    }

    // ponytail: fails every pending call, so one sent to a new player in the instant it connects returns default too.
    private void FailPending()
    {
        foreach (int id in _pending.Keys)
            if (_pending.TryRemove(id, out TaskCompletionSource<JObject>? tcs))
                tcs.TrySetResult(new JObject());
    }

    public string Call(string function, params object[] args)
    {
        return Call<string>(function, args);
    }

    public T Call<T>(string function, params object[] args)
    {
        try
        {
            object? o = Call(function, typeof(T), args);
            return o is T value ? value : GetDefaultValue<T>();
        }
        catch
        {
            return GetDefaultValue<T>();
        }
    }

    private static T GetDefaultValue<T>()
    {
        object? defaultValue = DefaultProvider.GetDefault<T>(typeof(T));
        return defaultValue is T typed ? typed : default!;
    }

    public object Call(string function, Type type, params object[] args)
    {
        if (_lazyManager.Value.ShouldExit && Thread.CurrentThread.Name == "Script Thread")
            _lazyManager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();

        try
        {
            if (_bridge is not { } writer)
                return default!;

            int id = Interlocked.Increment(ref _nextId);
            TaskCompletionSource<JObject> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                Stopwatch callTimer = Stopwatch.StartNew();
                string request = JsonConvert.SerializeObject(new { id, name = function, args = args.Select(ToJs) });
                lock (_sendLock)
                    writer.WriteLine(request);

                if (!tcs.Task.Wait(CallTimeout))
                    throw new TimeoutException($"No answer from the game player within {CallTimeout.TotalSeconds}s.");

                if (callTimer.ElapsedMilliseconds >= 250)
                    Trace.WriteLine($"Slow Flash call: {function} took {callTimer.ElapsedMilliseconds} ms.");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }

            // Old ActiveX results came back as text, so callers still convert from a string.
            string? result = tcs.Task.Result["r"] switch
            {
                null or { Type: JTokenType.Null } => null,
                JValue { Value: bool b } => b ? "true" : "false",
                JValue v => Convert.ToString(v.Value, CultureInfo.InvariantCulture),
                JToken t => t.ToString(Formatting.None)
            };
            return result is null ? default! : Convert.ChangeType(result, type, CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            if (function != "isKicked")
                _messenger.Send<FlashErrorMessage>(new(e, function, args));
            return default!;
        }
    }

    /// <summary>Maps an argument to what the ActiveX player accepted: null, bools, numbers, objects, arrays, else text.</summary>
    private static object? ToJs(object? o)
    {
        return o switch
        {
            null or bool or double or float or long or int or string => o,
            ExpandoObject e => e.ToDictionary(kvp => kvp.Key, kvp => ToJs(kvp.Value)),
            Array a => a.Cast<object?>().Select(ToJs).ToArray(),
            _ => o.ToString()
        };
    }

    private static object FromJson(JToken t)
    {
        switch (t.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                return int.TryParse(t.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : (float)t;

            case JTokenType.Boolean:
                return (bool)t;

            case JTokenType.Null:
            case JTokenType.Undefined:
                return null!;

            case JTokenType.Array:
                return t.Select(FromJson).ToArray();

            case JTokenType.Object:
                IDictionary<string, object> d = new ExpandoObject();
                foreach (JProperty p in ((JObject)t).Properties())
                    d[p.Name] = FromJson(p.Value);
                return d;

            default:
                return (string)t!;
        }
    }

    public object FromFlashXml(XElement el)
    {
        switch (el.Name.ToString())
        {
            case "number":
                return int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, out float f) ? f : 0;

            case "true":
                return true;

            case "false":
                return false;

            case "null":
                return null!;

            case "array":
                return el.Elements().Select(e => FromFlashXml(e)).ToArray();

            case "object":
                dynamic d = new ExpandoObject();
                el.Elements().ForEach(e => d[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First()));
                return d;

            default:
                return el.Value;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    public void Dispose()
    {
        StopPlayer();
        _listener?.Stop();
        _listener = null;
        _bridge?.Dispose();
        FailPending();
    }
}
