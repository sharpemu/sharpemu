// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.VideoOut;

// The call records of a program linked with run-time record arguments (CallRecordSelector). For every record of a dispatch's
// tables, the host reads each image and sampler its callee reads through the record's argument, gives each distinct
// descriptor an element of set 1, and writes the table the shader's call entries
// search (BindlessHeap.CallRecordKeyWords). The memory a callee reads through its record, its own reads and its buffers, is
// made resident through the device-address table as a planned range would be.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // A descriptor's words, two to a value; a sampler leaves the last two zero.
        private readonly record struct CallRecordWords(ulong First, ulong Second, ulong Third, ulong Fourth);

        private readonly Dictionary<(int Slot, CallRecordWords Words), HeapEntry> _callRecordEntries = new();
        private readonly uint[] _callRecordNext = new uint[BindlessHeap.ArrayCount];
        private int _callRecordReports;

        internal (uint[] Words, DeviceAddressRange[] Ranges) PrepareCallRecords(ShaderProgramInfo program, ShaderStageResources stage)
        {
            var callees = program.CallRecordCallees;
            var targets = stage.CallTargets ?? [];
            var slotOf = new Dictionary<ulong, int>(targets.Length);
            for (var slot = 0; slot < targets.Length; slot++)
            {
                slotOf.TryAdd(targets[slot], slot);
            }

            var rows = new List<(uint Low, uint High, uint Callee, ulong Argument)>(stage.CallRecords?.Length ?? 0);
            foreach (var (target, argument) in stage.CallRecords ?? [])
            {
                if (slotOf.TryGetValue(target, out var slot) && slot < callees.Count)
                {
                    rows.Add(((uint)argument, (uint)(argument >> 32), (uint)slot, argument));
                }
            }

            rows.Sort(static (left, right) =>
                left.High != right.High ? left.High.CompareTo(right.High) :
                left.Low != right.Low ? left.Low.CompareTo(right.Low) : left.Callee.CompareTo(right.Callee));
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
            var ranges = new List<DeviceAddressRange>();
            Span<uint> buffer = stackalloc uint[4];
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
                    words[cursor++] = CallRecordElement(field, row.Argument);
                }

                if (callee.ArgumentBytes != 0)
                {
                    ranges.Add(new DeviceAddressRange(uint.MaxValue, row.Argument, callee.ArgumentBytes, true, false));
                }

                foreach (var field in callee.Buffers)
                {
                    if (!_guestMemory.TryRead(row.Argument + field.Immediate, MemoryMarshal.AsBytes(buffer)))
                    {
                        continue;
                    }

                    var address = buffer[0] | ((ulong)(buffer[1] & 0xFFFF) << 32);
                    var stride = (buffer[1] >> 16) & 0x3FFF;
                    var size = (ulong)buffer[2] * Math.Max(stride, 1u);
                    if (size != 0 && address != 0)
                    {
                        ranges.Add(new DeviceAddressRange(uint.MaxValue, address, size, true, field.Written));
                    }
                }
            }

            ranges.AddRange(stage.CallRecordRanges ?? []);
            return (words, MergeCallRecordRanges(ranges));
        }

        // The element of set 1 that holds the descriptor a record's callee reads Immediate bytes past its argument: one per
        // distinct descriptor and array, resolved when it is first seen.
        private uint CallRecordElement(CallRecordField field, ulong argument)
        {
            Span<uint> words = stackalloc uint[8];
            var count = field.Sampler ? 4 : 8;
            if (!_guestMemory.TryRead(argument + field.Immediate, MemoryMarshal.AsBytes(words[..count])))
            {
                return BindlessHeap.CallRecordNullIndex;
            }

            static ulong Pair(uint low, uint high) => low | ((ulong)high << 32);
            var key = (field.Slot, new CallRecordWords(Pair(words[0], words[1]), Pair(words[2], words[3]),
                Pair(words[4], words[5]), Pair(words[6], words[7])));
            if (_callRecordEntries.TryGetValue(key, out var entry))
            {
                return entry.Index;
            }

            var next = _callRecordNext[field.Slot];
            if (next >= BindlessHeap.CallRecordNullIndex)
            {
                if (_callRecordReports++ < 8)
                {
                    Console.Error.WriteLine($"[GPU][WARN] The call record elements of a bindless heap array are used up: slot={field.Slot}.");
                }

                return BindlessHeap.CallRecordNullIndex;
            }

            _callRecordNext[field.Slot] = next + 1;
            entry = new HeapEntry { Slot = field.Slot, Index = next, Words = words[..count].ToArray() };
            _callRecordEntries.Add(key, entry);
            ResolveHeapEntry(entry);
            return entry.Index;
        }

        // Ranges that overlap or meet, or lie apart only by readable memory within a page, become one, so a dispatch makes few
        // ranges resident for many records.
        private DeviceAddressRange[] MergeCallRecordRanges(List<DeviceAddressRange> ranges)
        {
            if (ranges.Count == 0)
            {
                return [];
            }

            ranges.Sort(static (left, right) => left.Written != right.Written ? left.Written.CompareTo(right.Written) : left.Base.CompareTo(right.Base));
            var merged = new List<DeviceAddressRange>(ranges.Count);
            var current = ranges[0];
            for (var index = 1; index < ranges.Count; index++)
            {
                var range = ranges[index];
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
    }
}
