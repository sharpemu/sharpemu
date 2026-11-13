// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// Callers serialize access. The tree does not change range ownership or locking.
public sealed class AvlSpanSet
{
    private sealed class Node(ulong start, ulong end)
    {
        public ulong Start = start;
        public ulong End = end;
        public Node? Left;
        public Node? Right;
        public int Height = 1;
        public int Count = 1;
    }

    // A new snapshot copies the last one's spans outside the extent of every span removed or inserted since, and walks the
    // tree only inside it. SHARPEMU_SPAN_SNAPSHOT_SPLICE=0 walks the whole tree.
    private static readonly bool SpliceSnapshots = Environment.GetEnvironmentVariable("SHARPEMU_SPAN_SNAPSHOT_SPLICE") != "0";

    private Node? _root;
    private SpanSnapshot? _snapshot = SpanSnapshot.Empty;
    private SpanSnapshot _built = SpanSnapshot.Empty;
    private ulong _changedStart = ulong.MaxValue;
    private ulong _changedEnd;
    internal int SplicedSnapshots { get; private set; }
    public bool IsEmpty => _root is null;
    public void Clear()
    {
        _root = null;
        Built(SpanSnapshot.Empty);
    }

    private void NoteChange(ulong start, ulong end)
    {
        _snapshot = null;
        _changedStart = Math.Min(_changedStart, start);
        _changedEnd = Math.Max(_changedEnd, end);
    }

    public void Add(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var start = address;
        var end = address + size;
        var previous = FindAtOrBelow(start);
        if (previous is not null && previous.End >= start)
        {
            if (previous.End >= end) return;
            start = previous.Start;
            _root = Delete(_root, start);
        }
        for (var next = FindAtOrAbove(start); next is not null && next.Start <= end; next = FindAtOrAbove(start))
        {
            end = Math.Max(end, next.End);
            _root = Delete(_root, next.Start);
        }
        _root = Insert(_root, start, end);
        NoteChange(start, end);
    }

    public void Remove(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var end = address + size;
        var current = FindAtOrBelow(address);
        if (current is null || current.End <= address) current = FindAtOrAbove(address);
        if (current is null || current.Start >= end) return;
        while (current is not null && current.Start < end)
        {
            var spanStart = current.Start;
            var spanEnd = current.End;
            NoteChange(spanStart, spanEnd);
            _root = Delete(_root, spanStart);
            if (spanStart < address) _root = Insert(_root, spanStart, address);
            if (spanEnd > end)
            {
                _root = Insert(_root, end, spanEnd);
                break;
            }
            current = FindAtOrAbove(address);
        }
    }

    public bool Contains(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var node = FindAtOrBelow(address);
        return node is not null && node.End >= address + size;
    }

    public bool Overlaps(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var end = address + size;
        var node = _root;
        while (node is not null)
        {
            if (node.Start >= end) node = node.Left;
            else if (node.End <= address) node = node.Right;
            else return true;
        }
        return false;
    }

    public SpanSnapshot Snapshot()
    {
        if (_snapshot is { } snapshot) return snapshot;
        if (_root is null) return Built(SpanSnapshot.Empty);
        var starts = new ulong[_root.Count];
        var ends = new ulong[starts.Length];
        if (SpliceSnapshots && TrySplice(starts, ends))
        {
            SplicedSnapshots++;
        }
        else
        {
            var index = 0;
            Collect(_root, starts, ends, ref index);
        }

        return Built(new SpanSnapshot(starts, ends));
    }

    private SpanSnapshot Built(SpanSnapshot snapshot)
    {
        _changedStart = ulong.MaxValue;
        _changedEnd = 0;
        return _snapshot = _built = snapshot;
    }

    // The spans that end before the changed extent or start after it are the last snapshot's; the tree gives the ones
    // between. False when the counts disagree, and the caller walks the whole tree.
    private bool TrySplice(ulong[] starts, ulong[] ends)
    {
        var builtStarts = _built.Starts;
        var builtEnds = _built.Ends;
        var before = FirstAtOrAbove(builtEnds, _changedStart);
        var after = FirstAbove(builtStarts, _changedEnd);
        var tail = builtStarts.Length - after;
        var middle = starts.Length - before - tail;
        if (middle < 0) return false;
        builtStarts[..before].CopyTo(starts);
        builtEnds[..before].CopyTo(ends);
        var index = before;
        if (!CollectWithin(_root, _changedStart, _changedEnd, starts, ends, ref index, before + middle) ||
            index != before + middle)
        {
            return false;
        }

        builtStarts[after..].CopyTo(starts.AsSpan(index));
        builtEnds[after..].CopyTo(ends.AsSpan(index));
        return true;
    }

