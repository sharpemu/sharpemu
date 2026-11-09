// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

internal readonly record struct MetadataAccessEntry(
    ulong Sequence, ulong Tick, int QueueId, ulong SubmissionId, string Operation,
    ulong Address, ulong Size, ulong TrackedImageBaseAddress, string Kind, uint Value);

// The image-cache lock protects this bounded diagnostic history.
internal sealed class MetadataAccessHistory
{
    internal const int AddressCapacity = 256;
    internal const int EventCapacity = 32;
    private readonly SortedDictionary<ulong, History> _histories = new();
    private readonly Queue<ulong> _insertionOrder = new();

    private sealed class History
    {
        internal ulong Size;
        internal readonly MetadataAccessEntry[] Entries = new MetadataAccessEntry[EventCapacity];
        internal int Next;
        internal int Count;

        internal void Add(in MetadataAccessEntry entry)
        {
            if (Count != 0)
            {
                var previousIndex = (Next + EventCapacity - 1) % EventCapacity;
                var previous = Entries[previousIndex];
                if (previous.Operation == entry.Operation && previous.Address == entry.Address &&
                    previous.Size == entry.Size && previous.TrackedImageBaseAddress == entry.TrackedImageBaseAddress &&
                    previous.Kind == entry.Kind && previous.Value == entry.Value)
                {
                    Entries[previousIndex] = entry;
                    return;
                }
            }

            Entries[Next] = entry;
            Next = (Next + 1) % EventCapacity;
            Count = Math.Min(Count + 1, EventCapacity);
        }
    }

    internal void Record(ulong metadataAddress, ulong metadataSize, in MetadataAccessEntry entry)
    {
        if (!_histories.TryGetValue(metadataAddress, out var history))
        {
            if (_histories.Count == AddressCapacity)
            {
                _histories.Remove(_insertionOrder.Dequeue());
            }

            history = new History();
            _histories.Add(metadataAddress, history);
            _insertionOrder.Enqueue(metadataAddress);
        }

        history.Size = Math.Max(history.Size, Math.Max(metadataSize, 1));
        history.Add(entry);
    }

    internal void RecordRange(in MetadataAccessEntry entry)
    {
        if (_histories.Count == 0) return;
        if (entry.Size == 0 || entry.Size > ulong.MaxValue - entry.Address)
        {
            return;
        }

        var end = entry.Address + entry.Size;
        foreach (var (address, history) in _histories)
        {
            if (address >= end) break;
            if (address >= entry.Address || entry.Address - address < history.Size)
            {
                history.Add(entry);
            }
        }
    }

    internal IEnumerable<MetadataAccessEntry> Read(ulong address)
    {
        if (!_histories.TryGetValue(address, out var history)) yield break;
        for (var index = 0; index < history.Count; index++)
        {
            yield return history.Entries[(history.Next - history.Count + index + EventCapacity) % EventCapacity];
        }
    }
}
