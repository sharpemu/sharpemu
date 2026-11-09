// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;

namespace SharpEmu.HLE.GpuMemory;

internal readonly record struct PermissionRange(ulong Address, ulong Size, GuestPageProtection Protection);

// Nodes never change once built: a change builds new nodes along its path and shares the rest, so a
// snapshot stays whole while the tree changes and can be read without a lock.
internal sealed class PermissionRangeTree : IEnumerable<PermissionRange>
{
    private sealed class Node(PermissionRange range, Node? left, Node? right)
    {
        internal readonly PermissionRange Range = range;
        internal readonly Node? Left = left;
        internal readonly Node? Right = right;
        internal readonly int Height = 1 + Math.Max(left?.Height ?? 0, right?.Height ?? 0);
    }

    private Node? _root;

    public PermissionRangeTree()
    {
    }

    private PermissionRangeTree(Node? root) => _root = root;

    // The records as they are now; later changes to this tree do not reach the snapshot.
    public PermissionRangeTree Snapshot() => new(_root);

    public PermissionRange this[ulong address]
    {
        set
        {
            if (address != value.Address)
                throw new ArgumentException("The key must match the permission-range address.", nameof(address));
            var added = false;
            _root = Insert(_root, value, replace: true, ref added);
        }
    }

    public PermissionRange? FindAtOrBelow(ulong address) => FindAtOrBelow(address, out _);

    // nextAddress is the start of the first record above the address, or ulong.MaxValue without one.
    public PermissionRange? FindAtOrBelow(ulong address, out ulong nextAddress)
    {
        var current = _root;
        PermissionRange? result = null;
        nextAddress = ulong.MaxValue;
        while (current is not null)
        {
            if (current.Range.Address > address)
            {
                nextAddress = current.Range.Address;
                current = current.Left;
            }
            else
            {
                result = current.Range;
                current = current.Right;
            }
        }
        return result;
    }

    public PermissionRange? FindAtOrAbove(ulong address)
    {
        var current = _root;
        PermissionRange? result = null;
        while (current is not null)
        {
            if (current.Range.Address < address)
                current = current.Right;
            else
            {
                result = current.Range;
                current = current.Left;
            }
        }
        return result;
    }

    public bool Add(PermissionRange range)
    {
        var added = false;
        _root = Insert(_root, range, replace: false, ref added);
        return added;
    }

    public void Remove(PermissionRange range) => _root = RemoveNode(_root, range.Address);
    public void Clear() => _root = null;

    private static Node Insert(Node? node, PermissionRange range, bool replace, ref bool added)
    {
        if (node is null)
        {
            added = true;
            return new Node(range, null, null);
        }
        if (range.Address < node.Range.Address)
            return Balance(node.Range, Insert(node.Left, range, replace, ref added), node.Right);
        if (range.Address > node.Range.Address)
            return Balance(node.Range, node.Left, Insert(node.Right, range, replace, ref added));
        return replace ? new Node(range, node.Left, node.Right) : node;
    }

    private static Node? RemoveNode(Node? node, ulong address)
    {
        if (node is null) return null;
        if (address < node.Range.Address)
            return Balance(node.Range, RemoveNode(node.Left, address), node.Right);
        if (address > node.Range.Address)
            return Balance(node.Range, node.Left, RemoveNode(node.Right, address));
        if (node.Left is null) return node.Right;
        if (node.Right is null) return node.Left;
        var successor = node.Right;
        while (successor.Left is not null)
            successor = successor.Left;
        return Balance(successor.Range, node.Left, RemoveNode(node.Right, successor.Range.Address));
    }

    private static int Height(Node? node) => node?.Height ?? 0;

    // A new node over the two subtrees, rotated when one of them is two levels deeper.
    private static Node Balance(PermissionRange range, Node? left, Node? right)
    {
        var difference = Height(left) - Height(right);
        if (difference > 1)
        {
            if (Height(left!.Left) < Height(left.Right))
                left = RotateLeft(left.Range, left.Left, left.Right!);
            return RotateRight(range, left, right);
        }
        if (difference < -1)
        {
            if (Height(right!.Right) < Height(right.Left))
                right = RotateRight(right.Range, right.Left!, right.Right);
            return RotateLeft(range, left, right);
        }
        return new Node(range, left, right);
    }

    private static Node RotateLeft(PermissionRange range, Node? left, Node right) =>
        new(right.Range, new Node(range, left, right.Left), right.Right);

    private static Node RotateRight(PermissionRange range, Node left, Node? right) =>
        new(left.Range, left.Left, new Node(range, left.Right, right));

    public IEnumerator<PermissionRange> GetEnumerator()
    {
        var pending = new Stack<Node>();
        var current = _root;
        while (current is not null || pending.Count != 0)
        {
            while (current is not null)
            {
                pending.Push(current);
                current = current.Left;
            }
            current = pending.Pop();
            yield return current.Range;
            current = current.Right;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
