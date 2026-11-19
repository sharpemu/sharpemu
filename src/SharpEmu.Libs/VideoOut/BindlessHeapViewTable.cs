// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.VideoOut;

// The runs of set-1 elements the heap views of the draws hold (see BindlessHeap, HeapView): one run per heap V# and
// descriptor offset (BindlessHeapDescriptor.ViewKey), as long as its records. A view that is read again keeps its run, so the elements the shaders marked
// stay resolved. A new view takes over the run of a view of its shape that no pending command reads any more: a table the
// guest allocates for each frame is the same records at another address, and most of its elements stay as they are.
// Otherwise it takes free elements, then the elements of the views no pending command reads, least recently used first,
// and at last shares the run of a view of its shape that is still read, as one heap that moved (a title's whole texture
// heap, kept in two copies, is larger than half of set 1).
internal sealed class BindlessHeapViewTable(uint capacity)
{
    internal sealed class Region
    {
        // The heap whose records the run's elements hold now.
        public required BindlessHeapDescriptor Heap { get; set; }
        // The shader reads up to View.Records, the most records a draw has named for the heap; the run holds Size elements,
        // at least that many, so a heap that grows keeps its run until it outgrows the room.
        public required HeapView View { get; set; }
        public uint Size { get; init; }
        public ulong LastUsedTick;
        public long LastUsedFrame;

        public uint Extent => Math.Max(Size, View.Records);

        public bool Holds(uint element) => element >= View.First && element - View.First < Extent;

        // The run's descriptors as a heap of their own, for reading them in guest memory: the first one's address, a
        // stride apart, each of the size its heap reads (an R128 view's are 16 bytes).
        public BindlessHeapDescriptor Descriptors =>
            Heap with { Base = View.DescriptorAddress(Heap, 0), Records = View.Records, Stride = View.Stride, Immediate = 0 };
    }

    // Sorted by first element; runs never overlap.
    private readonly List<Region> _regions = [];
    private readonly Dictionary<BindlessHeapDescriptor, Region> _byHeap = [];

    public IReadOnlyList<Region> Regions => _regions;

    // The run of the heap view. A view without one takes over the run of a view of its shape last used at or before
    // completedTick (moved: the run holds this heap's records from now on, and its elements are to be compared with them),
    // or free elements, or those of the least recently used views no pending command reads (released sees each one before
    // its elements are taken), or shares the run of a view of its shape that is still read (moved). Null when the view has
    // no records or the table has no room for it.
    public Region? Acquire(in BindlessHeapDescriptor descriptor, ulong tick, ulong completedTick, Action<Region> released, out bool moved, long frame = 0) =>
        Acquire(descriptor, tick, completedTick, released, out moved, out _, frame);

