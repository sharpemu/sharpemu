// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO;
using System.Text;

namespace SharpEmu.Logging;

public sealed class FileLogSink : ISharpEmuLogSink, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly object _sync = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private StreamWriter _writer;
    private readonly Timer _flushTimer;
    private bool _disposed;

    public FileLogSink(string path, bool append = true, bool includeTimestamp = true, long maxBytes = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _maxBytes = maxBytes > 0 ? maxBytes : 0;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var fileStream = new FileStream(
            path,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(fileStream, Encoding.UTF8, bufferSize: 65536)
        {
            AutoFlush = false
        };

        IncludeTimestamp = includeTimestamp;

        _flushTimer = new Timer(
            static state => ((FileLogSink)state!).FlushBuffered(),
            this,
            FlushInterval,
            FlushInterval);
    }

    public bool IncludeTimestamp { get; set; }

    public void Write(in LogEntry entry)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (IncludeTimestamp)
            {
                _writer.Write('[');
                _writer.Write(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                _writer.Write(']');
            }

            _writer.Write('[');
            _writer.Write(ToLevelLabel(entry.Level));
            _writer.Write(']');
            _writer.Write('[');
            _writer.Write(entry.Category);
            _writer.Write(']');
            _writer.Write(' ');

            _writer.Write(entry.SourceFileName);
            if (entry.SourceLine > 0)
            {
                _writer.Write(':');
                _writer.Write(entry.SourceLine);
            }

            _writer.Write(' ');
            _writer.WriteLine(entry.Message);

            if (entry.Exception is not null)
            {
                _writer.WriteLine(entry.Exception);
            }

            if (entry.Level >= LogLevel.Error)
            {
                _writer.Flush();
            }

            if (_maxBytes > 0 && _writer.BaseStream.Position >= _maxBytes)
            {
                TryRollover();
            }
        }
    }

    private void FlushBuffered()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _writer.Flush();
        }
    }

    // Called inside _sync. Flushes and closes the current writer, renames
    // the current file to <_path>.1 (overwriting any previous backup), then
    // opens a fresh writer at _path. If the rename or create fails the catch
    // block re-opens in append mode so subsequent writes are not silently
    // dropped. A second failure marks the sink disposed so Write() returns
    // immediately rather than throwing on every call.
    private void TryRollover()
    {
        try
        {
            _writer.Flush();
            _writer.Dispose();

            File.Move(_path, _path + ".1", overwrite: true);

            var fileStream = new FileStream(
                _path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 65536,
                FileOptions.SequentialScan);
            _writer = new StreamWriter(fileStream, Encoding.UTF8, bufferSize: 65536)
            {
                AutoFlush = false,
            };
        }
        catch
        {
            // Rollover failed. Re-open in append mode so the rest of the
            // session is not silently lost.
            try
            {
                var fileStream = new FileStream(
                    _path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 65536,
                    FileOptions.SequentialScan);
                _writer = new StreamWriter(fileStream, Encoding.UTF8, bufferSize: 65536)
                {
                    AutoFlush = false,
                };
            }
            catch
            {
                // Cannot reopen either; mark disposed so Write() returns
                // immediately instead of throwing on every subsequent call.
                _disposed = true;
            }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private static string ToLevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "LOG",
    };
}
