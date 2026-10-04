// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.GpuCommands;

internal static class VisibilityResultTrace
{
    internal delegate bool ReadBacking(ulong address, Span<byte> destination);
    private readonly record struct Publication(ulong Address, ulong Value, int Queue, ulong Submission, uint InstanceMask, uint StrideBytes, bool Aggregate);

    internal static bool Enabled { get; } = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VISIBILITY") == "1";
    private static readonly object _gate = new();
    private static readonly Queue<Publication> _publications = new();
    private static long _writes, _readFailures, _mismatches, _predicates, _skips, _downloads;
    private static long _flipCount, _detailCount;
    private static Publication _lastWrite;
    private static (int Queue, ulong Submission, ulong Address, ulong Value, uint Condition, uint Wait, bool Skip) _lastPredicate;

    internal static void Published(int queue, ulong submission, ulong address, ulong value,
        uint instanceMask, uint strideBytes, IGuestBackedSpace? memory, bool aggregate = false)
    {
        if (!Enabled) return;
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        var readable = true;
        uint mismatchMask = 0;
        for (var slot = 0; slot < 24; slot++)
        {
            if ((instanceMask & (1u << slot)) == 0) continue;
            if (!TryGetSlotAddress(address, slot, strideBytes, out var slotAddress) ||
                memory?.TryReadBacking(slotAddress, bytes) != true)
            {
                readable = false;
                continue;
            }
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes) != ExpectedSlotValue(value, instanceMask, strideBytes, slot, aggregate))
                mismatchMask |= 1u << slot;
        }
        lock (_gate)
        {
            _writes++;
            if (!readable) _readFailures++;
            if (mismatchMask != 0) _mismatches++;
            if (_publications.Count == 256) _publications.Dequeue();
            _publications.Enqueue(new(address, value, queue, submission, instanceMask, strideBytes, aggregate));
            _lastWrite = new(address, value, queue, submission, instanceMask, strideBytes, aggregate);
            if (_writes <= 2 || !readable || mismatchMask != 0)
                Detail($"published queue={queue} submit={submission} address=0x{address:X16} value=0x{value:X16} readable={readable} mismatchMask=0x{mismatchMask:X6} instanceMask=0x{instanceMask:X6} strideBytes={strideBytes}");
        }
    }

    internal static void Predicate(int queue, ulong submission, ulong address, ulong value, uint condition, uint wait, bool skip)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            _predicates++;
            if (skip) _skips++;
            _lastPredicate = (queue, submission, address, value, condition, wait, skip);
            if (_predicates <= 2) Detail($"predicate queue={queue} submit={submission} address=0x{address:X16} value=0x{value:X16} condition={condition} wait={wait} skip={skip}");
        }
    }

    internal static void Download(ulong address, ReadOnlySpan<byte> data, ReadBacking read)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            foreach (var publication in _publications)
            {
                var stillPublished = ReplacedSlots(publication.Address, publication.Value, address, data,
                    publication.InstanceMask, publication.StrideBytes, read, publication.Aggregate);
                if (stillPublished == 0) continue;
                _downloads++;
                Detail($"download-replaces queue={publication.Queue} submit={publication.Submission} " +
                    $"resultAddress=0x{publication.Address:X16} published=0x{publication.Value:X16} " +
                    $"downloadAddress=0x{address:X16} bytes={data.Length} slotMask=0x{stillPublished:X6}");
            }
        }
    }

    internal static uint DifferentSlots(ulong resultAddress, ulong expected, ulong dataAddress, ReadOnlySpan<byte> data,
        uint instanceMask = 0xFFFF, uint strideBytes = 16, bool aggregate = false)
    {
        uint mask = 0;
        for (var slot = 0; slot < 24; slot++)
        {
            if ((instanceMask & (1u << slot)) == 0) continue;
            if (!TryGetSlotAddress(resultAddress, slot, strideBytes, out var slotAddress)) break;
            if (slotAddress < dataAddress || data.Length < sizeof(ulong)) continue;
            var offset = slotAddress - dataAddress;
            if (offset > (ulong)(data.Length - sizeof(ulong))) continue;
            if (BinaryPrimitives.ReadUInt64LittleEndian(data[(int)offset..]) != ExpectedSlotValue(expected, instanceMask, strideBytes, slot, aggregate))
                mask |= 1u << slot;
        }
        return mask;
    }

    internal static uint ReplacedSlots(ulong resultAddress, ulong expected, ulong dataAddress, ReadOnlySpan<byte> data,
        uint instanceMask, uint strideBytes, ReadBacking read, bool aggregate = false)
    {
        var changed = DifferentSlots(resultAddress, expected, dataAddress, data, instanceMask, strideBytes, aggregate);
        uint mask = 0;
        Span<byte> current = stackalloc byte[sizeof(ulong)];
        for (var slot = 0; slot < 24; slot++)
        {
            if ((changed & (1u << slot)) == 0) continue;
            if (TryGetSlotAddress(resultAddress, slot, strideBytes, out var slotAddress) && read(slotAddress, current) &&
                BinaryPrimitives.ReadUInt64LittleEndian(current) == ExpectedSlotValue(expected, instanceMask, strideBytes, slot, aggregate))
                mask |= 1u << slot;
        }
        return mask;
    }

    private static bool TryGetSlotAddress(ulong address, int slot, uint strideBytes, out ulong slotAddress)
    {
        var offset = (ulong)slot * strideBytes;
        slotAddress = 0;
        if (address > ulong.MaxValue - offset || address + offset > ulong.MaxValue - (sizeof(ulong) - 1)) return false;
        slotAddress = address + offset;
        return true;
    }

    private static ulong ExpectedSlotValue(ulong value, uint instanceMask, uint strideBytes, int slot, bool aggregate)
    {
        var current = aggregate && slot != 0 ? 1UL << 63 : value;
        var next = aggregate ? 1UL << 63 : value;
        // At a four-byte stride, the next enabled result can replace the upper word.
        return strideBytes == 4 && slot < 23 && (instanceMask & (1u << (slot + 1))) != 0
            ? (uint)current | ((ulong)(uint)next << 32)
            : current;
    }

    internal static void OnFlip(long version)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            if (++_flipCount % 60 != 0) return;
            Console.Error.WriteLine($"[GPU][TRACE] VisibilitySummary flip={version} published={_writes} unreadable={_readFailures} " +
                $"mismatches={_mismatches} predicates={_predicates} skipped={_skips} replacements={_downloads} " +
                $"lastWrite=[queue={_lastWrite.Queue} submit={_lastWrite.Submission} address=0x{_lastWrite.Address:X16} value=0x{_lastWrite.Value:X16}] " +
                $"lastPredicate=[queue={_lastPredicate.Queue} submit={_lastPredicate.Submission} address=0x{_lastPredicate.Address:X16} " +
                $"value=0x{_lastPredicate.Value:X16} condition={_lastPredicate.Condition} wait={_lastPredicate.Wait} skip={_lastPredicate.Skip}]");
        }
    }

    internal static void Occlusion(string message)
    {
        if (!Enabled) return;
        lock (_gate) Detail($"occlusion {message}");
    }

    private static void Detail(string message)
    {
        if (++_detailCount <= 8192) Console.Error.WriteLine($"[GPU][TRACE] Visibility {message}");
        else if (_detailCount == 8193) Console.Error.WriteLine("[GPU][TRACE] Visibility detail limit=8192; summaries continue.");
    }
}
