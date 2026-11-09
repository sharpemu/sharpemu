// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    private sealed class DirectMappingIndex
    {
        private sealed class Node(MappedRegion region)
        {
            public MappedRegion Region = region;
            public Node? Left;
            public Node? Right;
            public int Height = 1;
            public ulong MaximumEnd = region.DirectStart + region.Length;
        }

        private Node? _root;

        public void Add(MappedRegion region) => _root = Insert(_root, region);
        public void Remove(MappedRegion region) => _root = Remove(_root, region);
        public void Clear() => _root = null;

        public MappedRegion[] FindOverlaps(ulong start, ulong length)
        {
            if (length == 0 || length > ulong.MaxValue - start)
                return [];
            var matches = new List<MappedRegion>();
            Collect(_root, start, start + length, matches);
            // Preserve the virtual-address order used by unmapping and rollback.
            matches.Sort((left, right) => left.Address.CompareTo(right.Address));
            return matches.ToArray();
        }

        private static void Collect(Node? node, ulong start, ulong end, List<MappedRegion> matches)
        {
            if (node is null || node.MaximumEnd <= start)
                return;
            Collect(node.Left, start, end, matches);
            if (node.Region.DirectStart >= end)
                return;
            if (start < node.Region.DirectStart + node.Region.Length)
                matches.Add(node.Region);
            Collect(node.Right, start, end, matches);
        }

        private static int Compare(MappedRegion left, MappedRegion right)
        {
            var comparison = left.DirectStart.CompareTo(right.DirectStart);
            return comparison != 0 ? comparison : left.Address.CompareTo(right.Address);
        }

        private static Node Insert(Node? node, MappedRegion region)
        {
            if (node is null)
                return new Node(region);
            var comparison = Compare(region, node.Region);
            if (comparison < 0)
                node.Left = Insert(node.Left, region);
            else if (comparison > 0)
                node.Right = Insert(node.Right, region);
            else
                node.Region = region;
            return Balance(node);
        }

        private static Node? Remove(Node? node, MappedRegion region)
        {
            if (node is null)
                return null;
            var comparison = Compare(region, node.Region);
            if (comparison < 0)
                node.Left = Remove(node.Left, region);
            else if (comparison > 0)
                node.Right = Remove(node.Right, region);
            else
            {
                if (node.Left is null) return node.Right;
                if (node.Right is null) return node.Left;
                var successor = node.Right;
                while (successor.Left is not null)
                    successor = successor.Left;
                node.Region = successor.Region;
                node.Right = Remove(node.Right, successor.Region);
            }
            return Balance(node);
        }

        private static int Height(Node? node) => node?.Height ?? 0;

        private static void Refresh(Node node)
        {
            node.Height = 1 + Math.Max(Height(node.Left), Height(node.Right));
            node.MaximumEnd = Math.Max(node.Region.DirectStart + node.Region.Length,
                Math.Max(node.Left?.MaximumEnd ?? 0, node.Right?.MaximumEnd ?? 0));
        }

        private static Node Balance(Node node)
        {
            Refresh(node);
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
            Refresh(node);
            Refresh(replacement);
            return replacement;
        }

        private static Node RotateRight(Node node)
        {
            var replacement = node.Left!;
            node.Left = replacement.Right;
            replacement.Right = node;
            Refresh(node);
            Refresh(replacement);
            return replacement;
        }
    }
}
