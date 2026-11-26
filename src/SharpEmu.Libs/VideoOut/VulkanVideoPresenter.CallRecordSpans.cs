// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.VideoOut;

// Call record spans. A record's image and sampler fields and buffer V#s lie a few dozen bytes past its argument, and
// the per-field preparation (PrepareCallRecordsOriginal) reads each of them on its own, every buffer V# twice
// (LeavesRecordOut, then the ranges), while a ray-traced dispatch can prepare thousands of records. Here each record is
// read once over the span of its callee's fields and V#s (CallRecordSpan), and the slot map, rows and ranges use
// scratch kept between dispatches. A span that cannot be read whole is read field by field, as the per-field
// preparation does. The rows and their order, the elements CallRecordElement assigns (in first use), the words table
// and the merged ranges are the per-field preparation's. On by default; SHARPEMU_CALL_RECORD_SPANS=0, or any value but
// 1, turns it off.
internal static unsafe partial class VulkanVideoPresenter
{
    internal enum CallRecordSpanMode { Off, On }

    internal static readonly CallRecordSpanMode ConfiguredCallRecordSpans =
        Environment.GetEnvironmentVariable("SHARPEMU_CALL_RECORD_SPANS") switch
        {
            null or "" or "1" => CallRecordSpanMode.On,
            _ => CallRecordSpanMode.Off,
        };

    // Tests: this thread's mode, whatever the configuration; and the element a field's words map to, instead of the
    // bindless heap (which needs a device).
    [ThreadStatic] internal static CallRecordSpanMode? CallRecordSpanOverride;
    [ThreadStatic] internal static Func<int, uint[], uint>? CallRecordElementForTests;

    internal static CallRecordSpanMode CallRecordSpans => CallRecordSpanOverride ?? ConfiguredCallRecordSpans;

    // The bytes past a record's argument that its callee's fields (32 bytes for an image, 16 for a sampler) and buffer V#s
    // (16 bytes) occupy: from the lowest immediate, Length bytes. Zero length when it reads none, or when they lie more than
    // MaxCallRecordSpan bytes apart; such records are read field by field.
    private readonly record struct CallRecordSpan(uint First, int Length);

    private const int MaxCallRecordSpan = 512;

    private static readonly ConditionalWeakTable<CallRecordCallee, StrongBox<CallRecordSpan>> CallRecordSpanTable = new();

    private static CallRecordSpan SpanOf(CallRecordCallee callee) => CallRecordSpanTable.GetValue(callee, static callee =>
    {
        var first = ulong.MaxValue;
        var end = 0ul;
        foreach (var field in callee.Fields)
        {
            first = Math.Min(first, field.Immediate);
            end = Math.Max(end, (ulong)field.Immediate + (field.Sampler ? 16u : 32u));
        }

        foreach (var buffer in callee.Buffers)
        {
            first = Math.Min(first, buffer.Immediate);
            end = Math.Max(end, (ulong)buffer.Immediate + 16u);
        }

        return new StrongBox<CallRecordSpan>(first == ulong.MaxValue || end - first > MaxCallRecordSpan
            ? default : new CallRecordSpan((uint)first, (int)(end - first)));
    }).Value;

    private sealed partial class Presenter
    {
        // A record the preparation keeps, with the offset of its span's bytes in the scratch (-1: read field by field).
        private readonly record struct CallRecordRow(uint Low, uint High, uint Callee, ulong Argument, int Bytes, uint First);

        private sealed class CallRecordScratch
        {
            public readonly Dictionary<ulong, int> SlotOf = new();
            public readonly List<CallRecordRow> Rows = new();
            public readonly List<DeviceAddressRange> Ranges = new();
            public readonly List<DeviceAddressRange> Merged = new();
            public byte[] Bytes = new byte[4096];
            public int ByteCount;
            public ulong[] Keys = new ulong[256];
            public DeviceAddressRange[] Sorted = new DeviceAddressRange[256];
            public ulong[] RowKeys = new ulong[256];
            public CallRecordRow[] SortedRows = new CallRecordRow[256];
            public CallRecordSpan[] Spans = new CallRecordSpan[16];
        }

