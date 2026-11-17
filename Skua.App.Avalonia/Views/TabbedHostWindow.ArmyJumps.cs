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

// Moving every tab to a map, a saved preset, a player or a house.
public partial class TabbedHostWindow
{
    private string _lastJumpMap = "";
    private string _lastJumpCell = "";
    private string _lastJumpPlayer = "";
    private static string ArmyJumpSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua", "army-control-jump.json");

    private sealed class ArmyJumpSettings
    {
        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Player { get; set; } = "";
        public List<ArmyJumpDestination> History { get; set; } = new();
        public List<ArmyJumpDestination> Presets { get; set; } = new();
    }

    private sealed class ArmyJumpDestination
    {
        public string Name { get; set; } = "";
        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Pad { get; set; } = "Spawn";

        public string DisplayName => $"{Map}{(string.IsNullOrWhiteSpace(Room) ? "" : $"-{Room}")} - {Cell}, {Pad}";
    }

    private sealed class ArmyClientLocation
    {
        public ArmyClientLocation() { }

        public string Map { get; set; } = "";
        public string Room { get; set; } = "";
        public string Cell { get; set; } = "Enter";
        public string Pad { get; set; } = "Spawn";
        public string Username { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }

    private readonly Dictionary<int, ArmyClientLocation> _latestClientLocations = new();
    private readonly Dictionary<int, string> _lastRecordedLocationByPid = new();

    private List<ArmyJumpDestination> _jumpMapHistory = new();
    private List<ArmyJumpDestination> _jumpMapPresets = new();
    private string _lastJumpRoom = "";

    private void LoadArmyJumpSettings()
    {
        try
        {
            string path = ArmyJumpSettingsPath;
            if (!File.Exists(path)) return;
            ArmyJumpSettings? settings = JsonSerializer.Deserialize<ArmyJumpSettings>(File.ReadAllText(path));
            if (settings == null) return;
            _lastJumpMap = settings.Map ?? "";
            _lastJumpCell = settings.Cell ?? "Enter";
            _lastJumpRoom = settings.Room ?? "";
            _lastJumpPlayer = settings.Player ?? "";
            _jumpMapHistory = settings.History ?? new List<ArmyJumpDestination>();
            _jumpMapPresets = settings.Presets ?? new List<ArmyJumpDestination>();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load Army Control jump settings: {ex.Message}");
        }
    }

    private void SaveArmyJumpSettings()
    {
        try
        {
            string path = ArmyJumpSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ArmyJumpSettings
            {
                Map = _lastJumpMap,
                Cell = _lastJumpCell,
                Room = _lastJumpRoom,
                Player = _lastJumpPlayer,
                History = _jumpMapHistory,
                Presets = _jumpMapPresets
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to save Army Control jump settings: {ex.Message}");
        }
    }

    private async void MenuItem_MapTravel_Click(object? sender, RoutedEventArgs e)
    {
        Window dialog = new Window
        {
            Title = "Map Travel",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Choose a recent destination or a saved preset to jump all clients.",
            TextWrapping = TextWrapping.Wrap
        });

        TextBlock historyLabel = new TextBlock { Text = "Recent destinations" };
        ComboBox historyPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 360
        };
        StackPanel historyButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Button jumpHistoryButton = new Button { Content = "Select / Jump", MinWidth = 110 };
        Button clearHistoryButton = new Button { Content = "Clear History", MinWidth = 100 };

        void RefreshHistory()
        {
            List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
            historyPicker.ItemsSource = history.Select(item => item.DisplayName).ToList();
            historyPicker.SelectedIndex = history.Count > 0 ? 0 : -1;
            jumpHistoryButton.IsEnabled = history.Count > 0;
            clearHistoryButton.IsEnabled = history.Count > 0;
        }

        jumpHistoryButton.Click += (_, _) =>
        {
            List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
            int index = historyPicker.SelectedIndex;
            if (index >= 0 && index < history.Count)
                JumpArmyToDestination(history[index]);
        };
        clearHistoryButton.Click += (_, _) =>
        {
            _jumpMapHistory.Clear();
            SaveArmyJumpSettings();
            RefreshHistory();
        };
        historyButtons.Children.Add(jumpHistoryButton);
        historyButtons.Children.Add(clearHistoryButton);
        panel.Children.Add(historyLabel);
        panel.Children.Add(historyPicker);
        panel.Children.Add(historyButtons);

        panel.Children.Add(new Separator());

