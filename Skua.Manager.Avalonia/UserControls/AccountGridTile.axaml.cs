using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Skua.Core.Interfaces.ViewModels;

namespace Skua.Manager.Avalonia.UserControls;

public partial class AccountGridTile : UserControl
{
    public AccountGridTile()
    {
        InitializeComponent();
    }

    private void Border_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;

        if (sender is not Border border || border.DataContext is not IAccountItemViewModel account)
            return;

        if (IsFromInteractiveControl(e.Source, border))
            return;

        account.ToggleSelectionCommand.Execute(null);
        e.Handled = true;
    }

    private async void CopyUsername_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is IAccountItemViewModel account && !string.IsNullOrEmpty(account.Username))
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(account.Username);
            }
        }
    }

    private static bool IsFromInteractiveControl(object? source, Border boundary)
    {
        if (source is not global::Avalonia.StyledElement sourceElement)
            return false;

        global::Avalonia.StyledElement? current = sourceElement;
        while (current is not null && !ReferenceEquals(current, boundary))
        {
            if (current is Button || current is MenuItem || current is CheckBox)
                return true;

            current = current.Parent;
        }

        return false;
    }
}
