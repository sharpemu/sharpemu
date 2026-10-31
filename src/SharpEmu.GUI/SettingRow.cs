// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace SharpEmu.GUI;

/// <summary>Where a game setting row's value comes from, for the badge and reset button.</summary>
public enum SettingScopeState
{
    /// <summary>Not a game setting, or the settings for every game are being edited.</summary>
    None,

    /// <summary>The game uses the value set for every game.</summary>
    Inherited,

    /// <summary>The game has its own value.</summary>
    Overridden,
}

public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<SettingScopeState> ScopeStateProperty =
        AvaloniaProperty.Register<SettingRow, SettingScopeState>(nameof(ScopeState));

    /// <summary>The value set for every game, as shown in the reset button's tooltip.</summary>
    public static readonly StyledProperty<string?> InheritedValueProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(InheritedValue));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public static readonly StyledProperty<FontFamily?> LabelFontFamilyProperty =
        AvaloniaProperty.Register<SettingRow, FontFamily?>(nameof(LabelFontFamily));

    private TextBlock? _label;
    private Button? _reset;

    /// <summary>Raised when the reset button asks for the value set for every game.</summary>
    public event EventHandler? ResetRequested;

    public SettingRow()
    {
        // The row, not the control inside it, is the focus target: keyboard and
        // D-pad focus anywhere within lights the row; a mouse click does not.
        AddHandler(GotFocusEvent, (_, e) => PseudoClasses.Set(":rowfocus", e.NavigationMethod != NavigationMethod.Pointer));
        AddHandler(LostFocusEvent, (_, _) => PseudoClasses.Set(":rowfocus", false));
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public SettingScopeState ScopeState
    {
        get => GetValue(ScopeStateProperty);
        set => SetValue(ScopeStateProperty, value);
    }

    public string? InheritedValue
    {
        get => GetValue(InheritedValueProperty);
        set => SetValue(InheritedValueProperty, value);
    }

    public FontFamily? LabelFontFamily
    {
        get => GetValue(LabelFontFamilyProperty);
        set => SetValue(LabelFontFamilyProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _label = e.NameScope.Find<TextBlock>("PART_Label");
        if (_reset is not null)
        {
            _reset.Click -= OnResetClick;
        }

        _reset = e.NameScope.Find<Button>("PART_Reset");
        if (_reset is not null)
        {
            _reset.Click += OnResetClick;
        }

        UpdateLabelFont();
        UpdateScopeState();
    }

    private void OnResetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelFontFamilyProperty)
        {
            UpdateLabelFont();
        }
        else if (change.Property == ScopeStateProperty || change.Property == InheritedValueProperty)
        {
            UpdateScopeState();
        }
    }

    private void UpdateScopeState()
    {
        PseudoClasses.Set(":inherited", ScopeState == SettingScopeState.Inherited);
        PseudoClasses.Set(":overridden", ScopeState == SettingScopeState.Overridden);
        if (_reset is not null)
        {
            var tip = string.Format(
                Localization.Instance.Get("Setting.Scope.Reset"),
                InheritedValue ?? string.Empty);
            ToolTip.SetTip(_reset, tip);
            Avalonia.Automation.AutomationProperties.SetName(_reset, tip);
        }
    }

    private void UpdateLabelFont()
    {
        if (_label is not null && LabelFontFamily is { } family)
        {
            _label.FontFamily = family;
        }
    }
}
