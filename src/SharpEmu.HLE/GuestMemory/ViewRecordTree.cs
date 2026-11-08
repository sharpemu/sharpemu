// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;

namespace SharpEmu.HLE.GuestMemory;

internal sealed class ViewRecordTree : IEnumerable<ViewRecord>
{
    internal sealed class Node(ViewRecord range, Node? left = null, Node? right = null)
    {
        internal readonly ViewRecord Range = range;
        internal readonly Node? Left = left;
        internal readonly Node? Right = right;
        internal readonly int Height = 1 + Math.Max(left?.Height ?? 0, right?.Height ?? 0);
    }

    private Node? _root;

    internal Node? Snapshot => _root;

    public bool ContainsKey(ulong address)
    {
        var record = FindAtOrBelow(address);
        return record.Size != 0 && record.Address == address;
    }

    public ViewRecord this[ulong address]
    {
        set
        {
            if (address != value.Address)
                throw new ArgumentException("The key must match the view address.", nameof(address));
            var added = false;
            _root = Insert(_root, value, ref added, replace: true);
        }
    }

    public ViewRecord FindAtOrBelow(ulong address)
        => FindAtOrBelow(_root, address);

    internal static ViewRecord FindAtOrBelow(Node? root, ulong address)
    {
        var current = root;
        var result = default(ViewRecord);
        while (current is not null)
        {
            if (current.Range.Address > address)
                current = current.Left;
            else
            {
                result = current.Range;
                current = current.Right;
            }
        }
        return result;
    }

    public ViewRecord FindAtOrAbove(ulong address)
    {
        var current = _root;
        var result = default(ViewRecord);
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

    public bool Add(ViewRecord range)
    {
        var added = false;
        _root = Insert(_root, range, ref added);
        return added;
    }

    public void Remove(ViewRecord range) => _root = RemoveNode(_root, range.Address);
    public void Clear() => _root = null;

    private static Node Insert(Node? node, ViewRecord range, ref bool added, bool replace = false)
    {
        if (node is null)
        {
            added = true;
            return new Node(range);
        }
        if (range.Address < node.Range.Address)
            return Balance(new Node(node.Range, Insert(node.Left, range, ref added, replace), node.Right));
        else if (range.Address > node.Range.Address)
            return Balance(new Node(node.Range, node.Left, Insert(node.Right, range, ref added, replace)));
        else
            return replace ? new Node(range, node.Left, node.Right) : node;
    }

    private static Node? RemoveNode(Node? node, ulong address)
    {
        if (node is null) return null;
        if (address < node.Range.Address)
            return Balance(new Node(node.Range, RemoveNode(node.Left, address), node.Right));
        else if (address > node.Range.Address)
            return Balance(new Node(node.Range, node.Left, RemoveNode(node.Right, address)));
        else
        {
            if (node.Left is null) return node.Right;
            if (node.Right is null) return node.Left;
            var successor = node.Right;
            while (successor.Left is not null)
                successor = successor.Left;
            return Balance(new Node(successor.Range, node.Left, RemoveNode(node.Right, successor.Range.Address)));
        }
    }

    private static int Height(Node? node) => node?.Height ?? 0;

    private static Node Balance(Node node)
    {
        var difference = Height(node.Left) - Height(node.Right);
        if (difference > 1)
        {
            if (Height(node.Left!.Left) < Height(node.Left.Right))
                node = new Node(node.Range, RotateLeft(node.Left), node.Right);
            return RotateRight(node);
        }
        if (difference < -1)
        {
            if (Height(node.Right!.Right) < Height(node.Right.Left))
                node = new Node(node.Range, node.Left, RotateRight(node.Right));
            return RotateLeft(node);
        }
        return node;
    }

    private static Node RotateLeft(Node node)
    {
        var replacement = node.Right!;
        return new Node(replacement.Range, new Node(node.Range, node.Left, replacement.Left), replacement.Right);
    }

    private static Node RotateRight(Node node)
    {
        var replacement = node.Left!;
        return new Node(replacement.Range, replacement.Left, new Node(node.Range, replacement.Right, node.Right));
    }

    public IEnumerator<ViewRecord> GetEnumerator()
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
