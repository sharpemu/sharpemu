// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;

namespace SharpEmu.GUI;

// A modal page shown inside the launcher window instead of a separate window, so a
// controller never has to chase focus into a new window. SheetHost shows it over a scrim;
// Close hands the result back to whoever awaited SheetHost.ShowAsync.
public class SheetView : UserControl
{
    // The size the sheet asks for; the host shrinks it to fit a small window.
    // A NaN height lets the sheet size to its content.
    public double SheetWidth { get; set; } = 560;

    public double SheetHeight { get; set; } = double.NaN;

    // A popover hangs from the control that opened it (PopoverMargin from the host's top
    // right) over a light scrim that closes it when clicked, instead of centring on a dark one.
    public bool IsPopover { get; set; }

    public Thickness PopoverMargin { get; set; }

    internal SheetHost? Host { get; set; }

    public bool IsOpen { get; internal set; }

    // The control focused when the sheet opens; null focuses the first focusable control.
    public virtual Control? InitialFocus => null;

    public event EventHandler? Opened;

    public event EventHandler? Closed;

    public void Close(object? result = null) => Host?.Dismiss(this, result);

    // Escape or Circle. Sheets with work in flight override this to cancel it first.
    public virtual void RequestCancel() => Close();

    internal void RaiseOpened() => Opened?.Invoke(this, EventArgs.Empty);

    internal void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);

    // Shows a nested sheet over this one.
    protected Task<object?> ShowSheetAsync(SheetView sheet) =>
        Host?.ShowAsync(sheet) ?? Task.FromResult<object?>(null);
}
