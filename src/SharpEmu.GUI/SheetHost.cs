// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace SharpEmu.GUI;

// Shows SheetViews over the launcher content, each on its own scrim layer. Only the top
// layer takes input: the content under the first sheet and every lower sheet are disabled,
// and Tab cycles inside the top card. Escape asks the top sheet to cancel.
public sealed class SheetHost : Panel
{
    private readonly List<Layer> _layers = [];

    public SheetHost()
    {
        IsVisible = false;
    }

    // The content the sheets cover; disabled while any sheet is open.
    public Control? Covered { get; set; }

    public SheetView? Top => _layers.Count == 0 ? null : _layers[^1].Sheet;

    public Task<object?> ShowAsync(SheetView sheet)
    {
        var card = new Border
        {
            Classes = { "sheetCard" },
            MaxWidth = sheet.SheetWidth,
            Child = sheet,
        };
        if (sheet.IsPopover)
        {
            card.Classes.Add("popover");
            card.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            card.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            card.Margin = sheet.PopoverMargin;
        }
        else if (double.IsNaN(sheet.SheetHeight))
        {
            card.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        }
        else
        {
            card.MaxHeight = sheet.SheetHeight;
        }

        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);
        var scrim = new Border { Classes = { "sheetScrim" } };
        if (sheet.IsPopover)
        {
            scrim.Classes.Add("popover");
            scrim.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                sheet.RequestCancel();
            };
        }

        var root = new Grid
        {
            Classes = { "sheetLayer" },
            Children = { scrim, card },
        };
        root.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && !args.Handled)
            {
                args.Handled = true;
                sheet.RequestCancel();
            }
        };

        var layer = new Layer(sheet, root, TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement());
        SetBelowEnabled(false);
        _layers.Add(layer);
        Children.Add(root);
        IsVisible = true;
        sheet.Host = this;
        sheet.IsOpen = true;
        sheet.RaiseOpened();

        // Focus waits for the first layout pass, when the sheet's controls can take it.
        Dispatcher.UIThread.Post(() =>
        {
            if (sheet.IsOpen)
            {
                (sheet.InitialFocus ?? FocusManager.FindFirstFocusableElement(sheet))?.Focus();
            }
        }, DispatcherPriority.Loaded);
        return layer.Result.Task;
    }

    internal void Dismiss(SheetView sheet, object? result)
    {
        var index = _layers.FindIndex(layer => ReferenceEquals(layer.Sheet, sheet));
        if (index < 0)
        {
            return;
        }

        // A sheet takes the sheets it opened down with it.
        while (_layers.Count > index + 1)
        {
            Dismiss(_layers[^1].Sheet, null);
        }

        var layer = _layers[index];
        _layers.RemoveAt(index);
        Children.Remove(layer.Root);
        // Free the sheet from its card so a kept instance (the library menu) can open again.
        if (sheet.Parent is Border card)
        {
            card.Child = null;
        }

        sheet.IsOpen = false;
        sheet.Host = null;
        SetBelowEnabled(true);
        IsVisible = _layers.Count > 0;
        layer.FocusBefore?.Focus();
        sheet.RaiseClosed();
        layer.Result.TrySetResult(result);
    }

    public void CloseAll()
    {
        while (_layers.Count > 0)
        {
            Dismiss(_layers[^1].Sheet, null);
        }
    }

    private void SetBelowEnabled(bool enabled)
    {
        if (_layers.Count > 0)
        {
            _layers[^1].Root.IsEnabled = enabled;
        }
        else if (Covered is not null)
        {
            Covered.IsEnabled = enabled;
        }
    }

    private sealed record Layer(SheetView Sheet, Grid Root, IInputElement? FocusBefore)
    {
        public TaskCompletionSource<object?> Result { get; } = new();
    }
}
