// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SharpEmu.Libs.Gpu.GpuCommands;

internal static class IndexedDrawTrace
{
    internal delegate bool ReadGuestBytes(ulong address, Span<byte> destination);
    internal readonly record struct Sample(string Signature, ulong Address, uint Count, uint IndexType);

    private static readonly uint _selectedCount = ReadSelectedCount();
    private static readonly Dictionary<string, int> _occurrences = new(StringComparer.Ordinal);
    private static readonly object _gate = new();
    private static readonly FrameCounts _frameCounts = new();
    private static long _previousFlip;
    private static long _scanIssues;

    internal sealed class FrameCounts
    {
        private readonly Dictionary<(string Signature, string Stage), long> _counts = new();

        internal bool Record(string signature, string stage)
        {
            var key = (signature, stage);
            if (!_counts.TryGetValue(key, out var count) && _counts.Count >= 256) return false;
            _counts[key] = count + 1;
            return true;
        }

        internal KeyValuePair<(string Signature, string Stage), long>[] CloseFrame()
        {
            var counts = _counts.ToArray();
            foreach (var entry in counts) _counts[entry.Key] = 0;
            return counts;
        }
    }

    internal static bool Enabled => _selectedCount != 0;

    internal static Sample? Capture(uint count, uint indexType, ulong address, ReadGuestBytes read)
    {
        if (_selectedCount == 0 || count != _selectedCount) return null;
        var elementSize = indexType switch { 0 => 2, 1 => 4, 2 => 1, _ => 0 };
        if (elementSize == 0) return new("invalid-index-type", address, count, indexType);
        var data = new byte[checked((int)count * elementSize)];
        var signature = read(address, data) ? ComputeSignature(data, elementSize) : "unreadable";
        return new(signature, address, count, indexType);
    }

    internal static string ComputeSignature(ReadOnlySpan<byte> indices, int elementSize)
    {
        if (elementSize is not (1 or 2 or 4) || indices.Length % elementSize != 0)
            throw new ArgumentException("The index data has an invalid element size.", nameof(elementSize));
        var normalized = new byte[checked(indices.Length / elementSize * sizeof(uint))];
        for (var index = 0; index < indices.Length / elementSize; index++)
        {
            var source = indices[(index * elementSize)..];
            var value = elementSize switch
            {
                1 => source[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(source),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(source),
            };
            BinaryPrimitives.WriteUInt32LittleEndian(normalized.AsSpan(index * sizeof(uint)), value);
        }
        return Convert.ToHexString(SHA256.HashData(normalized));
    }

    internal static void Write(Sample? sample, string stage, ulong submit, ulong packet)
    {
        if (sample is not { } value) return;
        lock (_gate)
        {
            if (!_frameCounts.Record(value.Signature, stage)) _scanIssues++;
            var key = $"{value.Signature}:{stage}";
            if (!_occurrences.TryGetValue(key, out var occurrence) && _occurrences.Count >= 256) return;
            if (occurrence >= 4) return;
            _occurrences[key] = ++occurrence;
            Console.Error.WriteLine($"[GPU][TRACE] IndexedDraw stage={stage} hash={value.Signature} " +
                $"address=0x{value.Address:X16} count={value.Count} indexType={value.IndexType} " +
                $"submit={submit} packet=0x{packet:X16} occurrence={occurrence}");
            if (_occurrences.Count == 256 && occurrence == 1)
                Console.Error.WriteLine("[GPU][TRACE] IndexedDraw limit=256. New signatures and stages are omitted.");
        }
    }

    internal static string CreateLabel(Sample sample, ulong submit, ulong packet)
    {
        lock (_gate)
            return $"IndexedDraw afterFlip={_previousFlip} hash={sample.Signature} address=0x{sample.Address:X16} " +
                $"count={sample.Count} indexType={sample.IndexType} submit={submit} packet=0x{packet:X16}";
    }

    internal static void ScanIssue(string reason)
    {
        lock (_gate)
        {
            if (++_scanIssues <= 4)
                Console.Error.WriteLine($"[GPU][TRACE] IndexedDrawScan afterFlip={_previousFlip} incomplete={reason}");
        }
    }

    internal static void OnFlip(long version)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            Console.Error.WriteLine($"[GPU][TRACE] IndexedDrawFrame afterFlip={_previousFlip} throughFlip={version} scanIssues={_scanIssues}");
            foreach (var group in _frameCounts.CloseFrame().GroupBy(entry => entry.Key.Signature))
            {
                var counts = string.Join(' ', group.Select(entry => $"{entry.Key.Stage}={entry.Value}"));
                Console.Error.WriteLine($"[GPU][TRACE] IndexedDrawCounts throughFlip={version} hash={group.Key} {counts}");
            }
            _previousFlip = version;
            _scanIssues = 0;
        }
    }

    private static uint ReadSelectedCount()
    {
        var text = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAW_INDEX_COUNT");
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (uint.TryParse(text, out var count) && count is > 0 and <= 4096) return count;
        Console.Error.WriteLine("[GPU][WARN] SHARPEMU_TRACE_DRAW_INDEX_COUNT must be from 1 through 4096. The trace is off.");
        return 0;
    }
}
