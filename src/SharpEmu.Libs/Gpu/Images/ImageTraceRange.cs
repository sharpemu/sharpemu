// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.Libs.Gpu.Images;

// Narrows the image-clear trace to guest address ranges. See docs/image-clear-tracing.md.
// Ranges come from SHARPEMU_TRACE_IMAGE_RANGE or from images that followed shaders use.
internal static class ImageTraceRange
{
    internal const int MaximumRanges = 16;

    private static readonly object _gate = new();
    private static readonly IReadOnlyList<(ulong ShaderHash, int ImageIndex)> _followTargets =
        ParseFollowTargets(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_IMAGE_FOLLOW"), Console.Error);
    private static readonly HashSet<ulong> _pendingAddresses = new();
    private static GuestSpan[] _ranges = Load();

    internal static IReadOnlyList<GuestSpan> Ranges => Volatile.Read(ref _ranges);

    internal static bool IsSet => Ranges.Count != 0;

    // History stays unfiltered until a range is known, so a follow keeps earlier producers.
    internal static IReadOnlyList<GuestSpan>? ActiveFilter => SelectActiveFilter(Ranges);

    internal static IReadOnlyList<GuestSpan>? SelectActiveFilter(IReadOnlyList<GuestSpan> ranges) =>
        ranges.Count == 0 ? null : ranges;

    internal static bool Overlaps(ulong address, ulong size) => Overlaps(Ranges, address, size);

    internal static bool Overlaps(IReadOnlyList<GuestSpan> ranges, ulong address, ulong size)
    {
        if (size == 0 || size > ulong.MaxValue - address) return false;
        foreach (var range in ranges)
        {
            if (address < range.End && range.Address < address + size) return true;
        }
        return false;
    }

    // The descriptor gives the base address; the image lookup supplies the footprint.
    internal static void NoteDescriptor(ulong shaderHash, int imageIndex, ulong address)
    {
        if (address == 0 || !IsFollowTarget(_followTargets, shaderHash, imageIndex)) return;
        lock (_gate)
        {
            _pendingAddresses.Add(address);
        }
    }

    internal static void NoteImageLookup(ulong address, ulong size)
    {
        if (_followTargets.Count == 0 || size == 0) return;
        lock (_gate)
        {
            if (_pendingAddresses.Remove(address)) AddFollowedRangeLocked(address, size);
        }
    }

    // A binding knows its shader, index, and footprint, so the range exists before the first lookup.
    internal static void NoteFollowedImage(ulong shaderHash, int imageIndex, ulong address, ulong size)
    {
        if (address == 0 || size == 0 || !IsFollowTarget(_followTargets, shaderHash, imageIndex)) return;
        lock (_gate)
        {
            _pendingAddresses.Remove(address);
            AddFollowedRangeLocked(address, size);
        }
    }

    private static void AddFollowedRangeLocked(ulong address, ulong size)
    {
        var updated = IncludeFootprint(_ranges, address, size);
        if (updated is null)
        {
            Console.Error.WriteLine($"[GPU][WARN] Image trace follow found more than {MaximumRanges} ranges. Address 0x{address:X16} is not traced.");
            return;
        }
        if (ReferenceEquals(updated, _ranges)) return;
        Volatile.Write(ref _ranges, updated);
        Console.Error.WriteLine($"[GPU][TRACE] ImageTraceRange follow address=0x{address:X16} size=0x{size:X}");
    }

    internal static GuestSpan[]? IncludeFootprint(GuestSpan[] ranges, ulong address, ulong size)
    {
        if (size == 0 || size > ulong.MaxValue - address) return ranges;
        var end = address + size;
        foreach (var range in ranges)
            if (range.Address <= address && range.End >= end) return ranges;

        var combined = new List<GuestSpan>(ranges.Length + 1);
        combined.AddRange(ranges);
        combined.Add(new GuestSpan(address, size));
        combined.Sort((left, right) => left.Address.CompareTo(right.Address));
        var merged = new List<GuestSpan>(combined.Count);
        foreach (var range in combined)
        {
            if (merged.Count != 0 && range.Address <= merged[^1].End)
            {
                var previous = merged[^1];
                merged[^1] = new GuestSpan(previous.Address, Math.Max(previous.End, range.End) - previous.Address);
            }
            else merged.Add(range);
        }
        return merged.Count <= MaximumRanges ? merged.ToArray() : null;
    }

    internal static bool IsFollowTarget(IReadOnlyList<(ulong ShaderHash, int ImageIndex)> targets, ulong shaderHash, int imageIndex)
    {
        foreach (var target in targets)
        {
            if (target.ShaderHash == shaderHash && target.ImageIndex == imageIndex) return true;
        }
        return false;
    }

    private static GuestSpan[] Load()
    {
        var ranges = Parse(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_IMAGE_RANGE"), Console.Error);
        if ((ranges.Count != 0 || _followTargets.Count != 0) && !ImageClearTrace.Enabled)
            Console.Error.WriteLine("[GPU][WARN] SHARPEMU_TRACE_IMAGE_RANGE and SHARPEMU_TRACE_IMAGE_FOLLOW have no effect without SHARPEMU_TRACE_IMAGE_CLEARS=1.");
        return [.. ranges];
    }

    // Entries are "start+size" or "start-end" in hexadecimal, separated by commas or semicolons.
    internal static IReadOnlyList<GuestSpan> Parse(string? value, TextWriter? errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<GuestSpan>();
        var ranges = new List<GuestSpan>();
        foreach (var entry in SplitEntries(value))
        {
            if (ranges.Count == MaximumRanges)
            {
                errors?.WriteLine($"[GPU][WARN] SHARPEMU_TRACE_IMAGE_RANGE accepts {MaximumRanges} ranges. Further entries are ignored.");
                break;
            }
            if (TryParseRange(entry, out var range)) ranges.Add(range);
            else errors?.WriteLine($"[GPU][WARN] SHARPEMU_TRACE_IMAGE_RANGE entry '{entry}' is ignored. Use start+size or start-end in hexadecimal.");
        }
        return ranges;
    }

    // Entries are "shaderHash:imageIndex"; the hash is hexadecimal and the index is decimal.
    internal static IReadOnlyList<(ulong ShaderHash, int ImageIndex)> ParseFollowTargets(string? value, TextWriter? errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<(ulong, int)>();
        var targets = new List<(ulong, int)>();
        foreach (var entry in SplitEntries(value))
        {
            var separator = entry.IndexOf(':');
            if (separator > 0 && TryParseHex(entry[..separator], out var hash) &&
                int.TryParse(entry[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                targets.Add((hash, index));
            else
                errors?.WriteLine($"[GPU][WARN] SHARPEMU_TRACE_IMAGE_FOLLOW entry '{entry}' is ignored. Use shaderHash:imageIndex.");
        }
        return targets;
    }

    private static string[] SplitEntries(string value) =>
        value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseRange(string entry, out GuestSpan range)
    {
        range = default;
        var separator = entry.IndexOfAny(['+', '-']);
        if (separator <= 0 || !TryParseHex(entry[..separator], out var start) ||
            !TryParseHex(entry[(separator + 1)..], out var second))
        {
            return false;
        }
        var size = entry[separator] == '+' ? second : second > start ? second - start : 0;
        if (size == 0 || size > ulong.MaxValue - start) return false;
        range = new GuestSpan(start, size);
        return true;
    }

    private static bool TryParseHex(string text, out ulong value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