        private CallRecordScratch? _callRecordScratch;

        // The words table the shader's call entries search and the memory their callees read through the records.
        internal (uint[] Words, DeviceAddressRange[] Ranges) PrepareCallRecords(ShaderProgramInfo program, ShaderStageResources stage) =>
            CallRecordSpans == CallRecordSpanMode.Off
                ? PrepareCallRecordsOriginal(program, stage)
                : PrepareCallRecordsSpanned(program, stage);

        // PrepareCallRecordsOriginal with each record read once over its span (see above).
        private (uint[] Words, DeviceAddressRange[] Ranges) PrepareCallRecordsSpanned(ShaderProgramInfo program, ShaderStageResources stage)
        {
            var callees = program.CallRecordCallees;
            var targets = stage.CallTargets ?? [];
            var scratch = _callRecordScratch ??= new CallRecordScratch();
            var slotOf = scratch.SlotOf;
            slotOf.Clear();
            for (var slot = 0; slot < targets.Length; slot++)
            {
                slotOf.TryAdd(targets[slot], slot);
            }

            var rows = scratch.Rows;
            rows.Clear();
            scratch.ByteCount = 0;
            if (scratch.Spans.Length < callees.Count) scratch.Spans = new CallRecordSpan[callees.Count];
            for (var slot = 0; slot < callees.Count; slot++)
            {
                scratch.Spans[slot] = SpanOf(callees[slot]);
            }

            // Rows sort on argument and callee slot packed in 64 bits while every argument fits in 48: the original's order
            // (argument high word, low word, then slot; one row per argument and slot).
            var packable = callees.Count <= ushort.MaxValue;
            foreach (var (target, argument) in stage.CallRecords ?? [])
            {
                if (!slotOf.TryGetValue(target, out var slot) || slot >= callees.Count)
                {
                    continue;
                }

                var callee = callees[slot];
                var span = scratch.Spans[slot];
                var bytes = -1;
                if (span.Length != 0)
                {
                    if (scratch.Bytes.Length - scratch.ByteCount < span.Length)
                    {
                        Array.Resize(ref scratch.Bytes, Math.Max(scratch.Bytes.Length * 2, scratch.ByteCount + span.Length));
                    }

                    if (_guestMemory.TryRead(argument + span.First, scratch.Bytes.AsSpan(scratch.ByteCount, span.Length)))
                    {
                        bytes = scratch.ByteCount;
                        scratch.ByteCount += span.Length;
                    }
                }

                var row = new CallRecordRow((uint)argument, (uint)(argument >> 32), (uint)slot, argument, bytes, span.First);
                if (!LeavesRecordOut(callee, target, program.Hash, scratch, row))
                {
                    rows.Add(row);
                    packable &= argument >> 48 == 0;
                }
            }

            if (packable && rows.Count > 1)
            {
                if (scratch.RowKeys.Length < rows.Count)
                {
                    scratch.RowKeys = new ulong[Math.Max(rows.Count, scratch.RowKeys.Length * 2)];
                    scratch.SortedRows = new CallRecordRow[scratch.RowKeys.Length];
                }

                for (var index = 0; index < rows.Count; index++)
                {
                    scratch.RowKeys[index] = rows[index].Argument << 16 | rows[index].Callee;
                    scratch.SortedRows[index] = rows[index];
                }

                Array.Sort(scratch.RowKeys, scratch.SortedRows, 0, rows.Count);
                for (var index = 0; index < rows.Count; index++)
                {
                    rows[index] = scratch.SortedRows[index];
                }
            }
            else
            {
                rows.Sort(static (left, right) =>
                    left.High != right.High ? left.High.CompareTo(right.High) :
                    left.Low != right.Low ? left.Low.CompareTo(right.Low) : left.Callee.CompareTo(right.Callee));
            }
            if (rows.Count > BindlessHeap.CallRecordMaximumRows)
            {
                if (_callRecordReports++ < 8)
                {
                    Console.Error.WriteLine(
                        $"[GPU][WARN] A dispatch has more call records than the shader searches: records={rows.Count} " +
                        $"searched={BindlessHeap.CallRecordMaximumRows} hash=0x{program.Hash:X16}.");
                }

                rows.RemoveRange((int)BindlessHeap.CallRecordMaximumRows, rows.Count - (int)BindlessHeap.CallRecordMaximumRows);
            }

            var fieldWords = 0;
            foreach (var row in rows)
            {
                fieldWords += callees[(int)row.Callee].Fields.Length;
            }

            var keyWords = (int)BindlessHeap.CallRecordKeyWords;
            var rowStarts = 1 + rows.Count * keyWords;
            var words = new uint[Math.Max(rowStarts + rows.Count + fieldWords, (int)BindlessHeap.CallRecordMinimumWords)];
            words[0] = (uint)rows.Count;
            var cursor = rowStarts + rows.Count;
            var ranges = scratch.Ranges;
            ranges.Clear();
            Span<uint> descriptor = stackalloc uint[8];
            var argumentBase = 0ul;
            var argumentEnd = 0ul;
            var argumentOpen = false;
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                words[1 + index * keyWords] = row.Low;
                words[2 + index * keyWords] = row.High;
                words[3 + index * keyWords] = row.Callee;
                words[rowStarts + index] = (uint)cursor;
                var callee = callees[(int)row.Callee];
                foreach (var field in callee.Fields)
                {
                    // As CallRecordElement(field, argument): the words past a sampler's four stay zero in the key.
                    descriptor.Clear();
                    words[cursor++] = ReadRecordWords(scratch, row, field.Immediate, descriptor[..(field.Sampler ? 4 : 8)])
                        ? CallRecordElement(field, descriptor)
                        : BindlessHeap.CallRecordNullIndex;
                }

                if (callee.ArgumentBytes != 0)
                {
                    // An argument range that overlaps or meets the open one (rows ascend by argument) joins it, as the merge
                    // joins them; one whose end wraps is added on its own.
                    var end = row.Argument + callee.ArgumentBytes;
                    if (argumentOpen && row.Argument <= argumentEnd && end > row.Argument)
                    {
                        argumentEnd = Math.Max(argumentEnd, end);
                    }
                    else
                    {
                        if (argumentOpen)
                        {
                            ranges.Add(new DeviceAddressRange(uint.MaxValue, argumentBase, argumentEnd - argumentBase, true, false));
                            argumentOpen = false;
                        }

                        if (end > row.Argument)
                        {
                            (argumentBase, argumentEnd, argumentOpen) = (row.Argument, end, true);
                        }
                        else
                        {
                            ranges.Add(new DeviceAddressRange(uint.MaxValue, row.Argument, callee.ArgumentBytes, true, false));
                        }
                    }
                }

                foreach (var field in callee.Buffers)
                {
                    var buffer = descriptor[..4];
                    if (!ReadRecordWords(scratch, row, field.Immediate, buffer))
                    {
                        continue;
                    }

                    var address = buffer[0] | ((ulong)(buffer[1] & 0xFFFF) << 32);
                    var stride = (buffer[1] >> 16) & 0x3FFF;
                    var size = (ulong)buffer[2] * Math.Max(stride, 1u);
                    if (Gpu.Pipelines.ShaderProgramCache.ClassifyRecordBuffer(address, size, field.Written) ==
                        Gpu.Pipelines.ShaderProgramCache.RecordBuffer.Resident)
                    {
                        ranges.Add(new DeviceAddressRange(uint.MaxValue, address, size, true, field.Written));
                    }
                }
            }

