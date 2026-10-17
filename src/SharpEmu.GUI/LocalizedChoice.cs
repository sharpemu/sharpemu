// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SharpEmu.GUI;

/// <summary>
/// A stable settings value with a display label that can be refreshed when
/// the active UI language changes.
/// </summary>
public sealed class LocalizedChoice : INotifyPropertyChanged
{
    private string _label;

    private LocalizedChoice(string value, string label, string? localizationKey)
    {
        Value = value;
        _label = label;
        LocalizationKey = localizationKey;
    }

    public string Value { get; }

    public string Label
    {
        get => _label;
        private set
        {
            if (_label == value)
            {
                return;
            }

            _label = value;
            OnPropertyChanged();
        }
    }

    private string? LocalizationKey { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static LocalizedChoice FromKey(string value, string localizationKey) =>
        new(value, localizationKey, localizationKey);

    public static LocalizedChoice Literal(string value, string label) =>
        new(value, label, null);

    public void Refresh(Localization localization)
    {
        if (LocalizationKey is { } key)
        {
            Label = localization.Get(key);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
