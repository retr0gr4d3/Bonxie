using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Skua.App.Avalonia.ViewModels.AdvancedSkills;
using Skua.App.Avalonia.ViewModels.Dialogs;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Models;
using Skua.Shared.Avalonia.Controls;
using Skua.Shared.Avalonia.Controls.Options;
using Skua.Shared.Avalonia.Controls.Shell;
using Skua.Shared.Avalonia.ViewModels.Dialogs;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Shared.Avalonia.ViewModels.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CustomDialogViewModel = Skua.Shared.Avalonia.ViewModels.Dialogs.CustomDialogViewModel;

namespace Skua.App.Avalonia.Services;

public class DialogService : Skua.Shared.Avalonia.Services.DialogServiceBase
{
    protected override bool? ShowDialogInternal<TViewModel>(TViewModel viewModel, string title) where TViewModel : class
    {
        return viewModel switch
        {
            OptionContainerViewModel options => ShowOptionContainerDialog(options, string.IsNullOrWhiteSpace(title) || title == nameof(OptionContainerViewModel) ? options.Title : title),
            IOptionContainerViewModel optionsVm => ShowOptionContainerDialog(optionsVm as OptionContainerViewModel ?? new OptionContainerViewModel(optionsVm.Container), title),
            InputDialogViewModel input => ShowInputDialog(input, title),
            AssignHotKeyDialogViewModel hotKey => ShowAssignHotKeyDialog(hotKey, title),
            SkillRuleEditorDialogViewModel skillRules => ShowSkillRuleEditorDialog(skillRules, title),
            MessageBoxDialogViewModel messageBox => ShowMessageBoxInternal(messageBox.Message, messageBox.Title, messageBox.YesAndNo),
            CustomDialogViewModel custom => ShowCustomDialog(custom, title),
            _ => ShowFallbackDialog(viewModel.GetType().Name, title)
        };
    }

    private bool? ShowInputDialog(InputDialogViewModel vm, string title)
    {
        Window dialog = CreateBaseDialog(title);
        TextBox input = new() { Text = vm.DialogTextInput, Watermark = vm.TextBoxHint };
        if (vm.NumberOnly)
        {
            input.TextChanged += (_, _) =>
            {
                string digitsOnly = new(input.Text?.Where(char.IsDigit).ToArray() ?? Array.Empty<char>());
                if (input.Text != digitsOnly)
                {
                    int caret = Math.Min(input.CaretIndex, digitsOnly.Length);
                    input.Text = digitsOnly;
                    input.CaretIndex = caret;
                }
            };
        }

        bool? result = null;
        Button ok = new() { Content = "OK", MinWidth = 84, Classes = { "skua-button", "skua-primary" } };
        Button cancel = new() { Content = "Cancel", MinWidth = 84, Classes = { "skua-button" }, IsCancel = true };
        ok.IsDefault = true;

        ok.Click += (_, _) =>
        {
            vm.DialogTextInput = input.Text ?? string.Empty;
            result = true;
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            result = false;
            dialog.Close();
        };

        StackPanel content = new()
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = vm.DialogHint, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };

