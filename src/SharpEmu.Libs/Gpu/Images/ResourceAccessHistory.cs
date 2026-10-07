// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

internal readonly record struct ResourceHistoryEntry(
    ulong Sequence, ulong Tick, int Queue, ulong Submission, string Operation,
    ulong Address, ulong Size, Format Format, Extent3D Extent, ImageRole Role,
    ulong LastSequence = 0, ulong Notifications = 1, ulong Source = 0, ulong ShaderHash = 0);

// The image-cache lock protects the independent bounded histories.
// An active filter keeps only overlapping events, so unrelated writes cannot evict them.
internal sealed class ResourceAccessHistory(Func<IReadOnlyList<GuestSpan>?>? filter = null)
{
    internal sealed class Ring(int capacity)
    {
        private readonly ResourceHistoryEntry[] _entries = new ResourceHistoryEntry[capacity];
        private int _next;
        internal int Count { get; private set; }
        internal ulong Dropped { get; private set; }
        internal ulong Coalesced { get; private set; }

        internal void Add(ResourceHistoryEntry entry, bool mergeWrites)
        {
            if (mergeWrites && Count != 0)
            {
                var previousIndex = (_next + _entries.Length - 1) % _entries.Length;
                var previous = _entries[previousIndex];
                var start = Math.Min(previous.Address, entry.Address);
                var end = Math.Max(previous.Address + previous.Size, entry.Address + entry.Size);
                if (previous.LastSequence + 1 == entry.Sequence && previous.Operation == entry.Operation &&
                    previous.Source == entry.Source && previous.ShaderHash == entry.ShaderHash &&
                    previous.Tick == entry.Tick && previous.Queue == entry.Queue && previous.Submission == entry.Submission &&
                    (start >> 12) == ((end - 1) >> 12))
                {
                    // The range is an envelope of notifications, not proof that every byte was written.
                    _entries[previousIndex] = previous with
                    {
                        Address = start, Size = end - start, LastSequence = entry.Sequence,
                        Notifications = previous.Notifications + 1,
                    };
                    Coalesced++;
                    return;
                }
            }
            if (Count == _entries.Length) Dropped += _entries[_next].Notifications;
            else Count++;
            _entries[_next] = entry with { LastSequence = entry.Sequence };
            _next = (_next + 1) % _entries.Length;
        }

        internal IEnumerable<ResourceHistoryEntry> Read()
        {
            for (var index = 0; index < Count; index++)
                yield return _entries[(_next - Count + index + _entries.Length) % _entries.Length];
        }
    }

    internal Ring Lifecycle { get; } = new(8192);
    internal Ring Bindings { get; } = new(16384);
    internal Ring Writes { get; } = new(4096);
    internal Ring GpuWrites { get; } = new(4096);
    internal Ring CommandWrites { get; } = new(16384);
    internal Ring ShaderWrites { get; } = new(16384);
    internal Ring DrawTargets { get; } = new(16384);

    internal void Record(ResourceHistoryEntry entry)
    {
        if (entry.Size == 0 || entry.Size > ulong.MaxValue - entry.Address) return;
        if (filter?.Invoke() is { } ranges && !ImageTraceRange.Overlaps(ranges, entry.Address, entry.Size)) return;
        if (entry.Operation.StartsWith("command-", StringComparison.Ordinal))
            CommandWrites.Add(entry, true);
        else if (entry.Operation.StartsWith("draw-target-", StringComparison.Ordinal))
            DrawTargets.Add(entry, true);
        else if (entry.Operation == "shader-buffer-write")
            ShaderWrites.Add(entry, true);
        else if (entry.Operation is "gpu-buffer-write-notification" or "buffer-fill")
            GpuWrites.Add(entry, true);
        else if (entry.Operation == "cpu-write-notification")
            Writes.Add(entry, true);
        else if (entry.Operation is "create" or "release" or "unmap")
            Lifecycle.Add(entry, false);
        else
            Bindings.Add(entry, false);
    }
}
