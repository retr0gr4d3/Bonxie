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

// The named pipe on which later launches hand their arguments to this host.
public partial class TabbedHostWindow
{
    #region Pipe Server

    private void StartPipeServer()
    {
        Task.Run(async () =>
        {
            while (!_isClosing && !_pipeCts.Token.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        App.HostPipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    using var reg = _pipeCts.Token.Register(() =>
                    {
                        try { pipeServer?.Dispose(); } catch { }
                    });

                    await pipeServer.WaitForConnectionAsync(_pipeCts.Token);

                    using (pipeServer)
                    using (StreamReader sr = new(pipeServer))
                    {
                        string? line = await sr.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            if (line.StartsWith("ARTWORK|", StringComparison.Ordinal))
                            {
                                string encodedPath = line.Substring("ARTWORK|".Length);
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        string artworkPath = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));
                                        Skua.Shared.Avalonia.Controls.Shell.WindowTitleBar.SetArtworkPath(
                                            string.IsNullOrWhiteSpace(artworkPath) ? null : artworkPath);
                                    }
                                    catch (Exception ex)
                                    {
                                        Trace.WriteLine($"Bonxie artwork sync failed: {ex.GetType().Name}: {ex.Message}");
                                    }
                                });
                            }
                            else if (line.StartsWith("THEME|", StringComparison.Ordinal))
                            {
                                string themePayload = line.Substring("THEME|".Length);
                                bool useAccentOnMainTitleBar = false;
                                string themeCsv = themePayload;
                                int separatorIndex = themePayload.IndexOf('|');
                                if (separatorIndex >= 0 &&
                                    bool.TryParse(themePayload[..separatorIndex], out bool parsedAccentSetting))
                                {
                                    useAccentOnMainTitleBar = parsedAccentSetting;
                                    themeCsv = themePayload[(separatorIndex + 1)..];
                                }

                                Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        Skua.Shared.Avalonia.Services.ThemeItem? themeItem =
                                            Skua.Shared.Avalonia.Services.ThemeItem.FromString(themeCsv);
                                        if (themeItem is not null &&
                                            Ioc.Default.GetService<IThemeService>() is Skua.Shared.Avalonia.Services.AvaloniaThemeService themeService)
                                        {
                                            themeService.ApplyExternalThemeSnapshot(themeItem, useAccentOnMainTitleBar);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Trace.WriteLine($"Bonxie theme sync failed: {ex.GetType().Name}: {ex.Message}");
                                    }
                                });
                            }
                            else
                            {
                                string[]? parsedArgs = null;
                                try
                                {
                                    if (line.TrimStart().StartsWith("["))
                                    {
                                        parsedArgs = System.Text.Json.JsonSerializer.Deserialize<string[]>(line);
                                    }
                                }
                                catch { }

                                parsedArgs ??= ParseRawCommandLine(line);
                                Dispatcher.UIThread.Post(() => AddNewInstance(parsedArgs));
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    try { pipeServer?.Dispose(); } catch { }
                    break;
                }
                catch
                {
                    try { pipeServer?.Dispose(); } catch { }
                    if (_isClosing || _pipeCts.Token.IsCancellationRequested) break;
                    await Task.Delay(100);
                }
            }
        });
    }

    private static string[] ParseRawCommandLine(string? cmdLine)
    {
        if (string.IsNullOrWhiteSpace(cmdLine))
            return Array.Empty<string>();

        List<string> args = new();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < cmdLine.Length; i++)
        {
            char c = cmdLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args.ToArray();
    }

    #endregion Pipe Server
}
