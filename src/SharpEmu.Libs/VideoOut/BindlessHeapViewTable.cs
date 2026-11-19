// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.VideoOut;

// The runs of set-1 elements the heap views of the draws hold (see BindlessHeap, HeapView): one run per heap V# and
// descriptor offset (BindlessHeapDescriptor.ViewKey()), at least as long as its records. A view that is read again keeps its run, so the elements the
// shaders marked stay resolved. A new view takes over a run long enough for it of a view of its shape that no pending command reads any more: a
// table the guest allocates for each frame is the same records at another address, and most of its elements stay as they are, even when the
// frame's table has a few records more or less (a terrain's tile list grows and shrinks while the camera moves). A view longer than every run of
// its shape gets a longer run, so the counts that follow fit in it. A packed heap is one view at any count (a texture heap the guest
// streams more textures into): when it outgrows its run it takes a longer one, and the old run stays for the commands that read it.
// Otherwise it takes free elements, then the elements of the views no pending command reads, least recently used first,
// and at last shares the run of a view of its shape that is still read, as one heap that moved (a title's whole texture
// heap, kept in two copies, is larger than half of set 1).
internal sealed class BindlessHeapViewTable(uint capacity)
{
    internal sealed class Region
    {
        // The heap whose records the run's elements hold now.
        public required BindlessHeapDescriptor Heap { get; set; }
        // The view the run serves now: its records are the run's first elements.
        public required HeapView View { get; set; }
        // The elements the run holds, its view's records and the ones it keeps for a longer view of its shape.
        public required uint Length { get; init; }
        // The set-1 arrays (BindlessHeap slots, one bit each) the shaders have read the run's records through. A new run
        // starts with those of the latest run of its shape: the caller resolves a small view's records in these arrays
        // before its first read instead of waiting for the shader's marks.
        public uint Slots;
        // The records and arrays the caller has resolved every record of (a small view): it adds the missing entries again
        // only when either grows.
        public uint ResolvedRecords;
        public uint ResolvedSlots;
        // Set by Acquire: a command not yet completed read the run before this use, so its elements must not change
        // (Fork gives the view another run instead).
        public bool ReadByPending;
        public ulong LastUsedTick;
        public long LastUsedFrame;

        public bool Holds(uint element) => element >= View.First && element - View.First < View.Records;

        // The run's descriptors as a heap of their own, for reading them in guest memory: the first one's address, a
        // stride apart, each of the size its heap reads (an R128 view's are 16 bytes).
        public BindlessHeapDescriptor Descriptors =>
            Heap with { Base = View.DescriptorAddress(Heap, 0), Records = View.Records, Stride = View.Stride, Immediate = 0 };
    }

    // Sorted by first element; runs never overlap.
    private readonly List<Region> _regions = [];
    private readonly Dictionary<BindlessHeapDescriptor, Region> _byHeap = [];

    public IReadOnlyList<Region> Regions => _regions;

    // The run of the heap view. A view without one takes over a run long enough for it of a view of its shape last used at or
    // before completedTick (moved: the run holds this heap's records from now on, and its elements are to be compared with them),
    // or free elements, or those of the least recently used views no pending command reads (released sees each one before
    // its elements are taken), or shares the run of a view of its shape that is still read (moved). Null when the view has
    // no records or the table has no room for it.
    public Region? Acquire(in BindlessHeapDescriptor descriptor, ulong tick, ulong completedTick, Action<Region> released, out bool moved, long frame = 0) =>
        Acquire(descriptor, tick, completedTick, released, out moved, out _, frame);

    // grownFrom is the run a packed heap outgrew: the view's new run holds the same records from the same first record on,
    // so the caller can give it that run's elements at once.
    public Region? Acquire(in BindlessHeapDescriptor descriptor, ulong tick, ulong completedTick, Action<Region> released, out bool moved,
        out Region? grownFrom, long frame = 0) =>
        AcquireKey(descriptor.ViewKey(), Math.Min(descriptor.ViewRecords, capacity), tick, completedTick, released, out moved, out grownFrom, frame);

    // A view whose records changed while commands not yet completed still read its run: the view gets another run (a free
    // one, or that of an idle view of its shape, moved), and the old run keeps its elements for those commands until it is
    // idle; it is then taken over or released like any other. Null when no other run can be had: the caller updates the
    // run in place.
    public Region? Fork(Region region, ulong tick, ulong completedTick, Action<Region> released, out bool moved, long frame = 0)
    {
        moved = false;
        var heap = region.Heap;
        if (!_byHeap.TryGetValue(heap, out var keyed) || !ReferenceEquals(keyed, region))
        {
            return null;
        }

        _byHeap.Remove(heap);
        var fork = AcquireKey(heap, region.View.Records, tick, completedTick, released, out moved, out _, frame);
        if (fork is null || ReferenceEquals(fork, region))
        {
            _byHeap[heap] = region;
            moved = false;
            return null;
        }

        return fork;
    }

