// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Diagnostics;

internal static class DccWriterTrace
{
    private const int MaxReports = 256;
    private const int MaxFrames = 12;

    private static readonly bool DccEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DCC_WRITERS") == "1";

    private static readonly ulong[] TargetAddresses = (Environment.GetEnvironmentVariable("SHARPEMU_TRACE_WRITER_ADDRESS") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(ParseAddress)
        .Where(address => address != 0)
        .ToArray();

    public static readonly bool Enabled = DccEnabled || TargetAddresses.Length != 0;

    [ThreadStatic]
    public static ulong CurrentProgram;

    private static readonly HashSet<(ulong Address, ulong Size, ulong Program)> _reported = new();
    private static readonly HashSet<(ulong Hash, string Reason)> _refusals = new();

    private static ulong ParseAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    public static void Record(IGuestImageCache images, ulong address, ulong size)
    {
        var targeted = TargetAddresses.Any(target => target >= address && target - address < size);
        if (_reported.Count >= MaxReports || (!targeted && !(DccEnabled && images.OverlapsDccMetadata(address, size))) ||
            !_reported.Add((address, size, CurrentProgram)))
        {
            return;
        }

        var frames = new StackTrace(1, false).GetFrames();
        var names = frames.Take(MaxFrames).Select(frame => frame.GetMethod() is { } method
            ? $"{method.DeclaringType?.Name}.{method.Name}"
            : "?");
        Console.Error.WriteLine(
            $"[GPU][{(targeted ? "TARGET_WRITER" : "DCC_WRITER")}] address=0x{address:X16} size=0x{size:X} program=0x{CurrentProgram:X16} stack={string.Join(" < ", names)}");
    }

    public static void Refuse(ulong hash, string reason)
    {
        if (!Enabled || _refusals.Count >= MaxReports || !_refusals.Add((hash, reason)))
        {
            return;
        }

        Console.Error.WriteLine($"[GPU][BOUNDED_FILL_REFUSED] program=0x{hash:X16} reason={reason}");
    }
}
