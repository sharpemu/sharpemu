// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SharpEmu.GUI;

public sealed class GameEntry : INotifyPropertyChanged
{
    // Placeholder gradients for games without cover art, picked
    // deterministically from the game name so a game keeps its color.
    private static readonly (Color Start, Color End)[] PlaceholderPalette =
    {
        (Color.Parse("#5B4B8A"), Color.Parse("#2C2A4A")),
        (Color.Parse("#1F6E8C"), Color.Parse("#173B45")),
        (Color.Parse("#7A4069"), Color.Parse("#3B1C32")),
        (Color.Parse("#2D6A4F"), Color.Parse("#1B3A2B")),
        (Color.Parse("#8C5425"), Color.Parse("#4A2B12")),
        (Color.Parse("#4F6D9E"), Color.Parse("#263349")),
        (Color.Parse("#8A4B4B"), Color.Parse("#3F2222")),
        (Color.Parse("#3E7C7B"), Color.Parse("#1E3D3C")),
    };

    private Bitmap? _cover;
    private IBrush? _placeholderBrush;
    private long _sizeBytes;

    public GameEntry(
        string name, string? titleId, string path, long sizeBytes, string? coverPath, string? backgroundPath)
    {
        Name = name;
        TitleId = titleId;
        Path = path;
        _sizeBytes = sizeBytes;
        CoverPath = coverPath;
        BackgroundPath = backgroundPath;
        Initials = ComputeInitials(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string? TitleId { get; }

    public string Path { get; }

    /// <summary>
    /// Total size of the game. Initially the eboot's own size from the scan;
    /// replaced with the full install folder size once computed in the
    /// background.
    /// </summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes == value)
            {
                return;
            }

            _sizeBytes = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeBytes)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    /// <summary>Path to the cover art image shipped with the game, if found.</summary>
    public string? CoverPath { get; }

    /// <summary>Path to the key art (pic0/pic1) shipped with the game, if found.</summary>
    public string? BackgroundPath { get; }

    /// <summary>
    /// Decoded key art used as the window backdrop while this game is
    /// selected. Loaded on demand and cached; not exposed via binding.
    /// </summary>
    public Bitmap? Background { get; set; }

    public string Initials { get; }

    // Built lazily: brushes are AvaloniaObjects that must be created on the
    // UI thread, while GameEntry itself is constructed on the scan thread.
    public IBrush PlaceholderBrush => _placeholderBrush ??= BuildPlaceholderBrush(Name);

    /// <summary>Decoded cover art; loaded asynchronously after the library scan.</summary>
    public Bitmap? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value))
            {
                return;
            }

            _cover = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
        }
    }

    public bool HasCover => _cover is not null;

    public string Detail => TitleId is not null
        ? $"{TitleId}  •  {FormatSize(SizeBytes)}"
        : FormatSize(SizeBytes);

    private static string ComputeInitials(string name)
    {
        var initials = name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => char.IsLetterOrDigit(word[0]))
            .Select(word => char.ToUpperInvariant(word[0]))
            .Take(2)
            .ToArray();

        return initials.Length > 0 ? new string(initials) : "?";
    }

    private static IBrush BuildPlaceholderBrush(string name)
    {
        var hash = 0;
        foreach (var ch in name)
        {
            hash = unchecked(hash * 31 + ch);
        }

        var (start, end) = PlaceholderPalette[(int)((uint)hash % PlaceholderPalette.Length)];
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(start, 0),
                new GradientStop(end, 1),
            },
        };
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GiB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KiB",
            _ => $"{bytes} B",
        };
    }
}
