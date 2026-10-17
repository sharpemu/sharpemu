// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SharpEmu.GUI;

[Flags]
internal enum GameEntryChanges
{
    None = 0,
    Metadata = 1,
    Cover = 2,
    Background = 4,
}

public sealed class GameEntry : LibraryTile, INotifyPropertyChanged
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
    private string _name;
    private string? _titleId;
    private string? _version;
    private string? _coverPath;
    private string? _backgroundPath;
    private string _initials;
    private FileStamp _coverStamp;
    private FileStamp _backgroundStamp;

    public GameEntry(
        string name, string? titleId, string? version, string path, long sizeBytes,
        string? coverPath, string? backgroundPath)
    {
        _name = name;
        _titleId = titleId;
        _version = version;
        Path = path;
        _sizeBytes = sizeBytes;
        _coverPath = coverPath;
        _backgroundPath = backgroundPath;
        _initials = ComputeInitials(name);
        _coverStamp = FileStamp.Read(coverPath);
        _backgroundStamp = FileStamp.Read(backgroundPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => _name;

    public string? TitleId => _titleId;

    /// <summary>Content version from sce_sys/param.json, e.g. "01.000.000".</summary>
    public string? Version => _version;

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        }
    }

    /// <summary>Path to the cover art image shipped with the game, if found.</summary>
    public string? CoverPath => _coverPath;

    /// <summary>Path to the key art (pic0/pic1) shipped with the game, if found.</summary>
    public string? BackgroundPath => _backgroundPath;

    /// <summary>
    /// Decoded key art used as the window backdrop while this game is
    /// selected. Loaded on demand and cached; not exposed via binding.
    /// </summary>
    public Bitmap? Background { get; set; }

    public string Initials => _initials;

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

    public bool HasTitleId => TitleId is not null;

    /// <summary>Badge text shown in the launch bar, e.g. "v01.000.000".</summary>
    public string? VersionText => Version is null ? null : $"v{Version}";

    public bool HasVersion => Version is not null;

    /// <summary>Formatted install size badge shown in the launch bar.</summary>
    public string SizeText => FormatSize(SizeBytes);

    /// <summary>
    /// Applies a fresh filesystem scan to this presentation object so its
    /// ListBox container and selection remain stable
    /// </summary>
    internal GameEntryChanges UpdateFrom(GameEntry scanned)
    {
        if (!Path.Equals(scanned.Path, GameLibraryPath.Comparison))
        {
            throw new ArgumentException("Only matching game paths can be reconciled", nameof(scanned));
        }

        var changes = GameEntryChanges.None;
        if (!string.Equals(_name, scanned.Name, StringComparison.Ordinal))
        {
            _name = scanned.Name;
            _initials = ComputeInitials(scanned.Name);
            _placeholderBrush = null;
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(Initials));
            RaisePropertyChanged(nameof(PlaceholderBrush));
            changes |= GameEntryChanges.Metadata;
        }

        if (!string.Equals(_titleId, scanned.TitleId, StringComparison.Ordinal))
        {
            _titleId = scanned.TitleId;
            RaisePropertyChanged(nameof(TitleId));
            RaisePropertyChanged(nameof(HasTitleId));
            changes |= GameEntryChanges.Metadata;
        }

        if (!string.Equals(_version, scanned.Version, StringComparison.Ordinal))
        {
            _version = scanned.Version;
            RaisePropertyChanged(nameof(Version));
            RaisePropertyChanged(nameof(VersionText));
            RaisePropertyChanged(nameof(HasVersion));
            changes |= GameEntryChanges.Metadata;
        }

        if (!string.Equals(_coverPath, scanned.CoverPath, StringComparison.Ordinal)
            || _coverStamp != scanned._coverStamp)
        {
            _coverPath = scanned.CoverPath;
            _coverStamp = scanned._coverStamp;
            var previousCover = Cover;
            Cover = null;
            previousCover?.Dispose();
            changes |= GameEntryChanges.Cover;
        }

        if (!string.Equals(_backgroundPath, scanned.BackgroundPath, StringComparison.Ordinal)
            || _backgroundStamp != scanned._backgroundStamp)
        {
            _backgroundPath = scanned.BackgroundPath;
            _backgroundStamp = scanned._backgroundStamp;
            var previousBackground = Background;
            Background = null;
            previousBackground?.Dispose();
            changes |= GameEntryChanges.Background;
        }

        return changes;
    }

    private void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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

    private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks)
    {
        public static FileStamp Read(string? path)
        {
            if (path is null)
            {
                return default;
            }

            try
            {
                var file = new FileInfo(path);
                return file.Exists
                    ? new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks)
                    : default;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException)
            {
                return default;
            }
        }
    }
}
