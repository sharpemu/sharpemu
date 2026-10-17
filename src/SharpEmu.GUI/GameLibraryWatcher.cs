// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

/// <summary>
/// Watches configured game folders and collapses filesystem bursts into one
/// library refresh request
/// </summary>
internal sealed class GameLibraryWatcher : IDisposable
{
    private static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(600);

    private readonly object _sync = new();
    private readonly TimeSpan _debounceInterval;
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _debounceTimer;
    private bool _disposed;

    internal GameLibraryWatcher(TimeSpan? debounceInterval = null)
    {
        _debounceInterval = debounceInterval ?? DefaultDebounceInterval;
    }

    public event EventHandler? RefreshRequested;

    /// <summary>
    /// Replaces the watched roots with the current configured game folders
    /// </summary>
    public void Watch(IReadOnlyList<string> folders)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
            _debounceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            foreach (var folder in folders.Distinct(GameLibraryPath.Comparer))
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName
                                       | NotifyFilters.DirectoryName
                                       | NotifyFilters.LastWrite
                                       | NotifyFilters.Size,
                    };
                    watcher.Created += OnFileSystemChanged;
                    watcher.Changed += OnFileSystemChanged;
                    watcher.Deleted += OnFileSystemChanged;
                    watcher.Renamed += OnFileSystemChanged;
                    watcher.Error += OnWatcherError;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"[GUI][WARN] Could not watch game folder '{folder}': {exception.Message}");
                }
            }
        }
    }

    private static readonly HashSet<string> LibraryFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "eboot.bin",
        "param.json",
        "icon0.png",
        "pic0.png",
        "pic1.png",
        "snd0.at9",
        "trophy00.ucp",
    };

    private void OnFileSystemChanged(object sender, FileSystemEventArgs args)
    {
        if (args.ChangeType is WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed || AffectsLibrary(args.FullPath))
        {
            ScheduleRefresh();
        }
    }

    private static bool AffectsLibrary(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == 0 ||
               Path.GetExtension(name).Length == 0 ||
               name.EndsWith(".zar", StringComparison.OrdinalIgnoreCase) ||
               LibraryFiles.Contains(name);
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        Console.Error.WriteLine(
            $"[GUI][WARN] Game library watcher reported an error: {args.GetException().Message}");
        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_debounceTimer is null)
            {
                _debounceTimer = new Timer(
                    static state => ((GameLibraryWatcher)state!).RaiseRefreshRequested(),
                    this,
                    _debounceInterval,
                    Timeout.InfiniteTimeSpan);
            }
            else
            {
                _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void RaiseRefreshRequested()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
        }

        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }
}
