using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Skua.App.Avalonia.ViewModels.Packets;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Skua.App.Avalonia.Views;

public partial class PacketInterceptorView : UserControl
{
    private PacketInterceptorViewModel? _vm;
    private ObservableCollection<InterceptedPacketViewModel>? _packets;

    public PacketInterceptorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe(DataContext as PacketInterceptorViewModel);
    }

    private void Subscribe(PacketInterceptorViewModel? vm)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = vm;
        if (_vm is not null)
            _vm.PropertyChanged += Vm_PropertyChanged;
        WatchPackets();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PacketInterceptorViewModel.FilteredPackets))
            WatchPackets();
    }

    private void WatchPackets()
    {
        if (_packets is not null)
            _packets.CollectionChanged -= Packets_CollectionChanged;
        _packets = _vm?.FilteredPackets;
        if (_packets is not null)
            _packets.CollectionChanged += Packets_CollectionChanged;
        UpdateCountText();
    }

    private void Packets_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // After the ListBox has handled the change, so a cleared list also clears the selected count.
        Dispatcher.UIThread.Post(UpdateCountText, DispatcherPriority.Background);
    }

    private void PacketsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateCountText();
    }

    private void UpdateCountText()
    {
        int packets = _packets?.Count ?? 0;
        int selected = PacketsListBox.SelectedItems?.Count ?? 0;
        string text = packets == 1 ? "1 packet" : $"{packets:N0} packets";
        CountText.Text = selected > 0 ? $"{text} · {selected} selected" : text;
        UnselectButton.IsVisible = selected > 0;
    }

    private void UnselectAll_Click(object? sender, RoutedEventArgs e)
    {
        PacketsListBox.UnselectAll();
    }

    private async void PacketsListBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.C)
        {
            await CopyAsync();
            e.Handled = true;
        }
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        await CopyAsync();
    }

    // The selected packets, or every shown packet when none are selected.
    private async Task CopyAsync()
    {
        IEnumerable<InterceptedPacketViewModel> packets = PacketsListBox.SelectedItems is { Count: > 0 } items
            ? items.OfType<InterceptedPacketViewModel>()
            : _packets ?? Enumerable.Empty<InterceptedPacketViewModel>();
        string text = string.Join(Environment.NewLine, packets.Select(p => p.Packet));
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
