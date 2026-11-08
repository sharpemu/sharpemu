// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;

using Allocation = SharpEmu.Libs.Kernel.DirectMemoryAllocationMap.Allocation;

namespace SharpEmu.Libs.Kernel;

internal sealed class DirectAllocationTree : IEnumerable<Allocation>
{
    private sealed class Node(Allocation range)
    {
        internal Allocation Range = range;
        internal Node? Left;
        internal Node? Right;
        internal int Height = 1;
    }

    private Node? _root;

    public Allocation this[ulong address]
    {
        set
        {
            if (address != value.Start)
                throw new ArgumentException("The key must match the allocation address.", nameof(address));
            var current = _root;
            while (current is not null)
            {
                if (address == current.Range.Start)
                {
                    current.Range = value;
                    return;
                }
                current = address < current.Range.Start ? current.Left : current.Right;
            }
            Add(value);
        }
    }

    public Allocation FindAtOrBelow(ulong address)
    {
        var current = _root;
        var result = default(Allocation);
        while (current is not null)
        {
            if (current.Range.Start > address)
                current = current.Left;
            else
            {
                result = current.Range;
                current = current.Right;
            }
        }
        return result;
    }

    public Allocation FindAtOrAbove(ulong address)
    {
        var current = _root;
        var result = default(Allocation);
        while (current is not null)
        {
            if (current.Range.Start < address)
                current = current.Right;
            else
            {
                result = current.Range;
                current = current.Left;
            }
        }
        return result;
    }

    public bool Add(Allocation range)
    {
        var added = false;
        _root = Insert(_root, range, ref added);
        return added;
    }

    public void Remove(Allocation range) => _root = RemoveNode(_root, range.Start);
    public void Clear() => _root = null;

    private static Node Insert(Node? node, Allocation range, ref bool added)
    {
        if (node is null)
        {
            added = true;
            return new Node(range);
        }
        if (range.Start < node.Range.Start)
            node.Left = Insert(node.Left, range, ref added);
        else if (range.Start > node.Range.Start)
            node.Right = Insert(node.Right, range, ref added);
        else
            return node;
        return Balance(node);
    }

    private static Node? RemoveNode(Node? node, ulong address)
    {
        if (node is null) return null;
        if (address < node.Range.Start)
            node.Left = RemoveNode(node.Left, address);
        else if (address > node.Range.Start)
            node.Right = RemoveNode(node.Right, address);
        else
        {
            if (node.Left is null) return node.Right;
            if (node.Right is null) return node.Left;
            var successor = node.Right;
            while (successor.Left is not null)
                successor = successor.Left;
            node.Range = successor.Range;
            node.Right = RemoveNode(node.Right, successor.Range.Start);
        }
        return Balance(node);
    }

    private static int Height(Node? node) => node?.Height ?? 0;

    private static void UpdateHeight(Node node) =>
        node.Height = 1 + Math.Max(Height(node.Left), Height(node.Right));

    private static Node Balance(Node node)
    {
        UpdateHeight(node);
        var difference = Height(node.Left) - Height(node.Right);
        if (difference > 1)
        {
            if (Height(node.Left!.Left) < Height(node.Left.Right))
                node.Left = RotateLeft(node.Left);
            return RotateRight(node);
        }
        if (difference < -1)
        {
            if (Height(node.Right!.Right) < Height(node.Right.Left))
                node.Right = RotateRight(node.Right);
            return RotateLeft(node);
        }
        return node;
    }

    private static Node RotateLeft(Node node)
    {
        var replacement = node.Right!;
        node.Right = replacement.Left;
        replacement.Left = node;
        UpdateHeight(node);
        UpdateHeight(replacement);
        return replacement;
    }

    private static Node RotateRight(Node node)
    {
        var replacement = node.Left!;
        node.Left = replacement.Right;
        replacement.Right = node;
        UpdateHeight(node);
        UpdateHeight(replacement);
        return replacement;
    }

    public IEnumerator<Allocation> GetEnumerator()
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
