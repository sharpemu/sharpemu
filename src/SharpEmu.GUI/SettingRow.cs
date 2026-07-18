// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;

namespace SharpEmu.GUI;

public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public static readonly StyledProperty<bool> ShowOverrideProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(ShowOverride));

    public static readonly StyledProperty<bool> IsOverriddenProperty =
        AvaloniaProperty.Register<SettingRow, bool>(
            nameof(IsOverridden), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<FontFamily?> LabelFontFamilyProperty =
        AvaloniaProperty.Register<SettingRow, FontFamily?>(nameof(LabelFontFamily));

    private ContentPresenter? _slot;
    private TextBlock? _label;

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

    public bool ShowOverride
    {
        get => GetValue(ShowOverrideProperty);
        set => SetValue(ShowOverrideProperty, value);
    }

    public bool IsOverridden
    {
        get => GetValue(IsOverriddenProperty);
        set => SetValue(IsOverriddenProperty, value);
    }

    public FontFamily? LabelFontFamily
    {
        get => GetValue(LabelFontFamilyProperty);
        set => SetValue(LabelFontFamilyProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _slot = e.NameScope.Find<ContentPresenter>("PART_Slot");
        _label = e.NameScope.Find<TextBlock>("PART_Label");
        UpdateSlotEnabled();
        UpdateLabelFont();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowOverrideProperty || change.Property == IsOverriddenProperty)
        {
            UpdateSlotEnabled();
        }
        else if (change.Property == LabelFontFamilyProperty)
        {
            UpdateLabelFont();
        }
    }

    private void UpdateLabelFont()
    {
        if (_label is not null && LabelFontFamily is { } family)
        {
            _label.FontFamily = family;
        }
    }

    private void UpdateSlotEnabled()
    {
        if (_slot is not null)
        {
            _slot.IsEnabled = !ShowOverride || IsOverridden;
        }
    }
}
