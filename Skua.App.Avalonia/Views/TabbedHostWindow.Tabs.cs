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

    public void AddNewInstance(string[]? clientArgs = null)
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
        for (int i = 0; i < clientArgs.Length; i++)
        {
            if ((clientArgs[i] == "-u" || clientArgs[i] == "--user") && i + 1 < clientArgs.Length)
            {
                if (!string.IsNullOrWhiteSpace(clientArgs[i + 1]))
                    tabName = clientArgs[i + 1];
                break;
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

        TabInfo info = new TabInfo();

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
                _isSpawning = false;
                ProcessNextSpawn();
                return;
            }

            Process p = new Process();
            p.StartInfo.FileName = Process.GetCurrentProcess().MainModule?.FileName ?? "";
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
                    if (!_isClosing)
                    {
                        CloseTab(newTab);
                    }
                });
            };

            try
            {
                p.Start();
            }
            catch (Exception ex)
            {
                p.Dispose();
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

        headerPanel.PointerMoved += async (s, ev) =>
        {
            if (editTitle.IsVisible) return;

            if (ev.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            {
                Point mousePos = ev.GetPosition(null);
                Vector diff = startPoint - mousePos;
                if (Math.Abs(diff.X) > 4 || Math.Abs(diff.Y) > 4)
                {
                    var dragData = new global::Avalonia.Input.DataObject();
                    dragData.Set("TabItem", newTab);
                    await DragDrop.DoDragDrop(ev, dragData, DragDropEffects.Move);
                }
            }
        };

        newTab.AddHandler(DragDrop.DropEvent, (s, ev) =>
        {
            if (ev.Data.Contains("TabItem"))
            {
                TabItem? droppedTab = ev.Data.Get("TabItem") as TabItem;
                if (droppedTab != null && droppedTab != newTab)
                {
                    int targetIndex = InstancesTabControl.Items.IndexOf(newTab);
                    InstancesTabControl.Items.Remove(droppedTab);
                    InstancesTabControl.Items.Insert(targetIndex, droppedTab);
                    InstancesTabControl.SelectedItem = droppedTab;
                }
            }
        });

        #endregion Tab Interactions
    }

    #endregion Tab Management
}
