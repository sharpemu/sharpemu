// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO;

namespace SharpEmu.Logging;

public sealed class ConsoleLogSink : ISharpEmuLogSink
{
    public ConsoleLogSink(bool useColors = true, bool includeTimestamp = false)
    {
        UseColors = useColors;
        IncludeTimestamp = includeTimestamp;
    }

    public bool UseColors { get; set; }

    public bool IncludeTimestamp { get; set; }

    public void Write(in LogEntry entry)
    {
        var writer = entry.Level >= LogLevel.Error ? Console.Error : Console.Out;

        lock (writer)
        {
            if (IncludeTimestamp)
            {
                writer.Write('[');
                writer.Write(entry.Timestamp.ToString("HH:mm:ss.fff"));
                writer.Write(']');
            }

            var levelLabel = ToLevelLabel(entry.Level);
            WriteLevelSegment(writer, levelLabel, entry.Level);
            writer.Write('[');
            writer.Write(entry.Category);
            writer.Write(']');
            writer.Write(' ');
            writer.Write(entry.SourceFileName);
            if (entry.SourceLine > 0)
            {
                writer.Write(':');
                writer.Write(entry.SourceLine);
            }

            writer.Write(' ');
            WriteMessageSegment(writer, entry.Message);
            writer.WriteLine();
            if (entry.Exception is not null)
            {
                writer.WriteLine(entry.Exception);
            }
        }
    }

    private static string ToLevelLabel(LogLevel level)
    {
        return level switch
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

    private void WriteLevelSegment(TextWriter writer, string label, LogLevel level)
    {
        if (!UseColors)
        {
            writer.Write('[');
            writer.Write(label);
            writer.Write(']');
            return;
        }

        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = GetLevelColor(level);
        writer.Write('[');
        writer.Write(label);
        writer.Write(']');
        Console.ForegroundColor = originalColor;
    }

    private static ConsoleColor GetLevelColor(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => ConsoleColor.DarkGray,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Info => ConsoleColor.Blue,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.DarkRed,
            _ => ConsoleColor.White,
        };
    }

    private void WriteMessageSegment(TextWriter writer, string message)
    {
        if (!UseColors || !TryGetMessageColor(message, out var messageColor))
        {
            writer.Write(message);
            return;
        }

        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = messageColor;
        writer.Write(message);
        Console.ForegroundColor = originalColor;
    }

    private static bool TryGetMessageColor(string message, out ConsoleColor color)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            color = default;
            return false;
        }

        if (ContainsIgnoreCase(message, "unresolved import") ||
            ContainsIgnoreCase(message, "unresolved symbol") ||
            ContainsIgnoreCase(message, "hot_unresolved_imports=") ||
            ContainsIgnoreCase(message, "unresolved_imports_hit="))
        {
            color = ConsoleColor.Red;
            return true;
        }

        if (ContainsIgnoreCase(message, "syscall") ||
            ContainsIgnoreCase(message, "UnhandledSyscall"))
        {
            color = ConsoleColor.DarkYellow;
            return true;
        }

        if (ContainsIgnoreCase(message, "Import trace") ||
            ContainsIgnoreCase(message, "import_stub_return") ||
            ContainsIgnoreCase(message, "last_import=") ||
            ContainsIgnoreCase(message, "hot_imports=") ||
            ContainsIgnoreCase(message, "resolved import"))
        {
            color = ConsoleColor.Blue;
            return true;
        }

        color = default;
        return false;
    }

    private static bool ContainsIgnoreCase(string text, string value)
    {
        return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