        TextBlock presetLabel = new TextBlock { Text = "Saved presets" };
        ComboBox presetPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 360
        };
        StackPanel presetButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Button jumpPresetButton = new Button { Content = "Select / Jump", MinWidth = 110 };
        Button createPresetButton = new Button { Content = "Create", MinWidth = 80 };
        Button editPresetButton = new Button { Content = "Edit", MinWidth = 70 };
        Button deletePresetButton = new Button { Content = "Delete", MinWidth = 80 };

        void RefreshPresets(int selectedIndex = 0)
        {
            presetPicker.ItemsSource = _jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}").ToList();
            presetPicker.SelectedIndex = _jumpMapPresets.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, _jumpMapPresets.Count - 1);
            bool hasPresets = _jumpMapPresets.Count > 0;
            jumpPresetButton.IsEnabled = hasPresets;
            editPresetButton.IsEnabled = hasPresets;
            deletePresetButton.IsEnabled = hasPresets;
        }

        jumpPresetButton.Click += (_, _) =>
        {
            int index = presetPicker.SelectedIndex;
            if (index >= 0 && index < _jumpMapPresets.Count)
                JumpArmyToDestination(_jumpMapPresets[index]);
        };
        createPresetButton.Click += async (_, _) =>
        {
            dialog.Hide();
            await OpenJumpMapPresetEditor(null);
            RefreshPresets(_jumpMapPresets.Count - 1);
            dialog.Show();
        };
        editPresetButton.Click += async (_, _) =>
        {
            int index = presetPicker.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            ArmyJumpDestination selectedPreset = _jumpMapPresets[index];
            dialog.Hide();
            await OpenJumpMapPresetEditor(selectedPreset);
            RefreshPresets(index);
            dialog.Show();
        };
        deletePresetButton.Click += (_, _) =>
        {
            int index = presetPicker.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            _jumpMapPresets.RemoveAt(index);
            SaveArmyJumpSettings();
            RefreshPresets(index);
        };

        presetButtons.Children.Add(jumpPresetButton);
        presetButtons.Children.Add(createPresetButton);
        presetButtons.Children.Add(editPresetButton);
        presetButtons.Children.Add(deletePresetButton);
        panel.Children.Add(presetLabel);
        panel.Children.Add(presetPicker);
        panel.Children.Add(presetButtons);

        StackPanel bottomButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Button manualJumpButton = new Button { Content = "Jump to Map / Cell...", MinWidth = 140 };
        Button closeButton = new Button { Content = "Close", MinWidth = 84, IsCancel = true };
        manualJumpButton.Click += (_, _) =>
        {
            dialog.Hide();
            MenuItem_JumpMap_Click(null, new RoutedEventArgs());
            dialog.Show();
        };
        closeButton.Click += (_, _) => dialog.Close();
        bottomButtons.Children.Add(manualJumpButton);
        bottomButtons.Children.Add(closeButton);
        panel.Children.Add(bottomButtons);

        RefreshHistory();
        RefreshPresets();
        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private void MenuItem_JumpMap_Click(object? sender, RoutedEventArgs e)
    {
        InputDialogViewModel vm = new("Jump Army to Map / Cell", "Enter destination as Map,Room,Cell (room is optional):", "e.g., yulgar,12345,Enter", false);
        vm.DialogTextInput = $"{_lastJumpMap},{_lastJumpRoom},{_lastJumpCell}";

        if (Ioc.Default.GetRequiredService<IDialogService>().ShowDialog(vm) != true)
            return;

        string[] parts = (vm.DialogTextInput ?? "").Split(',');
        string targetMap = parts.Length > 0 ? parts[0].Trim() : "";
        string targetRoom = parts.Length > 2 ? parts[1].Trim() : "";
        string targetCell = parts.Length > 2 ? parts[2].Trim() : (parts.Length > 1 ? parts[1].Trim() : "Enter");

        if (!string.IsNullOrWhiteSpace(targetMap))
            JumpArmyToDestination(new ArmyJumpDestination { Map = targetMap, Room = targetRoom, Cell = string.IsNullOrWhiteSpace(targetCell) ? "Enter" : targetCell });
    }

    private void JumpArmyToDestination(ArmyJumpDestination destination)
    {
        _lastJumpMap = destination.Map.Trim();
        _lastJumpRoom = destination.Room.Trim();
        _lastJumpCell = string.IsNullOrWhiteSpace(destination.Cell) ? "Enter" : destination.Cell.Trim();

        SaveArmyJumpSettings();
        string mapToJoin = string.IsNullOrWhiteSpace(_lastJumpRoom) ? _lastJumpMap : $"{_lastJumpMap}-{_lastJumpRoom}";
        string tempFile = TabHostFiles.Jump;
        File.WriteAllLines(tempFile, new[] { mapToJoin, _lastJumpCell, string.IsNullOrWhiteSpace(destination.Pad) ? "Spawn" : destination.Pad.Trim() });

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostMessage(info.ChildHwnd, WM_SKUA_JUMP_MAP, IntPtr.Zero, IntPtr.Zero);
    }

    private async void MenuItem_JumpMapHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_jumpMapHistory.Count == 0)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("No recently visited maps yet. Maps will appear here as your open clients travel.", "Jump Map History");
            return;
        }

        Window dialog = new Window
        {
            Title = "Jump to Recent Destination",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select a recent map, room and cell:", TextWrapping = TextWrapping.Wrap });

        List<ArmyJumpDestination> history = _jumpMapHistory.ToList();
        ComboBox historyPicker = new ComboBox
        {
            ItemsSource = history.Select(item => item.DisplayName).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(historyPicker);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button clearButton = new Button { Content = "Clear History", MinWidth = 100 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button jumpButton = new Button { Content = "Jump", MinWidth = 84, IsDefault = true };
        clearButton.Click += (_, _) =>
        {
            _jumpMapHistory.Clear();
            SaveArmyJumpSettings();
            dialog.Close(false);
        };
        cancelButton.Click += (_, _) => dialog.Close(false);
        jumpButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(clearButton);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(jumpButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        if (confirmed != true || historyPicker.SelectedIndex < 0 || historyPicker.SelectedIndex >= history.Count)
            return;

        JumpArmyToDestination(history[historyPicker.SelectedIndex]);
    }

    private async void MenuItem_SaveJumpMapPreset_Click(object? sender, RoutedEventArgs e)
    {
        await OpenJumpMapPresetEditor(null);
    }

    private async Task OpenJumpMapPresetEditor(ArmyJumpDestination? existingPreset)
    {
        Window dialog = new Window
        {
            Title = existingPreset == null ? "Create Map Preset" : "Edit Map Preset",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        TextBox nameInput = new TextBox { Watermark = "Preset name", Text = existingPreset?.Name ?? "" };
        TextBox mapInput = new TextBox { Watermark = "Map name", Text = existingPreset?.Map ?? "" };
        TextBox roomInput = new TextBox { Watermark = "Room number (optional)", Text = existingPreset?.Room ?? "" };
        TextBox cellInput = new TextBox { Watermark = "Cell", Text = existingPreset?.Cell ?? "Enter" };
        TextBox padInput = new TextBox { Watermark = "Pad", Text = existingPreset?.Pad ?? "Spawn" };

        // New presets start with the selected client's current location when available.
        if (existingPreset == null
            && InstancesTabControl.SelectedItem is TabItem currentTab
            && _tabs.TryGetValue(currentTab, out TabInfo? currentInfo)
            && currentInfo.Process != null
            && _latestClientLocations.TryGetValue(currentInfo.Process.Id, out ArmyClientLocation? currentLocation)
            && currentLocation != null
            && currentLocation.UpdatedUtc >= DateTime.UtcNow.AddSeconds(-5)
            && !string.IsNullOrWhiteSpace(currentLocation.Map))
        {
            mapInput.Text = currentLocation.Map;
            roomInput.Text = currentLocation.Room;
            cellInput.Text = currentLocation.Cell;
            padInput.Text = currentLocation.Pad;
            nameInput.Text = currentLocation.Map + (string.IsNullOrWhiteSpace(currentLocation.Room) ? "" : "-" + currentLocation.Room);
        }

        panel.Children.Add(new TextBlock { Text = "Preset name" });
        panel.Children.Add(nameInput);
        panel.Children.Add(new TextBlock { Text = "Map" });
        panel.Children.Add(mapInput);
        panel.Children.Add(new TextBlock { Text = "Room number (optional)" });
        panel.Children.Add(roomInput);
        panel.Children.Add(new TextBlock { Text = "Cell" });
        panel.Children.Add(cellInput);
        panel.Children.Add(new TextBlock { Text = "Pad" });
        panel.Children.Add(padInput);

        Button saveCurrentButton = new Button { Content = "Use Current Client Location", HorizontalAlignment = HorizontalAlignment.Left };
        saveCurrentButton.Click += (_, _) =>
        {
            if (InstancesTabControl.SelectedItem is not TabItem selectedTab
                || !_tabs.TryGetValue(selectedTab, out TabInfo? selectedInfo)
                || selectedInfo.Process == null
                || !_latestClientLocations.TryGetValue(selectedInfo.Process.Id, out ArmyClientLocation? location)
                || location == null
                || location.UpdatedUtc < DateTime.UtcNow.AddSeconds(-5)
                || string.IsNullOrWhiteSpace(location.Map))
            {
                Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("The selected client has not reported a current location yet. Log in and wait a moment, then try again.", "Current Location");
                return;
            }

            mapInput.Text = location.Map;
            roomInput.Text = location.Room;
            cellInput.Text = location.Cell;
            padInput.Text = location.Pad;
            if (string.IsNullOrWhiteSpace(nameInput.Text))
                nameInput.Text = location.Map + (string.IsNullOrWhiteSpace(location.Room) ? "" : "-" + location.Room);
        };
        panel.Children.Add(saveCurrentButton);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button saveButton = new Button { Content = "Save Preset", MinWidth = 100, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        saveButton.Click += (_, _) =>
        {
            string name = nameInput.Text?.Trim() ?? "";
            string map = mapInput.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(map))
            {
                Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("Enter both a preset name and map name.", "Create Map Preset");
                return;
            }

            ArmyJumpDestination preset = existingPreset ?? new ArmyJumpDestination();
            preset.Name = name;
            preset.Map = map;
            preset.Room = roomInput.Text?.Trim() ?? "";
            preset.Cell = string.IsNullOrWhiteSpace(cellInput.Text) ? "Enter" : cellInput.Text.Trim();
            preset.Pad = string.IsNullOrWhiteSpace(padInput.Text) ? "Spawn" : padInput.Text.Trim();

            _jumpMapPresets.RemoveAll(item => !ReferenceEquals(item, preset) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existingPreset == null)
                _jumpMapPresets.Add(preset);
            SaveArmyJumpSettings();
            dialog.Close(true);
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(saveButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog<bool?>(this);
    }

    private async void MenuItem_JumpMapPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (_jumpMapPresets.Count == 0)
        {
            Ioc.Default.GetRequiredService<IDialogService>().ShowMessageBox("No map presets saved yet. Use Save Current Map as Preset to add one.", "Map Presets");
            return;
        }

        Window dialog = new Window
        {
            Title = "Jump to Map Preset",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select a saved map, room and cell preset:", TextWrapping = TextWrapping.Wrap });

        List<ArmyJumpDestination> presets = _jumpMapPresets.ToList();
        ComboBox presetPicker = new ComboBox
        {
            ItemsSource = presets.Select(item => $"{item.Name} — {item.DisplayName}").ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(presetPicker);

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button jumpButton = new Button { Content = "Jump", MinWidth = 84, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        jumpButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(jumpButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        if (confirmed != true || presetPicker.SelectedIndex < 0 || presetPicker.SelectedIndex >= presets.Count)
            return;

        JumpArmyToDestination(presets[presetPicker.SelectedIndex]);
    }

    private async void MenuItem_ManageJumpMapPresets_Click(object? sender, RoutedEventArgs e)
    {
        Window dialog = new Window
        {
            Title = "Manage Map Presets",
            Width = 600,
            MinHeight = 280,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };

        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Create, inspect, edit, or delete saved map presets.", TextWrapping = TextWrapping.Wrap });

        ListBox presetList = new ListBox
        {
            ItemsSource = _jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}").ToList(),
            SelectedIndex = _jumpMapPresets.Count > 0 ? 0 : -1,
            MinHeight = 100,
            MaxHeight = 320
        };
        panel.Children.Add(presetList);

        TextBlock destinationText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void UpdateDestination()
        {
            int index = presetList.SelectedIndex;
            destinationText.Text = index >= 0 && index < _jumpMapPresets.Count
                ? $"Destination: {_jumpMapPresets[index].DisplayName}"
                : "No preset selected.";
        }

        presetList.SelectionChanged += (_, _) => UpdateDestination();
        panel.Children.Add(destinationText);
        UpdateDestination();

        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button createButton = new Button { Content = "Create Preset...", MinWidth = 110 };
        Button editButton = new Button { Content = "Edit Selected...", MinWidth = 110, IsEnabled = _jumpMapPresets.Count > 0 };
        Button deleteButton = new Button { Content = "Delete Selected", MinWidth = 120, IsEnabled = _jumpMapPresets.Count > 0 };
        Button closeButton = new Button { Content = "Close", MinWidth = 84, IsCancel = true };

        void RefreshPresets(int selectedIndex)
        {
            presetList.ItemsSource = _jumpMapPresets.Select(item => $"{item.Name} — {item.DisplayName}").ToList();
            presetList.SelectedIndex = _jumpMapPresets.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, _jumpMapPresets.Count - 1);
            editButton.IsEnabled = _jumpMapPresets.Count > 0;
            deleteButton.IsEnabled = _jumpMapPresets.Count > 0;
            UpdateDestination();
        }

        createButton.Click += async (_, _) =>
        {
            dialog.Hide();
            await OpenJumpMapPresetEditor(null);
            dialog.Show();
            RefreshPresets(_jumpMapPresets.Count - 1);
        };
        editButton.Click += async (_, _) =>
        {
            int index = presetList.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            ArmyJumpDestination selectedPreset = _jumpMapPresets[index];
            dialog.Hide();
            await OpenJumpMapPresetEditor(selectedPreset);
            dialog.Show();
            RefreshPresets(index);
        };
        deleteButton.Click += (_, _) =>
        {
            int index = presetList.SelectedIndex;
            if (index < 0 || index >= _jumpMapPresets.Count)
                return;

            _jumpMapPresets.RemoveAt(index);
            SaveArmyJumpSettings();
            RefreshPresets(index);
        };
        closeButton.Click += (_, _) => dialog.Close();
        buttons.Children.Add(createButton);
        buttons.Children.Add(editButton);
        buttons.Children.Add(deleteButton);
        buttons.Children.Add(closeButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog(this);
    }

    private async void MenuItem_JumpPlayer_Click(object? sender, RoutedEventArgs e)
    {
        SyncTabTitles();
        List<string> players = _tabs
            .Select(entry => GetTabLoggedInUsername(entry.Key, entry.Value, GetTabTitle(entry.Key)))
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Select(username => username!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (players.Count == 0)
        {
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox("No logged-in player names were found in the open client tabs. Log in to at least one tab and try again.", "Jump All to Player");
            return;
        }

        Window dialog = new Window
        {
            Title = "Jump Army to Player",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = this.Background
        };
        StackPanel panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Select a player from the open client tabs:", TextWrapping = TextWrapping.Wrap });
        string initialPlayer = players.FirstOrDefault(player => player.Equals(_lastJumpPlayer, StringComparison.OrdinalIgnoreCase)) ?? players[0];
        ComboBox playerPicker = new ComboBox
        {
            ItemsSource = players,
            SelectedItem = initialPlayer,
            Text = initialPlayer,
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(playerPicker);
        StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button cancelButton = new Button { Content = "Cancel", MinWidth = 84 };
        Button jumpButton = new Button { Content = "Jump", MinWidth = 84, IsDefault = true };
        cancelButton.Click += (_, _) => dialog.Close(false);
        jumpButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(jumpButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        bool? confirmed = await dialog.ShowDialog<bool?>(this);
        string targetPlayer = playerPicker.Text?.Trim() ?? "";
        if (confirmed != true || string.IsNullOrWhiteSpace(targetPlayer))
            return;

        _lastJumpPlayer = targetPlayer;
        SaveArmyJumpSettings();
        string tempFile = TabHostFiles.JumpPlayer;
        File.WriteAllText(tempFile, targetPlayer);

        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostMessage(info.ChildHwnd, WM_SKUA_JUMP_PLAYER, IntPtr.Zero, IntPtr.Zero);
    }

    private string GetTabTitle(TabItem tab)
    {
        if (tab.Header is StackPanel headerPanel)
            return headerPanel.Children.OfType<TextBlock>().FirstOrDefault(block => block.Text != "✕")?.Text ?? "";
        return tab.Header?.ToString() ?? "";
    }

    // Each client joins its own house (no input needed).
    private void MenuItem_JumpHouse_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var info in _tabs.Values)
            if (info.ChildHwnd != IntPtr.Zero)
                PostMessage(info.ChildHwnd, WM_SKUA_JUMP_HOUSE, IntPtr.Zero, IntPtr.Zero);
    }
}
