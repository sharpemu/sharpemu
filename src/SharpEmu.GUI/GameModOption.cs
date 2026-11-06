// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Text.Json;
using SharpEmu.Core.Mods;

namespace SharpEmu.GUI;

internal sealed class GameModOption(GameMod mod, GameModRepository repository, Action<string> reportError) : INotifyPropertyChanged
{
    private bool _isChecked = mod.Enabled;
    public string Name => mod.Name;
    public string DirectoryPath => mod.DirectoryPath;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value == _isChecked) return;
            try
            {
                repository.SetEnabled(Name, value);
                _isChecked = value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                reportError(exception.Message);
            }
            PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
