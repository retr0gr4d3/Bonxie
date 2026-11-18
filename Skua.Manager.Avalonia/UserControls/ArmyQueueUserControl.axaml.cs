using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Skua.Manager.Avalonia.ViewModels;
using System;
using System.ComponentModel;

namespace Skua.Manager.Avalonia.UserControls;

public partial class ArmyQueueUserControl : UserControl
{
    private ArmyQueueViewModel? _currentVm;
    private ArmyQueueStepViewModel? _draggedStep;
    private PointerPressedEventArgs? _dragPressedArgs;
    private Point _dragStartPoint;

    public ArmyQueueUserControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (s, e) => (DataContext as ArmyQueueViewModel)?.EnsureSelection();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm != null)
        {
            _currentVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _currentVm = DataContext as ArmyQueueViewModel;

        if (_currentVm != null)
        {
            _currentVm.PropertyChanged += OnViewModelPropertyChanged;
            _currentVm.EnsureSelection();
        }
    }

    private void OnSquadTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_currentVm?.IsRunning == true || sender is not Control control ||
            control.DataContext is not ArmyQueueStepViewModel step ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        _draggedStep = step;
        _dragPressedArgs = e;
        _dragStartPoint = e.GetPosition(control);
    }

    private async void OnSquadTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_currentVm?.IsRunning == true || _draggedStep == null || _dragPressedArgs == null ||
            sender is not Control control ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        Point currentPosition = e.GetPosition(control);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) < 5 &&
            Math.Abs(currentPosition.Y - _dragStartPoint.Y) < 5)
            return;

        ArmyQueueStepViewModel step = _draggedStep;
        PointerPressedEventArgs pressedArgs = _dragPressedArgs;
        _dragPressedArgs = null;
        DataTransfer data = new();
        data.Add(DataTransferItem.Create(StepDragFormat, step));
        await DragDrop.DoDragDropAsync(pressedArgs, data, DragDropEffects.Move);
        _draggedStep = null;
    }

    private static readonly DataFormat<ArmyQueueStepViewModel> StepDragFormat =
        DataFormat.CreateInProcessFormat<ArmyQueueStepViewModel>("ArmyQueueStep");

    private void OnSquadTabDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _currentVm?.IsRunning == false &&
                        e.DataTransfer.Contains(StepDragFormat) &&
                        _draggedStep is not null
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSquadTabDrop(object? sender, DragEventArgs e)
    {
        if (_currentVm?.IsRunning != false ||
            !e.DataTransfer.Contains(StepDragFormat) ||
            _draggedStep is not { } draggedStep ||
            sender is not Control control ||
            control.DataContext is not ArmyQueueStepViewModel targetStep)
            return;

        _currentVm.ReorderStep(draggedStep, targetStep);
        e.Handled = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArmyQueueViewModel.ShowHelpGuide))
        {
            // When guide is closed or opened, smoothly reset/snap scroll offset to top
            ContentScrollViewer?.SetValue(ScrollViewer.OffsetProperty, Vector.Zero);
        }
    }
}