    private Region? AcquireKey(in BindlessHeapDescriptor heap, uint records, ulong tick, ulong completedTick, Action<Region> released,
        out bool moved, out Region? grownFrom, long frame)
    {
        moved = false;
        grownFrom = null;
        if (_byHeap.TryGetValue(heap, out var region))
        {
            if (region.Length >= records)
            {
                // A heap that grew within its run's room: the shader reads its new records from now on.
                if (records > region.View.Records)
                {
                    region.View = region.View with { Records = records };
                }

                region.ReadByPending = region.LastUsedTick > completedTick;
                region.LastUsedTick = tick;
                region.LastUsedFrame = frame;
                return region;
            }

            // The guest grew a packed heap past the run (a texture heap it streams more textures into). The run stays for
            // the commands that read it until it is idle; the view takes a run with room to grow again.
            grownFrom = region;
            _byHeap.Remove(heap);
        }

        if (heap.Base == 0 || records == 0)
        {
            return null;
        }

        var shape = HeapView.Of(heap, 0, records);
        if (grownFrom is null && FindShape(heap, shape, tick, completedTick) is { } idle)
        {
            moved = true;
            idle.ReadByPending = false;
            return Move(idle, heap, records, tick, frame);
        }

        // The room to grow is taken from free elements only: no view gives its elements up for it.
        var length = grownFrom is null ? RunLength(heap, shape) : GrownLength(records);
        if (length > records && !TryFindGap(length, out _, out _))
        {
            length = records;
        }

        var slots = grownFrom?.Slots ?? LatestShape(heap, shape)?.Slots ?? 0;
        int position;
        uint first;
        while (!TryFindGap(length, out position, out first))
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
                    // No room for a longer run: the view keeps the one it has.
                    _byHeap.Add(heap, grownFrom);
                    grownFrom.ReadByPending = grownFrom.LastUsedTick > completedTick;
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
                shared.ReadByPending = true;
                return Move(shared, heap, records, tick, frame);
            }

            Release(oldest, released);
        }

        region = new Region { Heap = heap, View = HeapView.Of(heap, first, records), Length = length, Slots = slots, LastUsedTick = tick, LastUsedFrame = frame };
        _regions.Insert(position, region);
        _byHeap.Add(heap, region);
        return region;
    }

    // The least recently used run long enough for a view of this shape last used before tick and at or before completedTick.
    private Region? FindShape(in BindlessHeapDescriptor heap, HeapView shape, ulong tick, ulong completedTick)
    {
        Region? found = null;
        foreach (var candidate in _regions)
        {
            if (SameShape(candidate, heap, shape) && candidate.Length >= shape.Records &&
                candidate.LastUsedTick <= completedTick && candidate.LastUsedTick < tick &&
                (found is null || candidate.LastUsedTick < found.LastUsedTick))
            {
                found = candidate;
            }
        }

        return found;
    }

    // The most recently used run of a view of this shape, whatever its length.
    private Region? LatestShape(in BindlessHeapDescriptor heap, HeapView shape)
    {
        Region? found = null;
        foreach (var candidate in _regions)
        {
            if (SameShape(candidate, heap, shape) && (found is null || candidate.LastUsedTick > found.LastUsedTick))
            {
                found = candidate;
            }
        }

        return found;
    }

    // Records of the same stride, field and descriptor size: a view's record count is not part of its shape.
    private static bool SameShape(Region candidate, in BindlessHeapDescriptor heap, HeapView shape) =>
        candidate.Heap.Sampler == heap.Sampler && candidate.Heap.R128 == heap.R128 &&
        candidate.View with { First = 0, Records = 0 } == shape with { Records = 0 };

    // A view's first run is as long as its records. A view longer than a run of its shape belongs to a table whose count
    // changes from frame to frame: its run gets a quarter more, rounded up to RunGranularity, when free elements allow,
    // so the next counts fit in it instead of taking a new run each time the count grows.
    private const uint RunGranularity = 32;

    private uint RunLength(in BindlessHeapDescriptor heap, HeapView shape)
    {
        var records = shape.Records;
        foreach (var candidate in _regions)
        {
            if (SameShape(candidate, heap, shape) && candidate.Length < records)
            {
                var grown = ((ulong)records + records / 4 + RunGranularity - 1) / RunGranularity * RunGranularity;
                return (uint)Math.Min(grown, capacity);
            }
        }

        return records;
    }

    // A packed heap that outgrew its run: an eighth more.
    private uint GrownLength(uint records) => (uint)Math.Min((ulong)records + records / 8, capacity);

    // A run forked away from its view, or one a grown heap left behind, is no longer the one its heap names.
    private void Unkey(Region region)
    {
        if (_byHeap.TryGetValue(region.Heap, out var keyed) && ReferenceEquals(keyed, region))
        {
            _byHeap.Remove(region.Heap);
        }
    }

    private Region Move(Region region, in BindlessHeapDescriptor heap, uint records, ulong tick, long frame)
    {
        Unkey(region);
        region.View = region.View with { Records = Math.Min(records, region.Length) };
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
            else if (element - region.View.First >= region.Length)
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
        Unkey(region);
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

            first = region.View.First + region.Length;
        }

        return capacity - first >= records;
    }
}
