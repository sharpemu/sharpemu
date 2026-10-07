// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GuestMemory;

// Callers hold the memory-map lock for both updates and searches.
internal sealed class AllocationGapTree
{
    private sealed class Node(ulong address, ulong end)
    {
        public ulong Address = address;
        public ulong End = end;
        public ulong First = address;
        public ulong Last = end;
        public ulong MaximumGap;
        public int Height = 1;
        public Node? Left;
        public Node? Right;
    }

    private Node? _root;

    public void Set(ulong address, ulong size)
    {
        if (size == 0 || size > ulong.MaxValue - address)
            throw new ArgumentOutOfRangeException(nameof(size));
        _root = Insert(_root, address, address + size);
    }

    public void Remove(ulong address) => _root = Remove(_root, address);
    public void Clear() => _root = null;

    public ulong Find(ulong start, ulong size, ulong alignment, ulong limit)
    {
        if (size == 0 || alignment == 0 || !TryAlign(start, alignment, out var candidate))
            return 0;
        var overflow = false;
        Find(_root, size, alignment, ref candidate, ref overflow);
        return !overflow && Fits(candidate, size, limit) ? candidate : 0;
    }

    private static bool Find(Node? node, ulong size, ulong alignment, ref ulong candidate, ref bool overflow)
    {
        if (node is null || node.Last <= candidate)
            return false;
        if (Fits(candidate, size, node.First))
            return true;
        // Alignment can shrink a gap, but it cannot make a small gap usable.
        if (node.MaximumGap < size)
        {
            overflow = !TryAlign(node.Last, alignment, out candidate);
            return overflow;
        }
        if (Find(node.Left, size, alignment, ref candidate, ref overflow))
            return true;
        if (Fits(candidate, size, node.Address))
            return true;
        if (node.End > candidate && !TryAlign(node.End, alignment, out candidate))
        {
            overflow = true;
            return true;
        }
        return Find(node.Right, size, alignment, ref candidate, ref overflow);
    }

    private static bool Fits(ulong address, ulong size, ulong end) => address <= end && size <= end - address;

    private static bool TryAlign(ulong address, ulong alignment, out ulong result)
    {
        var padding = (alignment - address % alignment) % alignment;
        result = padding <= ulong.MaxValue - address ? address + padding : 0;
        return padding <= ulong.MaxValue - address;
    }

    private static int Height(Node? node) => node?.Height ?? 0;

    private static Node Update(Node node)
    {
        node.Height = 1 + Math.Max(Height(node.Left), Height(node.Right));
        node.First = node.Left?.First ?? node.Address;
        node.Last = node.Right?.Last ?? node.End;
        node.MaximumGap = Math.Max(node.Left?.MaximumGap ?? 0, node.Right?.MaximumGap ?? 0);
        if (node.Left is not null)
            node.MaximumGap = Math.Max(node.MaximumGap, node.Address > node.Left.Last ? node.Address - node.Left.Last : 0);
        if (node.Right is not null)
            node.MaximumGap = Math.Max(node.MaximumGap, node.Right.First > node.End ? node.Right.First - node.End : 0);
        return node;
    }

    private static Node RotateRight(Node node)
    {
        var replacement = node.Left!;
        node.Left = replacement.Right;
        replacement.Right = Update(node);
        return Update(replacement);
    }

    private static Node RotateLeft(Node node)
    {
        var replacement = node.Right!;
        node.Right = replacement.Left;
        replacement.Left = Update(node);
        return Update(replacement);
    }

    private static Node Balance(Node node)
    {
        Update(node);
        if (Height(node.Left) - Height(node.Right) > 1)
        {
            if (Height(node.Left!.Left) < Height(node.Left.Right))
                node.Left = RotateLeft(node.Left);
            return RotateRight(node);
        }
        if (Height(node.Right) - Height(node.Left) > 1)
        {
            if (Height(node.Right!.Right) < Height(node.Right.Left))
                node.Right = RotateRight(node.Right);
            return RotateLeft(node);
        }
        return node;
    }

    private static Node Insert(Node? node, ulong address, ulong end)
    {
        if (node is null)
            return new Node(address, end);
        if (address < node.Address)
            node.Left = Insert(node.Left, address, end);
        else if (address > node.Address)
            node.Right = Insert(node.Right, address, end);
        else
            node.End = end;
        return Balance(node);
    }

    private static Node? Remove(Node? node, ulong address)
    {
        if (node is null)
            return null;
        if (address < node.Address)
            node.Left = Remove(node.Left, address);
        else if (address > node.Address)
            node.Right = Remove(node.Right, address);
        else
        {
            if (node.Left is null)
                return node.Right;
            if (node.Right is null)
                return node.Left;
            var successor = node.Right;
            while (successor.Left is not null)
                successor = successor.Left;
            node.Address = successor.Address;
            node.End = successor.End;
            node.Right = Remove(node.Right, successor.Address);
        }
        return Balance(node);
    }
}