    private static int FirstAtOrAbove(ReadOnlySpan<ulong> values, ulong bound)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (values[middle] < bound) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int FirstAbove(ReadOnlySpan<ulong> values, ulong bound)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (values[middle] <= bound) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    // In order, the spans that touch [low, high]; false when they would pass the limit.
    private static bool CollectWithin(Node? node, ulong low, ulong high, ulong[] starts, ulong[] ends, ref int index, int limit)
    {
        if (node is null) return true;
        if (node.Start >= low && !CollectWithin(node.Left, low, high, starts, ends, ref index, limit)) return false;
        if (node.End >= low && node.Start <= high)
        {
            if (index == limit) return false;
            starts[index] = node.Start;
            ends[index++] = node.End;
        }
        return node.End > high || CollectWithin(node.Right, low, high, starts, ends, ref index, limit);
    }

    public List<GuestSpan> GetOverlappingRanges(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var result = new List<GuestSpan>();
        VisitOverlaps(_root, address, address + size, result);
        return result;
    }

    private static void VisitOverlaps(Node? node, ulong start, ulong end, List<GuestSpan> result)
    {
        if (node is null) return;
        if (node.End <= start)
        {
            VisitOverlaps(node.Right, start, end, result);
            return;
        }
        if (node.Start >= end)
        {
            VisitOverlaps(node.Left, start, end, result);
            return;
        }
        VisitOverlaps(node.Left, start, end, result);
        var overlapStart = Math.Max(start, node.Start);
        var size = Math.Min(end, node.End) - overlapStart;
        result.Add(new GuestSpan(overlapStart, size));
        VisitOverlaps(node.Right, start, end, result);
    }

    private static void Collect(Node? node, ulong[] starts, ulong[] ends, ref int index)
    {
        if (node is null) return;
        Collect(node.Left, starts, ends, ref index);
        starts[index] = node.Start;
        ends[index++] = node.End;
        Collect(node.Right, starts, ends, ref index);
    }

    private Node? FindAtOrBelow(ulong address)
    {
        Node? result = null;
        for (var node = _root; node is not null;)
        {
            if (node.Start <= address) { result = node; node = node.Right; }
            else node = node.Left;
        }
        return result;
    }

    private Node? FindAtOrAbove(ulong address)
    {
        Node? result = null;
        for (var node = _root; node is not null;)
        {
            if (node.Start >= address) { result = node; node = node.Left; }
            else node = node.Right;
        }
        return result;
    }

    private static int Height(Node? node) => node?.Height ?? 0;
    private static void UpdateHeight(Node node)
    {
        node.Height = Math.Max(Height(node.Left), Height(node.Right)) + 1;
        node.Count = (node.Left?.Count ?? 0) + (node.Right?.Count ?? 0) + 1;
    }

    private static Node RotateRight(Node node)
    {
        var left = node.Left!;
        node.Left = left.Right;
        left.Right = node;
        UpdateHeight(node);
        UpdateHeight(left);
        return left;
    }

    private static Node RotateLeft(Node node)
    {
        var right = node.Right!;
        node.Right = right.Left;
        right.Left = node;
        UpdateHeight(node);
        UpdateHeight(right);
        return right;
    }

    private static Node Balance(Node node)
    {
        UpdateHeight(node);
        var difference = Height(node.Left) - Height(node.Right);
        if (difference > 1)
        {
            if (Height(node.Left!.Left) < Height(node.Left.Right)) node.Left = RotateLeft(node.Left);
            return RotateRight(node);
        }
        if (difference < -1)
        {
            if (Height(node.Right!.Right) < Height(node.Right.Left)) node.Right = RotateRight(node.Right);
            return RotateLeft(node);
        }
        return node;
    }

    private static Node Insert(Node? node, ulong start, ulong end)
    {
        if (node is null) return new Node(start, end);
        if (start < node.Start) node.Left = Insert(node.Left, start, end);
        else if (start > node.Start) node.Right = Insert(node.Right, start, end);
        else node.End = end;
        return Balance(node);
    }

    private static Node? Delete(Node? node, ulong start)
    {
        if (node is null) return null;
        if (start < node.Start) node.Left = Delete(node.Left, start);
        else if (start > node.Start) node.Right = Delete(node.Right, start);
        else
        {
            if (node.Left is null) return node.Right;
            if (node.Right is null) return node.Left;
            var successor = node.Right;
            while (successor.Left is not null) successor = successor.Left;
            node.Start = successor.Start;
            node.End = successor.End;
            node.Right = Delete(node.Right, successor.Start);
        }
        return Balance(node);
    }

    private static void ValidateRange(ulong address, ulong size)
    {
        if (size == 0 || address + size < address)
            Environment.FailFast($"The address range is invalid: 0x{address:X16}, 0x{size:X16}.");
    }
}
