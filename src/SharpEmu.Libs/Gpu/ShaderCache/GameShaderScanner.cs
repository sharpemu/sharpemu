// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal readonly record struct ShaderScanResult(int FilesScanned, int FilesSkipped, int Programs, long BytesScanned);

internal static class GameShaderScanner
{
    public const ulong ScannedCodeAddress = 0x10_0000_0000;
    public const int Version = 2;
    private const int ChunkBytes = 8 << 20;
    private const int CodeSlackBytes = 256;
    private const int CodeAlignment = 256;

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".webm", ".bk2", ".bik", ".usm", ".at9", ".wem", ".bnk", ".ogg", ".wav", ".mp3", ".opus", ".flac",
    };

    private readonly record struct FoundProgram(long Offset, CachedProgram? Program, AgcShaderType Type, ulong Identity);
    private readonly record struct ScanSource(string Path, string CachePath, long Length, long WriteTicks);

    public static ShaderScanResult Scan(string root, ShaderCacheFile file, Action<long, long>? progress, CancellationToken cancellation)
    {
        var files = App0Archive.EnumerateFilesRecursive(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(path => !MediaExtensions.Contains(Path.GetExtension(path)))
            .Select(path => Source(root, path))
            .Where(info => info.Length >= AgcShaderHeader.FixedBytes)
            .OrderBy(info => info.Path, StringComparer.Ordinal)
            .ToArray();
        var pending = new List<ScanSource>();
        var skipped = 0;
        foreach (var info in files)
        {
            if (file.TryGetScannedFile(info.CachePath, out var known) && known.Size == info.Length &&
                known.WriteTicks == info.WriteTicks && known.Scanner == Version)
            {
                skipped++;
                continue;
            }

            pending.Add(info);
        }

        var total = pending.Sum(entry => entry.Length);
        var scanned = 0L;
        var programs = 0;
        progress?.Invoke(0, total);
        Parallel.ForEach(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8), CancellationToken = cancellation },
            entry =>
            {
                var found = ScanFile(entry.Path, file, bytes =>
                    progress?.Invoke(Interlocked.Add(ref scanned, bytes), total), cancellation);
                Interlocked.Add(ref programs, found);
                file.RecordScannedFile(new ScannedFile(entry.CachePath, entry.Length, entry.WriteTicks, found, Version));
            });
        return new ShaderScanResult(pending.Count, skipped, programs, total);
    }

    private static ScanSource Source(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var info = new FileInfo(path);
        if (info.Exists)
        {
            return new ScanSource(path, relative, info.Length, info.LastWriteTimeUtc.Ticks);
        }

        if (!App0Archive.TryResolve(path, out var archive, out var node) || !archive.IsFile(node))
        {
            throw new FileNotFoundException("The shader scan source was not found.", path);
        }

        var container = new FileInfo(archive.Path);
        // Archive entries have no timestamps; invalidate their scan stamps with the container.
        return new ScanSource(path, $"zar:{archive.Path}|{container.Length}|{relative}",
            checked((long)archive.GetFileSize(node)), container.LastWriteTimeUtc.Ticks);
    }

    private static int ScanFile(string path, ShaderCacheFile file, Action<long> progress, CancellationToken cancellation)
    {
        using var stream = App0Archive.OpenReadOrFile(path, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var signature = AgcShaderHeader.Signature;
        var buffer = new byte[ChunkBytes];
        var found = new List<FoundProgram>();
        var reported = 0L;
        for (var chunkStart = 0L; chunkStart < length;)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = ReadAt(stream, buffer, chunkStart);
            if (read <= 0)
            {
                break;
            }

            var window = buffer.AsSpan(0, read);
            for (var position = window.IndexOf(signature); position >= 0;)
            {
                var offset = chunkStart + position;
                if (TryRecord(stream, length, offset, file) is { } program)
                {
                    found.Add(program);
                }

                var next = window[(position + 1)..].IndexOf(signature);
                position = next < 0 ? -1 : position + 1 + next;
            }

            var completed = Math.Min(chunkStart + read, length);
            progress(completed - reported);
            reported = completed;
            if (chunkStart + read >= length)
            {
                break;
            }

            chunkStart += read - (signature.Length - 1);
        }

        for (var index = 1; index < found.Count; index++)
        {
            if (found[index - 1].Type == AgcShaderType.Geometry && found[index].Type == AgcShaderType.Pixel)
            {
                file.RecordPair(found[index - 1].Identity, found[index].Identity);
            }
        }

        return found.Count(entry => entry.Program is not null);
    }

    private static FoundProgram? TryRecord(Stream stream, long length, long offset, ShaderCacheFile file)
    {
        Span<byte> head = stackalloc byte[AgcShaderHeader.FixedBytes];
        if (offset + head.Length > length || ReadAt(stream, head, offset) != head.Length ||
            !AgcShaderHeader.TryPeekSizes(head, out var headerBytes, out var codeBytes, out var type) ||
            !AgcShaderHeader.IsSupported(type) || offset + headerBytes > length)
        {
            return null;
        }

        var headerData = new byte[headerBytes];
        if (ReadAt(stream, headerData, offset) != headerBytes || !AgcShaderHeader.TryParse(headerData, 0, out var header))
        {
            return null;
        }

        foreach (var codeOffset in CodeCandidates(offset, headerBytes, codeBytes))
        {
            if (codeOffset < 0 || codeOffset + codeBytes > length)
            {
                continue;
            }

            var code = new byte[codeBytes + CodeSlackBytes];
            var read = ReadAt(stream, code, codeOffset);
            if (read < codeBytes || !ShaderInventory.TryHash(code, (uint)codeBytes, ScannedCodeAddress, out var hash))
            {
                continue;
            }

            var known = ShaderCacheFile.ProgramIdentity(hash, (uint)codeBytes, header.Bytes);
            if (file.TryGetProgram(known, out _))
            {
                return new FoundProgram(offset, null, header.Type, known);
            }

            if (!ShaderInventory.TryCreateCapture(code.AsSpan(0, Math.Max(read, codeBytes)), (uint)codeBytes, ScannedCodeAddress, out var capture))
            {
                continue;
            }

            var program = new InventoryProgram { Code = capture.Key, Source = InventorySource.Scanned, Header = header.Bytes };
            var identity = file.RecordProgram(program, capture);
            var cached = identity is { } recorded && file.TryGetProgram(recorded, out var stored) ? stored : null;
            return new FoundProgram(offset, cached, header.Type, identity ?? ShaderCacheFile.ProgramIdentity(program));
        }

        return null;
    }

    private static IEnumerable<long> CodeCandidates(long headerOffset, int headerBytes, int codeBytes)
    {
        yield return headerOffset - codeBytes;
        yield return headerOffset + headerBytes;
        var aligned = (headerOffset + headerBytes + CodeAlignment - 1) / CodeAlignment * CodeAlignment;
        if (aligned != headerOffset + headerBytes)
        {
            yield return aligned;
        }
    }

    private static int ReadAt(Stream stream, Span<byte> destination, long offset)
    {
        stream.Position = offset;
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
