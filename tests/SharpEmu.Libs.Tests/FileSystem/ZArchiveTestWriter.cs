// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace SharpEmu.Libs.Tests.FileSystem;

// Builds .zar archives the way the ZArchive writer lays them out: file data appended in order and
// cut into 64 KiB blocks (the last padded with zeros, a block that does not shrink stored raw), the
// data section padded to 8 bytes, then offset records, names, a breadth-first file tree with each
// directory's children sorted without case, empty metadata and the footer.
internal static class ZArchiveTestWriter
{
    private const int BlockSize = 64 * 1024;

    private sealed class PathNode(string name, bool isFile)
    {
        public string Name { get; } = name;
        public bool IsFile { get; } = isFile;
        public List<PathNode> Children { get; } = [];
        public ulong FileOffset { get; set; }
        public ulong FileSize { get; set; }
        public uint NameOffset { get; set; }
        public uint FirstChild { get; set; }
    }

    public static byte[] Build(IEnumerable<(string Path, byte[] Data)> files)
    {
        var root = new PathNode(string.Empty, isFile: false);
        var data = new MemoryStream();
        foreach (var (path, bytes) in files)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var directory = root;
            foreach (var part in parts[..^1])
            {
                var next = directory.Children.Find(child => string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase));
                if (next is null)
                {
                    next = new PathNode(part, isFile: false);
                    directory.Children.Add(next);
                }

                directory = next;
            }

            directory.Children.Add(new PathNode(parts[^1], isFile: true) { FileOffset = (ulong)data.Length, FileSize = (ulong)bytes.Length });
            data.Write(bytes);
        }

        if (data.Length % BlockSize != 0)
        {
            data.Write(new byte[BlockSize - data.Length % BlockSize]);
        }

        var output = new MemoryStream();
        var records = new List<(ulong BaseOffset, ushort[] Sizes)>();
        using var compressor = new Compressor(6);
        var raw = data.ToArray();
        for (var block = 0; block < raw.Length / BlockSize; block++)
        {
            var input = raw.AsSpan(block * BlockSize, BlockSize);
            var compressed = compressor.Wrap(input).ToArray();
            var offset = (ulong)output.Length;
            int size;
            if (compressed.Length >= BlockSize)
            {
                output.Write(input);
                size = BlockSize;
            }
            else
            {
                output.Write(compressed);
                size = compressed.Length;
            }

            if (block % 16 == 0)
            {
                records.Add((offset, new ushort[16]));
            }

            records[^1].Sizes[block % 16] = (ushort)(size - 1);
        }

        var dataSize = (ulong)output.Length;
        while (output.Length % 8 != 0)
        {
            output.WriteByte(0);
        }

        var sections = new (ulong Offset, ulong Size)[6];
        sections[0] = (0, dataSize);
        var start = (ulong)output.Length;
        foreach (var (baseOffset, sizes) in records)
        {
            WriteU64(output, baseOffset);
            foreach (var size in sizes)
            {
                WriteU16(output, size);
            }
        }

        sections[1] = (start, (ulong)output.Length - start);

        // Names in breadth-first order, as the writer interns them.
        var ordered = new List<PathNode>();
        var queue = new Queue<PathNode>();
        queue.Enqueue(root);
        var nextIndex = 1u;
        while (queue.TryDequeue(out var node))
        {
            ordered.Add(node);
            if (node.IsFile)
            {
                continue;
            }

            node.Children.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
            node.FirstChild = nextIndex;
            nextIndex += (uint)node.Children.Count;
            foreach (var child in node.Children)
            {
                queue.Enqueue(child);
            }
        }

        start = (ulong)output.Length;
        foreach (var node in ordered.Skip(1))
        {
            node.NameOffset = (uint)((ulong)output.Length - start);
            var name = Encoding.UTF8.GetBytes(node.Name);
            if (name.Length >= 0x80)
            {
                output.WriteByte((byte)((name.Length & 0x7F) | 0x80));
                output.WriteByte((byte)(name.Length >> 7));
            }
            else
            {
                output.WriteByte((byte)name.Length);
            }

            output.Write(name);
        }

        sections[2] = (start, (ulong)output.Length - start);
        start = (ulong)output.Length;
        foreach (var node in ordered)
        {
            var nameOffset = ReferenceEquals(node, root) ? 0x7FFFFFFFu : node.NameOffset;
            WriteU32(output, nameOffset | (node.IsFile ? 0x80000000u : 0));
            if (node.IsFile)
            {
                WriteU32(output, (uint)node.FileOffset);
                WriteU32(output, (uint)node.FileSize);
                WriteU32(output, (uint)((node.FileOffset >> 32) & 0xFFFF) | (uint)((node.FileSize >> 16) & 0xFFFF0000));
            }
            else
            {
                WriteU32(output, node.FirstChild);
                WriteU32(output, (uint)node.Children.Count);
                WriteU32(output, 0);
            }
        }

        sections[3] = (start, (ulong)output.Length - start);
        sections[4] = ((ulong)output.Length, 0);
        sections[5] = ((ulong)output.Length, 0);
        foreach (var (offset, size) in sections)
        {
            WriteU64(output, offset);
            WriteU64(output, size);
        }

        output.Write(new byte[32]);
        WriteU64(output, (ulong)output.Length + 8 + 4 + 4);
        WriteU32(output, 0x61BF3A01);
        WriteU32(output, 0x169F52D6);
        return output.ToArray();
    }

    private static void WriteU16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteU64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