            if (argumentOpen)
            {
                ranges.Add(new DeviceAddressRange(uint.MaxValue, argumentBase, argumentEnd - argumentBase, true, false));
            }

            ranges.AddRange(stage.CallRecordRanges ?? []);
            return (words, MergeCallRecordRangesKeyed(scratch));
        }

        // MergeCallRecordRanges, sorted on 64-bit keys (written in the top bit, then the base) instead of a comparison. Ranges
        // with equal keys join into the same range in any order, so the result is the same. A base with its top bit set
        // takes the original merge.
        private DeviceAddressRange[] MergeCallRecordRangesKeyed(CallRecordScratch scratch)
        {
            var ranges = scratch.Ranges;
            var count = ranges.Count;
            if (count == 0)
            {
                return [];
            }

            if (scratch.Keys.Length < count)
            {
                scratch.Keys = new ulong[Math.Max(count, scratch.Keys.Length * 2)];
                scratch.Sorted = new DeviceAddressRange[scratch.Keys.Length];
            }

            var keys = scratch.Keys;
            var sorted = scratch.Sorted;
            for (var index = 0; index < count; index++)
            {
                var range = ranges[index];
                if ((range.Base >> 63) != 0)
                {
                    return MergeCallRecordRanges(ranges);
                }

                keys[index] = (range.Written ? 1ul << 63 : 0) | range.Base;
                sorted[index] = range;
            }

            Array.Sort(keys, sorted, 0, count);
            var merged = scratch.Merged;
            merged.Clear();
            var current = sorted[0];
            for (var index = 1; index < count; index++)
            {
                var range = sorted[index];
                var end = current.Base + current.Size;
                if (range.Written == current.Written &&
                    (range.Base <= end || (range.Base - end <= 4096 && _guestMemory.CanRead(end, range.Base - end))))
                {
                    current = current with { Size = Math.Max(end, range.Base + range.Size) - current.Base };
                    continue;
                }

                merged.Add(current);
                current = range;
            }

            merged.Add(current);
            return [.. merged];
        }

