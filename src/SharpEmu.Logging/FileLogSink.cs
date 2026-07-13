// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO;
using System.Text;

namespace SharpEmu.Logging;

/// <summary>
/// Writes log entries to a file. Thread-safe via an internal lock.
/// <see cref="StreamWriter.AutoFlush"/> is enabled so entries survive a crash.
/// </summary>
public sealed class FileLogSink : ISharpEmuLogSink, IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    /// <param name="path">Absolute or relative file path. Parent directories are created if missing.</param>
    /// <param name="append"><see langword="true"/> to append to an existing file; <see langword="false"/> to overwrite.</param>
    /// <param name="includeTimestamp">Always recommended for file logs — entries include a full date-time prefix.</param>
    public FileLogSink(string path, bool append = true, bool includeTimestamp = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

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
            bufferSize: 4096,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(fileStream, Encoding.UTF8)
        {
            AutoFlush = true
        };

        IncludeTimestamp = includeTimestamp;
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
        }
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
