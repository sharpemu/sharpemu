// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

internal static class ImageClearTrace
{
    internal static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_IMAGE_CLEARS"), "1", StringComparison.Ordinal);

    private const int EntryLimit = 8192;
    private static readonly HashSet<string> _entries = new(StringComparer.Ordinal);
    private static readonly object _gate = new();

    internal static void Write(string message)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            if (_entries.Count >= EntryLimit || !_entries.Add(message)) return;
            Console.Error.WriteLine($"[GPU][TRACE] ImageClear {message}");
            if (_entries.Count == EntryLimit)
            {
                Console.Error.WriteLine("[GPU][TRACE] ImageClear limit=8192. Further entries are omitted.");
            }
        }
    }
}
