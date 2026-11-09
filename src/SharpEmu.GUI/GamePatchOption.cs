// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Text.Json;
using SharpEmu.Core.Patches;

namespace SharpEmu.GUI;

internal sealed class GamePatchOption : INotifyPropertyChanged
{
    private readonly GamePatchRepository _repository;
    private readonly string _titleId;
    private readonly Action<string> _reportError;
    private bool _isChecked;

    public GamePatchOption(GamePatch patch, GamePatchRepository repository, string titleId, string? version,
        bool selected, bool readOnly, Action<string> reportError)
    {
        Patch = patch;
        _repository = repository;
        _titleId = titleId;
        _isChecked = selected;
        _reportError = reportError;
        CanToggle = !readOnly && patch.Error is null && patch.MatchesVersion(version);
        Status = patch.Error ?? (!patch.MatchesVersion(version)
            ? Localization.Instance.Get("Library.Patches.VersionMismatch")
            : readOnly ? Localization.Instance.Get("Library.Patches.Running") : string.Empty);
    }

    public GamePatch Patch { get; }
    public bool CanToggle { get; }
    public string Status { get; }
    public bool HasStatus => Status.Length > 0;
    public string AppVersionText => Patch.IsPatternVersion ? Localization.Instance.Get("Library.Patches.PatternVersion") : Patch.AppVersion;
    public string TargetVersionText => Patch.IsPatternVersion ? AppVersionText : Localization.Instance.Format("Library.Patches.TargetVersion", Patch.AppVersion);
    public string Note => Patch.Note.Replace("\\n", "\n");

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value == _isChecked) return;
            try
            {
                if (CanToggle)
                {
                    _repository.SetEnabled(_titleId, Patch, value);
                    _isChecked = value;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            {
                _reportError(exception.Message);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public bool Matches(string? query, string? fileName) =>
        (fileName is null || string.Equals(Patch.FileName, fileName, StringComparison.Ordinal)) &&
        (string.IsNullOrWhiteSpace(query) || new[]
        {
            Patch.Name, Patch.Author, Note, Patch.AppVersion, Patch.Executable, Patch.FileName, Patch.PatchVersion,
        }.Any(text => text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