    // grownFrom is the run a packed heap outgrew: the view's new run holds the same records from the same first record on,
    // so the caller can give it that run's elements at once.
    public Region? Acquire(in BindlessHeapDescriptor descriptor, ulong tick, ulong completedTick, Action<Region> released, out bool moved,
        out Region? grownFrom, long frame = 0)
    {
        moved = false;
        grownFrom = null;
        var heap = descriptor.ViewKey;
        var records = Math.Min(descriptor.ViewRecords, capacity);
        var size = records;
        if (_byHeap.TryGetValue(heap, out var region))
        {
            if (region.Extent >= records)
            {
                // A heap that grew within its run's room: the shader reads its new records from now on.
                if (records > region.View.Records)
                {
                    region.View = region.View with { Records = records };
                }

                region.LastUsedTick = tick;
                region.LastUsedFrame = frame;
                return region;
            }

            // The guest grew the heap past the run (a texture heap it streams more textures into). The run stays for the
            // commands that read it until it is idle; the view takes a run with room to grow again.
            grownFrom = region;
            _byHeap.Remove(heap);
            size = Math.Min(records + records / 8, capacity);
        }

        if (heap.Base == 0 || records == 0)
        {
            return null;
        }

        var shape = HeapView.Of(heap, 0, records);
        if (grownFrom is null && FindShape(heap, shape, tick, completedTick) is { } idle)
        {
            moved = true;
            return Move(idle, heap, tick, frame);
        }

        int position;
        uint first;
        while (!TryFindGap(size, out position, out first))
        {
            Region? oldest = null;
            foreach (var candidate in _regions)
            {
                if (candidate != grownFrom && candidate.LastUsedTick <= completedTick && candidate.LastUsedTick < tick &&
                    (oldest is null || candidate.LastUsedTick < oldest.LastUsedTick))
                {
                    oldest = candidate;
                }
            }

            if (oldest is null)
            {
                if (grownFrom is not null)
                {
                    if (size > records)
                    {
                        size = records;
                        continue;
                    }

                    // No room for a larger run: the view keeps the one it has.
                    _byHeap.Add(heap, grownFrom);
                    grownFrom.LastUsedTick = tick;
                    grownFrom.LastUsedFrame = frame;
                    var kept = grownFrom;
                    grownFrom = null;
                    return kept;
                }

                if (FindShape(heap, shape, ulong.MaxValue, ulong.MaxValue) is not { } shared)
                {
                    return null;
                }

                moved = true;
                return Move(shared, heap, tick, frame);
            }

            Release(oldest, released);
        }

        region = new Region { Heap = heap, View = HeapView.Of(heap, first, records), Size = size, LastUsedTick = tick, LastUsedFrame = frame };
        _regions.Insert(position, region);
        _byHeap.Add(heap, region);
        return region;
    }

    // The least recently used run of a view of this shape last used before tick and at or before completedTick.
    private Region? FindShape(in BindlessHeapDescriptor heap, HeapView shape, ulong tick, ulong completedTick)
    {
        Region? found = null;
        foreach (var candidate in _regions)
        {
            if (candidate.Heap.Sampler == heap.Sampler && candidate.Heap.R128 == heap.R128 && candidate.View with { First = 0 } == shape &&
                candidate.LastUsedTick <= completedTick && candidate.LastUsedTick < tick &&
                (found is null || candidate.LastUsedTick < found.LastUsedTick))
            {
                found = candidate;
            }
        }

        return found;
    }

    private Region Move(Region region, in BindlessHeapDescriptor heap, ulong tick, long frame)
    {
        ForgetHeap(region);
        region.Heap = heap;
        region.LastUsedTick = tick;
        region.LastUsedFrame = frame;
        _byHeap.Add(heap, region);
        return region;
    }

    // The run that holds the element, or null.
    public Region? Find(uint element)
    {
        var low = 0;
        var high = _regions.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            var region = _regions[middle];
            if (element < region.View.First)
            {
                high = middle - 1;
            }
            else if (element - region.View.First >= region.Extent)
            {
                low = middle + 1;
            }
            else
            {
                return region;
            }
        }

        return null;
    }

    // Gives up runs idle in both submission ticks and guest frames, once no pending command reads them.
    public void ReleaseIdle(ulong idleBefore, ulong completedTick, Action<Region> released, long idleBeforeFrame = long.MaxValue)
    {
        for (var index = _regions.Count - 1; index >= 0; index--)
        {
            var region = _regions[index];
            if (region.LastUsedTick < idleBefore && region.LastUsedTick <= completedTick && region.LastUsedFrame < idleBeforeFrame)
            {
                Release(region, released);
            }
        }
    }

    private void Release(Region region, Action<Region> released)
    {
        released(region);
        _regions.Remove(region);
        ForgetHeap(region);
    }

    // A run a grown heap left behind no longer names the heap: the heap's current run does.
    private void ForgetHeap(Region region)
    {
        if (_byHeap.TryGetValue(region.Heap, out var current) && current == region)
        {
            _byHeap.Remove(region.Heap);
        }
    }

    private bool TryFindGap(uint records, out int position, out uint first)
    {
        first = 0;
        for (position = 0; position < _regions.Count; position++)
        {
            var region = _regions[position];
            if (region.View.First - first >= records)
            {
                return true;
            }

            first = region.View.First + region.Extent;
        }

        return capacity - first >= records;
    }
}