        dialog.Content = WrapWithShell(title, content);
        Show(dialog);
        return result;
    }

    private bool? ShowAssignHotKeyDialog(AssignHotKeyDialogViewModel vm, string title)
    {
        Window dialog = CreateBaseDialog(string.IsNullOrWhiteSpace(title) ? "Assign HotKey" : title);
        dialog.Width = 440;

        CheckBox ctrl = new() { Content = "Ctrl", IsChecked = vm.CtrlCheck };
        CheckBox shift = new() { Content = "Shift", IsChecked = vm.ShiftCheck };
        CheckBox alt = new() { Content = "Alt", IsChecked = vm.AltCheck };

        StackPanel modifiersPanel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { ctrl, shift, alt }
        };

        TextBox keyInput = new()
        {
            Text = vm.KeyInput,
            IsReadOnly = true,
            FontFamily = new FontFamily("JetBrains Mono,Consolas,Courier New,monospace"),
            Watermark = "Click 'Capture Key' or press a key"
        };

        Button captureBtn = new()
        {
            Content = "Capture Key",
            Classes = { "skua-button" },
            MinWidth = 100
        };

        TextBlock hintLabel = new()
        {
            Text = string.Empty,
            Foreground = new SolidColorBrush(Color.Parse("#EF4444")),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        bool capturing = string.IsNullOrWhiteSpace(vm.KeyInput);
        if (capturing)
        {
            keyInput.Text = "Press any key...";
            hintLabel.Text = "Press any key (Esc to cancel)";
        }

        void StartCapture()
        {
            capturing = true;
            keyInput.Text = "Press any key...";
            hintLabel.Text = "Press any key (Esc to cancel)";
            dialog.Focus();
        }

        captureBtn.Click += (_, _) => StartCapture();
        keyInput.PointerPressed += (_, _) => StartCapture();

        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!capturing) return;

            if (e.Key == Key.Escape)
            {
                capturing = false;
                keyInput.Text = vm.KeyInput;
                hintLabel.Text = string.Empty;
                e.Handled = true;
                return;
            }

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is Key.LeftCtrl or Key.RightCtrl)
                ctrl.IsChecked = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Key is Key.LeftShift or Key.RightShift)
                shift.IsChecked = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.Key is Key.LeftAlt or Key.RightAlt)
                alt.IsChecked = true;

            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
            {
                hintLabel.Text = "Modifier held. Now press a key...";
                e.Handled = true;
                return;
            }

            string keyName = e.Key switch
            {
                Key.Divide => "Divide",
                Key.Multiply => "Multiply",
                Key.Subtract => "Subtract",
                Key.Add => "Add",
                Key.Decimal => "Decimal",
                _ => e.Key.ToString()
            };
            keyInput.Text = keyName;
            capturing = false;
            hintLabel.Text = string.Empty;
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        Button ok = new() { Content = "OK", MinWidth = 84, Classes = { "skua-button", "skua-primary" } };
        Button cancel = new() { Content = "Cancel", MinWidth = 84, Classes = { "skua-button" }, IsCancel = true };
        ok.IsDefault = true;

        bool? result = null;
        ok.Click += (_, _) =>
        {
            string chosenKey = keyInput.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(chosenKey) || chosenKey == "Press any key...")
            {
                hintLabel.Text = "Please assign a valid key.";
                return;
            }

            bool isCtrl = ctrl.IsChecked == true;
            bool isShift = shift.IsChecked == true;
            bool isAlt = alt.IsChecked == true;

            string gesture = $"{(isCtrl ? "Ctrl+" : "")}{(isShift ? "Shift+" : "")}{(isAlt ? "Alt+" : "")}{chosenKey}";
            if (vm.UsedGestures.Any(g => string.Equals(g, gesture, StringComparison.OrdinalIgnoreCase)))
            {
                hintLabel.Text = "This hotkey is already assigned to another action.";
                return;
            }

            vm.CtrlCheck = isCtrl;
            vm.ShiftCheck = isShift;
            vm.AltCheck = isAlt;
            vm.KeyInput = chosenKey;
            result = true;
            dialog.Close();
        };

        cancel.Click += (_, _) =>
        {
            result = false;
            dialog.Close();
        };

        Grid keyRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                keyInput,
                captureBtn
            }
        };
        Grid.SetColumn(keyInput, 0);
        Grid.SetColumn(captureBtn, 1);
        captureBtn.Margin = new Thickness(8, 0, 0, 0);

        StackPanel content = new()
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Modifiers:", FontWeight = FontWeight.SemiBold, FontSize = 12 },
                modifiersPanel,
                new TextBlock { Text = "Assigned Key:", FontWeight = FontWeight.SemiBold, FontSize = 12 },
                keyRow,
                hintLabel,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };

        dialog.Content = WrapWithShell(string.IsNullOrWhiteSpace(title) ? "Assign HotKey" : title, content);
        Show(dialog);
        return result;
    }

    private bool? ShowSkillRuleEditorDialog(SkillRuleEditorDialogViewModel vm, string title)
    {
        Window dialog = CreateBaseDialog(string.IsNullOrWhiteSpace(title) ? "Edit Rules" : title);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.Width = 640;
        dialog.Height = 600;

        SkillRulesViewModel scratch = new(vm.UseRules);

        PropertyGridControl propertyGrid = new()
        {
            SelectedObject = scratch,
            MinHeight = 350
        };

        TextBlock help = new()
        {
            Text = "Edit rule values then click Confirm. Multi-aura entries can be managed in the main Skills panel.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        help[!TextBlock.ForegroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaMutedTextBrush");

        Button confirm = new() { Content = "Confirm", MinWidth = 84, Classes = { "skua-button", "skua-primary" } };
        Button cancel = new() { Content = "Cancel", MinWidth = 84, Classes = { "skua-button" }, IsCancel = true };
        confirm.IsDefault = true;

        bool? result = null;
        confirm.Click += (_, _) =>
        {
            CopySkillRules(scratch, vm.UseRules);
            result = true;
            dialog.Close();
        };

        cancel.Click += (_, _) =>
        {
            result = false;
            dialog.Close();
        };

        Grid content = new();
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        content.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        content.Margin = new Thickness(16);

        Grid.SetRow(help, 0);
        Grid.SetRow(propertyGrid, 1);

        StackPanel buttonPanel = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, confirm }
        };
        Grid.SetRow(buttonPanel, 2);

        content.Children.Add(help);
        content.Children.Add(propertyGrid);
        content.Children.Add(buttonPanel);

        dialog.Content = WrapWithShell(string.IsNullOrWhiteSpace(title) ? "Edit Rules" : title, content);
        Show(dialog);
        return result;
    }

    private string GetCurrentAccountName()
    {
        try
        {
            var bot = Ioc.Default.GetService<IScriptInterface>();
            if (bot?.Player != null && !string.IsNullOrWhiteSpace(bot.Player.Username) && bot.Player.Username != "loginInfo.strUsername")
                return bot.Player.Username.Trim();

            if (bot?.Servers != null && !string.IsNullOrWhiteSpace(bot.Servers.CachedUsername))
                return bot.Servers.CachedUsername.Trim();

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if ((args[i] == "-u" || args[i] == "--user") && !string.IsNullOrWhiteSpace(args[i + 1]))
                    return args[i + 1].Trim();
            }
        }
        catch { }
        return string.Empty;
    }

    private string ResolveOptionContainerTitle(string? title)
    {
        string accountName = GetCurrentAccountName();
        if (string.IsNullOrWhiteSpace(title) || title == "Script Options" || title == nameof(OptionContainerViewModel))
        {
            return string.IsNullOrWhiteSpace(accountName) ? "Script Options" : $"Script Options - {accountName}";
        }

        if (!string.IsNullOrWhiteSpace(accountName) && !title.Contains(accountName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{title} - {accountName}";
        }

        return title;
    }

    protected override bool? ShowOptionContainerDialog(OptionContainerViewModel vm, string title)
    {
        string resolvedTitle = ResolveOptionContainerTitle(title);
        Window dialog = CreateBaseDialog(resolvedTitle);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.Width = 560;
        dialog.MinWidth = 460;
        dialog.MinHeight = 420;
        dialog.CanResize = true;

        double maxScreenHeight = 720;
        try
        {
            var screen = Skua.Shared.Avalonia.Services.WindowPlacementHelper.GetTargetScreen(dialog);
            if (screen != null)
                maxScreenHeight = Math.Max(420, screen.WorkingArea.Height * 0.85);
        }
        catch { }
        dialog.MaxHeight = maxScreenHeight;
        dialog.Height = Math.Min(560, maxScreenHeight);

        OptionContainerUserControl content = new()
        {
            DataContext = vm
        };

        Button saveBtn = new()
        {
            Content = "Save",
            MinWidth = 84,
            Classes = { "skua-button", "skua-primary" },
            IsDefault = true
        };

        Button cancelBtn = new()
        {
            Content = "Cancel",
            MinWidth = 84,
            Classes = { "skua-button" },
            IsCancel = true
        };

        Button resetBtn = new()
        {
            Content = "Reset Defaults",
            Classes = { "skua-button" }
        };

        bool? result = null;

        saveBtn.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        cancelBtn.Click += (_, _) =>
        {
            result = false;
            dialog.Close();
        };

        resetBtn.Click += (_, _) =>
        {
            vm.ResetAllToDefaults();
        };

        Grid rootLayout = new();
        rootLayout.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        rootLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        Grid.SetRow(content, 0);
        rootLayout.Children.Add(content);

        Grid buttonPanel = new()
        {
            Margin = new Thickness(12, 0, 12, 12),
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };

        buttonPanel.Children.Add(resetBtn);
        Grid.SetColumn(resetBtn, 0);

        StackPanel actionButtons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelBtn, saveBtn }
        };
        buttonPanel.Children.Add(actionButtons);
        Grid.SetColumn(actionButtons, 1);

        Grid.SetRow(buttonPanel, 1);
        rootLayout.Children.Add(buttonPanel);

        dialog.Content = WrapWithShell(resolvedTitle, rootLayout);
        Show(dialog);
        return result;
    }

    private bool? ShowFallbackDialog(string typeName, string title)
    {
        _ = ShowButtonsDialog(title, $"Dialog view: {typeName}", ["OK"]);
        return true;
    }

    private bool? ShowMessageBoxInternal(string message, string caption, bool yesAndNo)
    {
        string[] buttons = yesAndNo ? ["No", "Yes"] : ["OK"];
        string clicked = ShowButtonsDialog(caption, message, buttons);
        return clicked == "Yes" || clicked == "OK";
    }

    protected override string ShowButtonsDialog(string title, string message, string[] buttons)
    {
        Window dialog = CreateBaseDialog(title);
        dialog.Width = (message.Length > 200 || message.Contains('\n')) ? 560 : 460;
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.CanResize = true;

        double maxScreenHeight = 720;
        var screen = Skua.Shared.Avalonia.Services.WindowPlacementHelper.GetTargetScreen(dialog);
        if (screen != null)
            maxScreenHeight = Math.Max(160, screen.WorkingArea.Height / screen.Scaling * 0.85);
        dialog.MinHeight = 160;
        dialog.MaxHeight = maxScreenHeight;

        SelectableTextBlock label = new()
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20
        };
        label.Measure(new Size(dialog.Width - 34, double.PositiveInfinity));
        dialog.Height = Math.Min(maxScreenHeight, Math.Max(dialog.MinHeight, label.DesiredSize.Height + 112));

        ScrollViewer scrollViewer = new()
        {
            Content = label,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        Button copyButton = new()
        {
            Content = "Copy",
            Classes = { "skua-button", "w84" }
        };
        copyButton.Click += async (_, _) =>
        {
            if (dialog.Clipboard != null)
            {
                await dialog.Clipboard.SetTextAsync(message);
                copyButton.Content = "Copied!";
            }
        };

        StackPanel buttonPanel = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        string clicked = string.Empty;
        for (int i = 0; i < buttons.Length; i++)
        {
            string text = buttons[i];
            Button button = new()
            {
                Content = text,
                MinWidth = 84,
                Classes = { "skua-button" }
            };

            if (text == "OK" || text == "Yes")
            {
                button.Classes.Add("skua-primary");
                button.IsDefault = true;
            }
            else if (text == "Cancel" || text == "No")
            {
                button.IsCancel = true;
            }

            button.Click += (_, _) =>
            {
                clicked = text;
                dialog.Close();
            };
            buttonPanel.Children.Add(button);
        }

        Grid footerPanel = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        Grid.SetColumn(copyButton, 0);
        Grid.SetColumn(buttonPanel, 1);
        footerPanel.Children.Add(copyButton);
        footerPanel.Children.Add(buttonPanel);

        Grid content = new()
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 16
        };
        Grid.SetRow(scrollViewer, 0);
        Grid.SetRow(footerPanel, 1);
        content.Children.Add(scrollViewer);
        content.Children.Add(footerPanel);

        dialog.Content = WrapWithShell(title, content);
        Show(dialog);
        return clicked;
    }

    private void CopySkillRules(SkillRulesViewModel source, SkillRulesViewModel target)
    {
        target.UseRuleBool = source.UseRuleBool;
        target.MultiAuraBool = source.MultiAuraBool;
        target.HealthGreaterThanBool = source.HealthGreaterThanBool;
        target.HealthUseValue = source.HealthUseValue;
        target.HealthIsPercentage = source.HealthIsPercentage;
        target.ManaGreaterThanBool = source.ManaGreaterThanBool;
        target.ManaUseValue = source.ManaUseValue;
        target.ManaIsPercentage = source.ManaIsPercentage;
        target.WaitUseValue = source.WaitUseValue;
        target.SkipUseBool = source.SkipUseBool;
        target.AuraGreaterThanBool = source.AuraGreaterThanBool;
        target.AuraUseValue = source.AuraUseValue;
        target.AuraTargetIndex = source.AuraTargetIndex;
        target.AuraName = source.AuraName;
        target.PartyMemberHealthGreaterThanBool = source.PartyMemberHealthGreaterThanBool;
        target.PartyMemberHealthUseValue = source.PartyMemberHealthUseValue;
        target.PartyMemberHealthIsPercentage = source.PartyMemberHealthIsPercentage;
        target.MultiAuraOperatorIndex = source.MultiAuraOperatorIndex;

        target.MultiAuraChecks.Clear();
        foreach (AuraCheckViewModel check in source.MultiAuraChecks)
            target.MultiAuraChecks.Add(new AuraCheckViewModel(check));
    }
}
