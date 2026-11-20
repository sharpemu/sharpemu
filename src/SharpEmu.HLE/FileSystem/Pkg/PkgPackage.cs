// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.FileSystem.Pkg;

public readonly record struct PkgExtractProgress(long BytesDone, long BytesTotal, string CurrentFile);

// A fake (fpkg) PS4 package opened for reading. Extraction streams every file block by block,
// so a package of any size needs only a few hundred KiB of memory.
public sealed class PkgPackage : IDisposable
{
    private const int EntryKeysTableOffset = 32 + 7 * 32; // seed digest + 7 digests, then 7 RSA keys
    private const int EntryKeySize = 256;
    private const long PfsSeedOffset = 0x370;

    private readonly FileStream _file;
    private readonly byte[] _entryTable;

    private PkgPackage(FileStream file, PkgHeader header, byte[] entryTable, PkgEntry[] entries)
    {
        _file = file;
        Header = header;
        _entryTable = entryTable;
        Entries = entries;
    }

    public PkgHeader Header { get; }

    public IReadOnlyList<PkgEntry> Entries { get; }

    public string TitleId => Header.TitleId;

    public static bool IsPkgPath(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".pkg", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            Span<byte> magic = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(magic) == 4 && PkgHeader.HasMagic(magic);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static PkgPackage Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
        try
        {
            var headerBytes = new byte[PkgHeader.Size];
            file.ReadExactly(headerBytes);
            var header = PkgHeader.Parse(headerBytes);
            if (header.PkgSize > (ulong)file.Length)
            {
                throw new InvalidDataException("The PKG file is shorter than its header says (incomplete download?).");
            }

            if (header.TableEntryCount > 0x10000)
            {
                throw new InvalidDataException("PKG entry table is implausibly large.");
            }

            var table = new byte[checked((int)header.TableEntryCount * PkgEntry.Size_)];
            file.Position = header.TableEntryOffset;
            file.ReadExactly(table);
            return new PkgPackage(file, header, table, PkgEntry.ParseTable(table, (int)header.TableEntryCount));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public byte[] ReadEntry(PkgEntry entry)
    {
        var data = new byte[entry.Size];
        _file.Position = entry.Offset;
        _file.ReadExactly(data);
        return data;
    }

    public byte[]? ReadParamSfo()
    {
        foreach (var entry in Entries)
        {
            if (entry.Id == 0x1000)
            {
                return ReadEntry(entry);
            }
        }

        return null;
    }

    // Writes sce_sys metadata and the whole PFS tree into `destination`, which then is a game
    // folder (eboot.bin at its root).
    public void Extract(string destination, IProgress<PkgExtractProgress>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Entries)
        {
            if (entry.Name is { } name)
            {
                var path = ResolveSafe(Path.Combine(destination, "sce_sys"), name.Split('/'));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, ReadEntry(entry));
            }
        }

        using var image = OpenPfsImageStream();
        var pfsc = new PfscImage(image, PfscImage.FindOffset(image));
        var pfs = new PfsImage(pfsc);

        var files = new List<(string Path, PfsInode Inode)>();
        long total = 0;
        CollectTree(pfs, pfs.GetUserRoot(), destination, files, ref total, new HashSet<int>(), 0);

        long done = 0;
        foreach (var (path, inode) in files)
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var relative = Path.GetRelativePath(destination, path);
            progress?.Report(new PkgExtractProgress(done, total, relative));
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            pfs.CopyFile(inode, output, bytes =>
            {
                done += bytes;
                cancellation.ThrowIfCancellationRequested();
            });
            progress?.Report(new PkgExtractProgress(done, total, relative));
        }

        progress?.Report(new PkgExtractProgress(total, total, string.Empty));
    }

    private PkgPfsStream OpenPfsImageStream()
    {
        var imageKeyEntry = FindEntry(PkgEntry.ImageKeyId, out var imageKeyRow)
            ?? throw new InvalidDataException("PKG has no image key entry.");
        var keysEntry = FindEntry(PkgEntry.EntryKeysId, out _)
            ?? throw new InvalidDataException("PKG has no entry keys entry.");

        var keys = ReadEntry(keysEntry);
        var key3Offset = EntryKeysTableOffset + 3 * EntryKeySize;
        if (keys.Length < key3Offset + EntryKeySize)
        {
            throw new InvalidDataException("PKG entry keys entry is truncated.");
        }

        var dk3 = PkgCrypto.DeriveDk3(keys.AsSpan(key3Offset, EntryKeySize));
        var imageKey = ReadEntry(imageKeyEntry);
        if (imageKey.Length < EntryKeySize)
        {
            throw new InvalidDataException("PKG image key entry is truncated.");
        }

        var ekpfs = PkgCrypto.DeriveEkpfs(imageKeyRow, dk3, imageKey.AsSpan(0, EntryKeySize));

        var seed = new byte[16];
        _file.Position = (long)Header.PfsImageOffset + PfsSeedOffset;
        _file.ReadExactly(seed);
        var (dataKey, tweakKey) = PkgCrypto.DerivePfsKeys(ekpfs, seed);
        return new PkgPfsStream(_file, (long)Header.PfsImageOffset, (long)Header.PfsImageSize, dataKey, tweakKey);
    }

    private PkgEntry? FindEntry(uint id, out byte[] row)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Id == id)
            {
                row = _entryTable.AsSpan(i * PkgEntry.Size_, PkgEntry.Size_).ToArray();
                return Entries[i];
            }
        }

        row = [];
        return null;
    }

    private static void CollectTree(
        PfsImage pfs,
        PfsInode directory,
        string path,
        List<(string, PfsInode)> files,
        ref long total,
        HashSet<int> visiting,
        int depth)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("PFS directory tree is too deep.");
        }

        foreach (var entry in pfs.ReadDirectory(directory))
        {
            if (entry.Type is (int)PfsDirentType.CurrentDirectory or (int)PfsDirentType.ParentDirectory)
            {
                continue;
            }

            var target = ResolveSafe(path, [entry.Name]);
            var inode = pfs.GetInode(entry.Inode);
            if (entry.Type == (int)PfsDirentType.Directory)
            {
                if (!visiting.Add(entry.Inode))
                {
                    throw new InvalidDataException("PFS directory tree has a cycle.");
                }

                CollectTree(pfs, inode, target, files, ref total, visiting, depth + 1);
                visiting.Remove(entry.Inode);
            }
            else if (entry.Type == (int)PfsDirentType.File)
            {
                files.Add((target, inode));
                total += inode.Size;
            }
        }
    }

    // Joins path components below `root`, refusing anything that could leave it.
    internal static string ResolveSafe(string root, string[] components)
    {
        var path = root;
        foreach (var component in components)
        {
            if (component.Length == 0 || component is "." or ".." ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"Unsafe path component '{component}' in package.");
            }

            path = Path.Combine(path, component);
        }

        return path;
    }

    public void Dispose() => _file.Dispose();
}
