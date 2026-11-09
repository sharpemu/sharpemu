// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using SharpEmu.HLE.Host;

namespace SharpEmu.GUI;

public partial class MainWindow
{
    private SettingRow? _controllerFocusRow;

    private void InitializeController()
    {
        XYFocus.SetNavigationModes(this, XYFocusNavigationModes.Gamepad);
        AddHandler(PointerPressedEvent, (_, _) => UsePointerNavigation(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => UsePointerNavigation(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(
            KeyDownEvent,
            (_, e) =>
            {
                if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Tab or Key.Enter or Key.Escape)
                {
                    SetPadNavigation(true);
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void UsePointerNavigation()
    {
        MarkControllerFocus(null);
        SetPadNavigation(false);
    }

    /// <summary>
    /// True while the last input was a key or the pad. The library tiles use it (focus-visible
    /// does not reach list items) to show the full focus treatment instead of the selected ring.
    /// </summary>
    private void SetPadNavigation(bool active) => SetClass(this, "padNav", active);

    private void MarkControllerFocus(Control? target)
    {
        var row = target?.FindAncestorOfType<SettingRow>();
        if (row == _controllerFocusRow)
        {
            return;
        }

        _controllerFocusRow?.Classes.Remove("controllerFocus");
        _controllerFocusRow = row;
        row?.Classes.Add("controllerFocus");
    }

    private void ShowPageFromController(int index)
    {
        if (index == _activePageIndex)
        {
            return;
        }

        if (index == 2)
        {
            OpenTrophyHub();
        }
        else
        {
            SetActivePage(index);
        }
    }

    private Control? ControllerRoot() =>
        Sheets.Top is { } sheet ? sheet
        : _isGameSettingsOpen ? GameOptionsOverlay
        : _activePageIndex == 1 ? OptionsPage
        : _activePageIndex == 2 ? TrophyPageHost
        : null;

    private void PollControllerPage(in HostGamepadState pad, HostGamepadButtons pressed)
    {
        if (ControllerRoot() is not { } root)
        {
            return;
        }

        if (pressed == HostGamepadButtons.None && !PadUp(pad) && !PadDown(pad) && !PadLeft(pad) && !PadRight(pad))
        {
            _navUpNextAt = _navDownNextAt = _navLeftNextAt = _navRightNextAt = default;
            return;
        }

        EnsureControllerFocus(root);
        var now = Environment.TickCount64;
        if (ShouldNavigate(PadUp(pad), ref _navUpNextAt, now))
        {
            ControllerKey(root, Key.Up);
        }

        if (ShouldNavigate(PadDown(pad), ref _navDownNextAt, now))
        {
            ControllerKey(root, Key.Down);
        }

        if (ShouldNavigate(PadLeft(pad), ref _navLeftNextAt, now))
        {
            ControllerKey(root, Key.Left);
        }

        if (ShouldNavigate(PadRight(pad), ref _navRightNextAt, now))
        {
            ControllerKey(root, Key.Right);
        }

        if ((pressed & HostGamepadButtons.Cross) != 0)
        {
            ControllerKey(root, Key.Enter);
        }

        if ((pressed & HostGamepadButtons.Circle) != 0 && !ControllerKey(root, Key.Escape))
        {
            if (_isGameSettingsOpen)
            {
                MarkControllerFocus(null);
                CloseGameSettings();
            }
            else
            {
                ShowPageFromController(0);
            }
        }
    }

    private sealed record SettingsLayout(Control Root, Button[] Navigation, Control[] Panels, Func<int> Section, Action<int> SetSection, Button? Launch = null);

    private SettingsLayout? ControllerSettings() =>
        Sheets.Top is not null ? null
        : _isGameSettingsOpen
            ? new SettingsLayout(GameOptionsOverlay, GameOptionsNavigationButtons(), GameOptionsSectionPanels(), () => _gameOptionsSectionIndex, section => SetGameOptionsSection(section), GameOptionsLaunchButton)
            : _activePageIndex == 1
                ? new SettingsLayout(OptionsPage, OptionsNavigationButtons(), OptionsSectionPanels(), () => _optionsSectionIndex, SetOptionsSection)
                : null;

    private bool ControllerKey(Control root, Key key) =>
        ControllerSettings() is { } layout ? SettingsControllerKey(layout, key) : SendControllerKey(root, key);

    private bool SettingsControllerKey(SettingsLayout layout, Key key)
    {
        var focused = FocusManager?.GetFocusedElement() as Control;
        if (focused is not null && (focused is ComboBoxItem || (focused is ComboBox { IsDropDownOpen: true }) ||
                                    !layout.Root.IsVisualAncestorOf(focused) && focused != layout.Root))
        {
            return key is Key.Left or Key.Right || SendControllerKey(layout.Root, key);
        }

        var navigation = layout.Navigation;
        var panel = layout.Panels[Math.Clamp(layout.Section(), 0, layout.Panels.Length - 1)];

        // The Launch button above the menu is the overlay's default focus: Cross launches,
        // Down drops into the section list, Circle (unhandled here) closes the overlay.
        if (layout.Launch is { } launch && ReferenceEquals(focused, launch))
        {
            switch (key)
            {
                case Key.Down:
                    FocusControl(navigation[layout.Section()]);
                    return true;
                case Key.Enter:
                    ActivateControl(launch);
                    return true;
                case Key.Up or Key.Left or Key.Right:
                    return true;
                default:
                    return false;
            }
        }

        if (focused is null || !panel.IsVisualAncestorOf(focused))
        {
            var current = Array.IndexOf(navigation, focused);
            if (current < 0)
            {
                current = layout.Section();
            }

            switch (key)
            {
                case Key.Up or Key.Down:
                    if (key == Key.Up && current == 0 && layout.Launch is { } above)
                    {
                        FocusControl(above);
                        return true;
                    }

                    var next = Math.Clamp(current + (key == Key.Down ? 1 : -1), 0, navigation.Length - 1);
                    if (next < layout.Panels.Length)
                    {
                        layout.SetSection(next);
                    }

                    FocusControl(navigation[next]);
                    return true;
                case Key.Enter when current >= layout.Panels.Length:
                    ActivateControl(navigation[current]);
                    return true;
                case Key.Right or Key.Enter:
                    if (current < layout.Panels.Length &&
                        OptionsTargets(layout.Panels[layout.Section()]).FirstOrDefault() is { } first)
                    {
                        FocusControl(first);
                    }

                    return true;
                case Key.Left:
                    FocusControl(navigation[current]);
                    return true;
                default:
                    return false;
            }
        }

        var targets = OptionsTargets(panel);
        var target = targets.FirstOrDefault(t => t == focused || t.IsVisualAncestorOf(focused));
        var index = target is null ? -1 : targets.IndexOf(target);
        switch (key)
        {
            case Key.Up or Key.Down:
                var next = Math.Clamp(index + (key == Key.Down ? 1 : -1), 0, targets.Count - 1);
                if (targets.Count > 0)
                {
                    FocusControl(targets[next]);
                }

                return true;
            case Key.Left or Key.Right when target is Slider slider:
                slider.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(slider.Value + (key == Key.Right ? slider.SmallChange : -slider.SmallChange), slider.Minimum, slider.Maximum));
                return true;
            case Key.Left or Key.Right when target is NumericUpDown numeric:
                numeric.SetCurrentValue(NumericUpDown.ValueProperty, Math.Clamp((numeric.Value ?? 0) + (key == Key.Right ? numeric.Increment : -numeric.Increment), numeric.Minimum, numeric.Maximum));
                return true;
            case Key.Left or Key.Escape:
                FocusControl(navigation[layout.Section()]);
                return true;
            case Key.Right:
                return true;
            case Key.Enter:
                if (target is not null)
                {
                    ActivateControl(target);
                }

                return true;
            default:
                return false;
        }
    }

    private static void ActivateControl(Control target)
    {
        switch (target)
        {
            case ComboBox combo:
                combo.IsDropDownOpen = true;
                break;
            case ToggleButton toggle:
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, toggle.IsChecked != true);
                break;
            case Button button:
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (button.Command?.CanExecute(button.CommandParameter) == true)
                {
                    button.Command.Execute(button.CommandParameter);
                }

                break;
        }
    }

    private static List<Control> OptionsTargets(Control panel)
    {
        var targets = new List<Control>();
        foreach (var visual in panel.GetVisualDescendants())
        {
            if (visual is not Control control ||
                control is not (Button or ComboBox or TextBox or Slider or NumericUpDown) ||
                !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled ||
                targets.Any(t => t.IsVisualAncestorOf(control)))
            {
                continue;
            }

            targets.Add(control);
        }

        return targets;
    }

    private void FocusControl(Control control)
    {
        control.Focus(NavigationMethod.Directional);
        control.BringIntoView();
        MarkControllerFocus(control);
    }

    private static bool PadUp(in HostGamepadState pad) => (pad.Buttons & HostGamepadButtons.Up) != 0 || pad.LeftY < 64;

    private static bool PadDown(in HostGamepadState pad) => (pad.Buttons & HostGamepadButtons.Down) != 0 || pad.LeftY > 192;

    private static bool PadLeft(in HostGamepadState pad) => (pad.Buttons & HostGamepadButtons.Left) != 0 || pad.LeftX < 64;

    private static bool PadRight(in HostGamepadState pad) => (pad.Buttons & HostGamepadButtons.Right) != 0 || pad.LeftX > 192;

    private void EnsureControllerFocus(Control root)
    {
        if (FocusManager?.GetFocusedElement() is Visual focused &&
            (focused == root || root.IsVisualAncestorOf(focused) ||
             (focused is ILogical logical && root.IsLogicalAncestorOf(logical))))
        {
            return;
        }

        // Settings open on their selected section (or on Launch in a game's overlay).
        if (ControllerSettings() is { } layout)
        {
            FocusControl(layout.Launch ?? layout.Navigation[layout.Section()]);
            return;
        }

        if (root.Focusable)
        {
            root.Focus(NavigationMethod.Directional);
            return;
        }

        Avalonia.Input.FocusManager.FindFirstFocusableElement(root)?.Focus(NavigationMethod.Directional);
    }

    private bool SendControllerKey(Control root, Key key)
    {
        var target = FocusManager?.GetFocusedElement() as Interactive ?? root;
        var down = new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent,
            Key = key,
            KeyDeviceType = KeyDeviceType.Gamepad,
            Source = target,
        };
        target.RaiseEvent(down);
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyUpEvent,
            Key = key,
            KeyDeviceType = KeyDeviceType.Gamepad,
            Source = target,
        });
        if (down.Handled)
        {
            return true;
        }

        NavigationDirection? direction = key switch
        {
            Key.Up => NavigationDirection.Up,
            Key.Down => NavigationDirection.Down,
            Key.Left => NavigationDirection.Left,
            Key.Right => NavigationDirection.Right,
            _ => null,
        };
        return direction is { } move && FocusManager?.TryMoveFocus(move, new FindNextElementOptions { SearchRoot = root }) == true;
    }
}