        // LeavesRecordOut(callee, target, argument, hash) on the record's span.
        private bool LeavesRecordOut(CallRecordCallee callee, ulong target, ulong hash, CallRecordScratch scratch, in CallRecordRow row)
        {
            Span<uint> buffer = stackalloc uint[4];
            foreach (var field in callee.Buffers)
            {
                if (!ReadRecordWords(scratch, row, field.Immediate, buffer))
                {
                    continue;
                }

                var address = buffer[0] | ((ulong)(buffer[1] & 0xFFFF) << 32);
                var size = (ulong)buffer[2] * Math.Max((buffer[1] >> 16) & 0x3FFF, 1u);
                if (Gpu.Pipelines.ShaderProgramCache.ClassifyRecordBuffer(address, size, field.Written) ==
                    Gpu.Pipelines.ShaderProgramCache.RecordBuffer.LeaveRecordOut)
                {
                    if (_callRecordReports++ < 8)
                    {
                        Console.Error.WriteLine($"[GPU][WARN][LINKED_PROGRAM] A call record's buffer describes no buffer; its call returns at once: " +
                            $"hash=0x{hash:X16} target=0x{target:X} argument=0x{row.Argument:X} base=0x{address:X} size=0x{size:X}.");
                    }

                    return true;
                }
            }

            return false;
        }

        // The words at an immediate past a record's argument: from its span when it was read whole, else read now as the
        // original reads them.
        private bool ReadRecordWords(CallRecordScratch scratch, in CallRecordRow row, uint immediate, Span<uint> destination)
        {
            var target = MemoryMarshal.AsBytes(destination);
            if (row.Bytes < 0)
            {
                return _guestMemory.TryRead(row.Argument + immediate, target);
            }

            scratch.Bytes.AsSpan(row.Bytes + (int)(immediate - row.First), target.Length).CopyTo(target);
            return true;
        }
    }
}
