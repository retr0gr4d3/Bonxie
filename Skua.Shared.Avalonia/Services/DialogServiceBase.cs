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
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Models;
using Skua.Shared.Avalonia.Controls.Shell;
using Skua.Shared.Avalonia.ViewModels.Dialogs;
using Skua.Shared.Avalonia.ViewModels.Options;
using System;
using System.Linq;
using System.Threading;
using CustomDialogViewModel = Skua.Shared.Avalonia.ViewModels.Dialogs.CustomDialogViewModel;

namespace Skua.Shared.Avalonia.Services;

/// <summary>
/// The part of <see cref="IDialogService"/> that the client and the manager share: marshalling to the UI thread, the
/// message-box entry points, and the window shell and modal loop every dialog uses. Each app supplies the dialogs it
/// knows how to draw.
/// </summary>
public abstract class DialogServiceBase : IDialogService
{
    /// <summary>Shows the dialog for <paramref name="viewModel"/>. Called on the UI thread.</summary>
    protected abstract bool? ShowDialogInternal<TViewModel>(TViewModel viewModel, string title) where TViewModel : class;

    /// <summary>Shows the options editor. Called on the UI thread.</summary>
    protected abstract bool? ShowOptionContainerDialog(OptionContainerViewModel vm, string title);

    /// <summary>Shows a message with buttons and returns the text of the one clicked (empty when closed).</summary>
    protected abstract string ShowButtonsDialog(string title, string message, string[] buttons);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowDialog(viewModel));
        return ShowDialogInternal(viewModel, DefaultTitle(viewModel));
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string title) where TViewModel : class
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowDialog(viewModel, title));
        return ShowDialogInternal(viewModel, title);
    }

    // A dialog shown without a title uses its own (DialogViewModelBase.Title) instead of the view model's type name.
    protected static string DefaultTitle(object viewModel)
    {
        return viewModel is DialogViewModelBase { Title: { Length: > 0 } title } ? title : viewModel.GetType().Name;
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowDialog(viewModel, callback));
        bool? result = ShowDialogInternal(viewModel, DefaultTitle(viewModel));
        if (result == true)
            callback?.Invoke(viewModel);
        return result;
    }

    public void ShowDialog(IOptionContainer optionContainer, Action<IOptionContainerViewModel> callback)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => ShowDialog(optionContainer, callback));
            return;
        }
        OptionContainerViewModel vm = new(optionContainer);
        bool? result = ShowOptionContainerDialog(vm, "Script Options");
        if (result == true)
        {
            callback?.Invoke(vm);
        }
    }

    public void ShowMessageBox(string message, string caption)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => ShowMessageBox(message, caption));
            return;
        }
        _ = ShowButtonsDialog(caption, message, ["OK"]);
    }

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowMessageBox(message, caption, yesAndNo));
        string[] buttons = yesAndNo ? ["No", "Yes"] : ["OK"];
        string clicked = ShowButtonsDialog(caption, message, buttons);
        return clicked == "Yes" || clicked == "OK";
    }

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.Invoke(() => ShowMessageBox(message, caption, buttons));
        string[] actualButtons = buttons.Length == 0 ? ["OK"] : buttons;
        string clicked = ShowButtonsDialog(caption, message, actualButtons);
        int index = Array.IndexOf(actualButtons, clicked);
        return index < 0 ? DialogResult.Cancelled : new DialogResult(clicked, index);
    }

    public IInputDialogViewModel CreateInputDialog(string title, string dialogHint)
    {
        return new InputDialogViewModel(title, dialogHint);
    }

    protected bool? ShowCustomDialog(CustomDialogViewModel vm, string title)
    {
        string clicked = ShowButtonsDialog(title, vm.Message, vm.Buttons.ToArray());
        int index = vm.Buttons.IndexOf(clicked);
        vm.Result = index < 0 ? DialogResult.Cancelled : new DialogResult(clicked, index);
        return index >= 0;
    }

    protected static Window CreateBaseDialog(string title)
    {
        Window dialog = new()
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            SystemDecorations = SystemDecorations.BorderOnly,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
            ExtendClientAreaTitleBarHeightHint = 32,
            CanResize = false,
            ShowActivated = true,
            Classes = { "skua" }
        };
        dialog[!Window.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaPageBrush");
        Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(dialog);
        return dialog;
    }

    protected static Control WrapWithShell(string title, Control content)
    {
        Grid layout = new();
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        WindowTitleBar titleBar = new()
        {
            TitleText = title
        };

        Border contentHost = new()
        {
            Child = content
        };
        contentHost.Classes.Add("shell");
        contentHost[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaPageBrush");

        Grid.SetRow(titleBar, 0);
        Grid.SetRow(contentHost, 1);
        layout.Children.Add(titleBar);
        layout.Children.Add(contentHost);

        Border rootBorder = new()
        {
            BorderThickness = new Thickness(1),
            Child = layout,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 12,
                Spread = 2,
                Color = Color.FromArgb(140, 0, 0, 0)
            })
        };
        rootBorder[!Border.BorderBrushProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaBorderBrush");
        rootBorder[!Border.BackgroundProperty] = new global::Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SkuaPageBrush");

        return rootBorder;
    }

    protected static void Show(Window dialog)
    {
        Skua.Shared.Avalonia.Services.WindowPlacementHelper.CenterOnActiveScreen(dialog);

        Window? owner = GetOwner();
        if (owner != null)
        {
            ShowModalAndBlock(dialog, owner);
            return;
        }

        ShowAndBlock(dialog);
    }

    protected static Window? GetOwner()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        Window? activeVisible = desktop.Windows.FirstOrDefault(w => w.IsVisible && w.IsActive && w.Position.X > -10000);
        if (activeVisible != null)
            return activeVisible;

        Window? visible = desktop.Windows.LastOrDefault(w => w.IsVisible && w.Position.X > -10000);
        if (visible != null)
            return visible;

        return (desktop.MainWindow?.IsVisible == true && desktop.MainWindow.Position.X > -10000) ? desktop.MainWindow : null;
    }

    protected static void ShowModalAndBlock(Window dialog, Window owner)
    {
        using CancellationTokenSource closedCts = new();
        void OnClosed(object? _, EventArgs __) => closedCts.Cancel();
        void OnOpened(object? _, EventArgs __) => FocusDefaultDialogAction(dialog);

        dialog.Closed += OnClosed;
        dialog.Opened += OnOpened;
        _ = dialog.ShowDialog(owner);

        try
        {
            Dispatcher.UIThread.MainLoop(closedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when the dialog closes and cancels the nested loop.
        }
        finally
        {
            dialog.Closed -= OnClosed;
            dialog.Opened -= OnOpened;
        }
    }

    protected static void ShowAndBlock(Window dialog)
    {
        using CancellationTokenSource closedCts = new();
        void OnClosed(object? _, EventArgs __) => closedCts.Cancel();
        void OnOpened(object? _, EventArgs __) => FocusDefaultDialogAction(dialog);

        dialog.Closed += OnClosed;
        dialog.Opened += OnOpened;
        dialog.Show();

        try
        {
            Dispatcher.UIThread.MainLoop(closedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when the dialog closes and cancels the nested loop.
        }
        finally
        {
            dialog.Closed -= OnClosed;
            dialog.Opened -= OnOpened;
        }
    }

    protected static void FocusDefaultDialogAction(Window dialog)
    {
        dialog.Activate();
        dialog.Focus();
        dialog.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.IsDefault)?
            .Focus();
    }
}
