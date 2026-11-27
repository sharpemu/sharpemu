// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace SharpEmu.HLE.FileSystem;

// Packs a folder into a .zar the way the ZArchive writer lays one out: file data appended in order
// and cut into 64 KiB zstd blocks (the last padded with zeros, a block that does not shrink stored
// raw), the data section padded to 8 bytes, then offset records, names, a breadth-first file tree
// with each directory's children sorted without case, empty metadata and the footer. Data streams
// through one block at a time, so a large game needs little memory.
public static class ZArchiveWriter
{
    internal const int CompressionLevel = 6;
    internal const int BlocksPerRecord = 16;
    // Logical bytes behind one offset record: 16 blocks, placed anywhere in the data section.
    internal const int RecordSpan = ZArchive.BlockSize * BlocksPerRecord;
    private const uint Version = 0x61BF3A01;
    private const uint Magic = 0x169F52D6;

    public readonly record struct Progress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

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

    // Writes the archive through a temporary name and moves it into place only when complete, so a
    // cancelled or failed pack leaves no partial .zar behind.
    public static void Pack(
        string sourceDirectory,
        string destinationPath,
        IProgress<Progress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(destinationPath);
        if (destination.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The archive cannot be written inside the folder being packed.");
        }

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderBy(info => info.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var bytesTotal = files.Sum(info => info.Length);
        var temporary = destination + ".partial";
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                Write(source, files, bytesTotal, output, progress, cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    // Whether the archive opens and holds every file of the folder at the same size: the check
    // that must pass before the folder may be deleted.
    public static bool MatchesFolder(string archivePath, string sourceDirectory)
    {
        using var archive = ZArchive.Open(archivePath);
        if (archive is null)
        {
            return false;
        }

        var source = Path.GetFullPath(sourceDirectory);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var node = archive.LookUp(Path.GetRelativePath(source, file));
            if (!archive.IsFile(node) || archive.GetFileSize(node) != (ulong)new FileInfo(file).Length)
            {
                return false;
            }
        }

        return true;
    }

    private static void Write(
        string source,
        List<FileInfo> files,
        long bytesTotal,
        FileStream output,
        IProgress<Progress>? progress,
        CancellationToken cancellationToken)
    {
        var block = new byte[ZArchive.BlockSize];
        var encoded = new byte[EncodeBufferSize];
        var filled = 0;
        var blockIndex = 0;
        var records = new List<(ulong BaseOffset, ushort[] Sizes)>();
        var entries = new List<(string[] Parts, ulong Offset, ulong Size)>(files.Count);
        using var compressor = new Compressor(CompressionLevel);
        ulong dataOffset = 0;
        var lastReport = 0L;

        void FlushBlock()
        {
            var offset = (ulong)output.Position;
            var size = EncodeBlock(compressor, block, encoded);
            output.Write(encoded, 0, size);
            if (blockIndex % BlocksPerRecord == 0)
            {
                records.Add((offset, new ushort[BlocksPerRecord]));
            }

            records[^1].Sizes[blockIndex % BlocksPerRecord] = (ushort)(size - 1);
            blockIndex++;
            filled = 0;
        }

        for (var index = 0; index < files.Count; index++)
        {
            var parts = Path.GetRelativePath(source, files[index].FullName).Split(Path.DirectorySeparatorChar);
            var fileOffset = dataOffset;
            ulong fileSize = 0;
            using (var input = new FileStream(files[index].FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                int read;
                while ((read = input.Read(block, filled, block.Length - filled)) > 0)
                {
                    filled += read;
                    dataOffset += (ulong)read;
                    fileSize += (ulong)read;
                    if (filled == block.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FlushBlock();
                        if ((long)dataOffset - lastReport >= 16 << 20)
                        {
                            lastReport = (long)dataOffset;
                            progress?.Report(new Progress(lastReport, bytesTotal, index, files.Count));
                        }
                    }
                }
            }

            entries.Add((parts, fileOffset, fileSize));
        }

        if (filled > 0)
        {
            Array.Clear(block, filled, block.Length - filled);
            FlushBlock();
        }

        WriteTrailer(output, (ulong)output.Position, records, entries);
        progress?.Report(new Progress(bytesTotal, bytesTotal, files.Count, files.Count));
    }

    // Room EncodeBlock needs in its destination.
    internal static int EncodeBufferSize { get; } = Math.Max(Compressor.GetCompressBound(ZArchive.BlockSize), ZArchive.BlockSize);

    // Compresses one 64 KiB block into `destination` (EncodeBufferSize bytes) and returns its stored
    // size: the zstd frame, or the raw block when the frame does not shrink it.
    internal static int EncodeBlock(Compressor compressor, ReadOnlySpan<byte> block, Span<byte> destination)
    {
        var size = compressor.Wrap(block, destination);
        if (size >= ZArchive.BlockSize)
        {
            block.CopyTo(destination);
            return ZArchive.BlockSize;
        }

        return size;
    }

    // Writes everything after the compressed data: padding to 8 bytes, the offset records, names, the
    // breadth-first tree (each directory's children sorted without case; directories merge without
    // case, the first spelling wins) and the footer. `output` sits at `dataSize`, the end of the data
    // section. Files are (path parts, data offset, size), in the order their nodes are created.
    internal static void WriteTrailer(
        Stream output,
        ulong dataSize,
        IReadOnlyList<(ulong BaseOffset, ushort[] Sizes)> records,
        IEnumerable<(string[] Parts, ulong Offset, ulong Size)> files)
    {
        var root = new PathNode(string.Empty, isFile: false);
        foreach (var (parts, offset, size) in files)
        {
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

            directory.Children.Add(new PathNode(parts[^1], isFile: true) { FileOffset = offset, FileSize = size });
        }

        while (output.Position % 8 != 0)
        {
            output.WriteByte(0);
        }

        var sections = new (ulong Offset, ulong Size)[6];
        sections[0] = (0, dataSize);
        var start = (ulong)output.Position;
        foreach (var (baseOffset, sizes) in records)
        {
            WriteU64(output, baseOffset);
            foreach (var size in sizes)
            {
                WriteU16(output, size);
            }
        }

        sections[1] = (start, (ulong)output.Position - start);

        // Names in breadth-first order, as the reference writer interns them.
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

        start = (ulong)output.Position;
        foreach (var node in ordered.Skip(1))
        {
            node.NameOffset = (uint)((ulong)output.Position - start);
            var name = Encoding.UTF8.GetBytes(node.Name);
            if (name.Length >= 0x8000)
            {
                throw new IOException($"The name '{node.Name}' is too long for an archive.");
            }

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

        sections[2] = (start, (ulong)output.Position - start);
        start = (ulong)output.Position;
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

        sections[3] = (start, (ulong)output.Position - start);
        sections[4] = ((ulong)output.Position, 0);
        sections[5] = ((ulong)output.Position, 0);
        foreach (var (offset, size) in sections)
        {
            WriteU64(output, offset);
            WriteU64(output, size);
        }

        output.Write(new byte[32]);
        WriteU64(output, (ulong)output.Position + 8 + 4 + 4);
        WriteU32(output, Version);
        WriteU32(output, Magic);
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
