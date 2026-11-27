// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.FileSystem.Pkg;

public readonly record struct PkgExtractProgress(long BytesDone, long BytesTotal, string CurrentFile);

// A fake (fpkg) PS4 package opened for reading. Extraction streams every file block by block,
// so a package of any size needs only a few hundred KiB of memory.
public sealed class PkgPackage : IGamePackage, IPackageContent
{
    private const int EntryKeysTableOffset = 32 + 7 * 32; // seed digest + 7 digests, then 7 RSA keys
    private const int EntryKeySize = 256;
    private const long PfsSeedOffset = 0x370;

    private readonly PackageSource _source;
    private readonly Stream _file;
    private readonly byte[] _entryTable;
    private (byte[]? Data, byte[]? Tweak)? _pfsKeys;
    // The image the content listing walked; SourceEnd maps file blocks through it.
    private PkgPfsStream? _contentImage;
    private PfscImage? _contentPfsc;
    private string? _category;
    private bool _categoryRead;

    private PkgPackage(PackageSource source, Stream file, PkgHeader header, byte[] entryTable, PkgEntry[] entries)
    {
        _source = source;
        _file = file;
        Header = header;
        _entryTable = entryTable;
        Entries = entries;
    }

    public PkgHeader Header { get; }

    public IReadOnlyList<PkgEntry> Entries { get; }

    public string ContentId => Header.ContentId;

    public string TitleId => Header.TitleId;

    // param.sfo CATEGORY (gd = game, gp = patch, ac = DLC...), or null when absent. Read once.
    public string? Category
    {
        get
        {
            if (!_categoryRead)
            {
                var sfo = ReadParamSfo();
                _category = sfo is null ? null : PkgParamSfo.ReadString(sfo, "CATEGORY");
                _categoryRead = true;
            }

            return _category;
        }
    }

    // param.sfo TITLE (the game's name), or null when absent.
    public string? Title => ReadParamSfo() is { } sfo ? PkgParamSfo.ReadString(sfo, "TITLE") : null;

    // Folder name for an install: the game's title made safe for a file name, else its title id,
    // else `fallback` (usually the package file name).
    public string GetFolderName(string fallback) =>
        ToFolderName(Title) ?? ToFolderName(TitleId) ?? fallback;

    // The characters Windows refuses in a file name. The host's own list is only '/' and NUL on
    // Linux, so extracted trees and folder names would differ by platform, and a package entry
    // holding '\' would pass there and become a nested path once copied to Windows.
    private static readonly char[] PortableInvalidNameChars =
        ['"', '<', '>', '|', ':', '*', '?', '\\', '/', .. Enumerable.Range(0, 32).Select(code => (char)code)];

    internal static string? ToFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            // Titles may hold line breaks or characters Windows refuses (':' in "Part I: ...").
            builder.Append(char.IsControl(c) || Array.IndexOf(PortableInvalidNameChars, c) >= 0 ? ' ' : c);
        }

        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        return cleaned.Length == 0 || cleaned is "." or ".." ? null : cleaned;
    }

    // Base games are "gd" (and variants such as "gde"). Packages without a category are treated as
    // games; patches ("gp") and DLC ("ac") are not.
    public bool IsGameApplication => Category is null || Category.StartsWith("gd", StringComparison.OrdinalIgnoreCase);

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

    public static PkgPackage Open(string path) => Open(new PackageSource(path));

    // Takes ownership of `source`.
    internal static PkgPackage Open(PackageSource source)
    {
        var file = new PackageSourceStream(source);
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

            if ((ulong)header.TableEntryOffset + (ulong)header.TableEntryCount * PkgEntry.Size_ > header.PkgSize ||
                (ulong)header.PfsImageOffset + header.PfsImageSize > header.PkgSize)
            {
                throw new InvalidDataException("PKG points outside its own bounds.");
            }

            var table = new byte[checked((int)header.TableEntryCount * PkgEntry.Size_)];
            file.Position = header.TableEntryOffset;
            file.ReadExactly(table);
            return new PkgPackage(source, file, header, table, PkgEntry.ParseTable(table, (int)header.TableEntryCount));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public byte[] ReadEntry(PkgEntry entry)
    {
        // A corrupt table must never make us allocate gigabytes before we notice.
        if ((ulong)entry.Offset + entry.Size > Header.PkgSize)
        {
            throw new InvalidDataException("PKG entry lies outside the package.");
        }

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

    // Bytes between progress reports while one large file is being written.
    internal const long ProgressStepBytes = 16L * 1024 * 1024;

    // Size on disk of everything Extract writes (sce_sys metadata plus the PFS files), computed
    // without extracting. It opens the PFS image to walk its tree.
    public long GetInstalledSize()
    {
        using var image = OpenPfsImageStream();
        var pfs = new PfsImage(new PfscImage(image, PfscImage.FindOffset(image)));
        var files = new List<(string Path, PfsInode Inode)>();
        long total = 0;
        CollectTree(pfs, pfs.GetUserRoot(), string.Empty, files, ref total, new HashSet<int>(), 0);
        return total + MetadataSize();
    }

    private long MetadataSize()
    {
        long size = 0;
        foreach (var entry in Entries)
        {
            if (entry.Name is not null)
            {
                size += entry.Size;
            }
        }

        return size;
    }

    // Free bytes on the drive that will hold `destination`, or null when it cannot be told.
    internal static long? GetAvailableSpace(string destination)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // Writes sce_sys metadata and the whole PFS tree into `destination`, which then is a game
    // folder (eboot.bin at its root). Throws IOException before writing anything when the drive
    // is known to be too small.
    public void Extract(string destination, IProgress<PkgExtractProgress>? progress = null, CancellationToken cancellation = default) =>
        Extract(destination, progress, cancellation, ProgressStepBytes, GetAvailableSpace(destination));

    internal void Extract(
        string destination,
        IProgress<PkgExtractProgress>? progress,
        CancellationToken cancellation,
        long progressStepBytes,
        long? availableBytes)
    {
        using var image = OpenPfsImageStream();
        var pfsc = new PfscImage(image, PfscImage.FindOffset(image));
        var pfs = new PfsImage(pfsc);

        var files = new List<(string Path, PfsInode Inode)>();
        long total = 0;
        CollectTree(pfs, pfs.GetUserRoot(), destination, files, ref total, new HashSet<int>(), 0);

        var needed = total + MetadataSize();
        if (availableBytes is { } available && available < needed)
        {
            throw new IOException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Not enough free disk space: need {needed / (1024.0 * 1024 * 1024):0.00} GB, {available / (1024.0 * 1024 * 1024):0.00} GB free."));
        }

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

        long done = 0;
        foreach (var (path, inode) in files)
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var relative = Path.GetRelativePath(destination, path);
            progress?.Report(new PkgExtractProgress(done, total, relative));
            var sinceReport = 0L;
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            pfs.CopyFile(inode, output, bytes =>
            {
                done += bytes;
                sinceReport += bytes;
                cancellation.ThrowIfCancellationRequested();
                if (sinceReport >= progressStepBytes)
                {
                    sinceReport = 0;
                    progress?.Report(new PkgExtractProgress(done, total, relative));
                }
            });
            progress?.Report(new PkgExtractProgress(done, total, relative));
        }

        progress?.Report(new PkgExtractProgress(total, total, string.Empty));
    }

    private PkgPfsStream OpenPfsImageStream() => OpenPfsImageStream(_file);

    // The decrypted PFS image over `file` (the package itself, or a per-thread view of it).
    private PkgPfsStream OpenPfsImageStream(Stream file)
    {
        var (dataKey, tweakKey) = _pfsKeys ??= DerivePfsKeys();
        return new PkgPfsStream(file, (long)Header.PfsImageOffset, (long)Header.PfsImageSize, dataKey, tweakKey);
    }

    private (byte[]? Data, byte[]? Tweak) DerivePfsKeys()
    {
        var imageKeyEntry = FindEntry(PkgEntry.ImageKeyId, out var imageKeyRow);
        var keysEntry = FindEntry(PkgEntry.EntryKeysId, out _);
        if (imageKeyEntry is null || keysEntry is null)
        {
            // No key material means the PFS image is stored in the clear.
            return (null, null);
        }

        var keys = ReadEntry(keysEntry.Value);
        var key3Offset = EntryKeysTableOffset + 3 * EntryKeySize;
        if (keys.Length < key3Offset + EntryKeySize)
        {
            throw new InvalidDataException("PKG entry keys entry is truncated.");
        }

        var dk3 = PkgCrypto.DeriveDk3(keys.AsSpan(key3Offset, EntryKeySize));
        var imageKey = ReadEntry(imageKeyEntry.Value);
        if (imageKey.Length < EntryKeySize)
        {
            throw new InvalidDataException("PKG image key entry is truncated.");
        }

        var ekpfs = PkgCrypto.DeriveEkpfs(imageKeyRow, dk3, imageKey.AsSpan(0, EntryKeySize));

        var seed = new byte[16];
        _file.Position = (long)Header.PfsImageOffset + PfsSeedOffset;
        _file.ReadExactly(seed);
        return PkgCrypto.DerivePfsKeys(ekpfs, seed);
    }

    PackageSource IPackageContent.Source => _source;

    IReadOnlyList<PackageContentFile> IPackageContent.ListContentFiles()
    {
        var content = new List<PackageContentFile>();
        foreach (var entry in Entries)
        {
            if (entry.Name is { } name)
            {
                ResolveSafe(string.Empty, ["sce_sys", .. name.Split('/')]);
                var bytes = ReadEntry(entry);
                content.Add(new PackageContentFile("sce_sys/" + name, bytes.Length, bytes, null));
            }
        }

        _contentImage?.Dispose();
        _contentImage = OpenPfsImageStream();
        _contentPfsc = new PfscImage(_contentImage, PfscImage.FindOffset(_contentImage));
        var pfs = new PfsImage(_contentPfsc);
        var files = new List<(string Path, PfsInode Inode)>();
        long total = 0;
        CollectTree(pfs, pfs.GetUserRoot(), string.Empty, files, ref total, new HashSet<int>(), 0);
        foreach (var (path, inode) in files)
        {
            if (inode.Size < 0 || inode.Blocks * (long)PfscImage.BlockSize < inode.Size)
            {
                throw new InvalidDataException("PFS file claims more data than its blocks hold.");
            }

            content.Add(new PackageContentFile(string.Join('/', path.Split(Path.DirectorySeparatorChar)), inode.Size, null, inode));
        }

        return content;
    }

    long IPackageContent.SourceEnd(PackageContentFile file, long offset, long length)
    {
        if (file.Native is not PfsInode inode || length <= 0)
        {
            return 0;
        }

        var pfsc = _contentPfsc ?? throw new InvalidOperationException("List the package content first.");
        long end = 0;
        var last = (offset + length - 1) / PfscImage.BlockSize;
        for (var block = offset / PfscImage.BlockSize; block <= last; block++)
        {
            var (start, size) = pfsc.BlockExtent((long)inode.Location + block);
            if (size > 0)
            {
                end = Math.Max(end, _contentImage!.SourceEnd(pfsc.Offset + start, size));
            }
        }

        return end;
    }

    IPackageContentReader IPackageContent.CreateContentReader()
    {
        var pfsc = _contentPfsc ?? throw new InvalidOperationException("List the package content first.");
        var image = OpenPfsImageStream(new PackageSourceStream(_source));
        return new ContentReader(image, new PfscImage(pfsc, image));
    }

    // Reads file ranges block by block, as PfsImage.CopyFile does, through its own image view.
    private sealed class ContentReader(PkgPfsStream image, PfscImage pfsc) : IPackageContentReader
    {
        private readonly byte[] _block = new byte[PfscImage.BlockSize];

        public void Read(PackageContentFile file, long offset, Memory<byte> destination)
        {
            var inode = (PfsInode)file.Native!;
            var span = destination.Span;
            var done = 0;
            while (done < span.Length)
            {
                var position = offset + done;
                var block = position / PfscImage.BlockSize;
                var within = (int)(position % PfscImage.BlockSize);
                pfsc.ReadBlock((long)inode.Location + block, _block);
                var count = Math.Min(span.Length - done, PfscImage.BlockSize - within);
                _block.AsSpan(within, count).CopyTo(span[done..]);
                done += count;
            }
        }

        public void Dispose() => image.Dispose();
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
                component.IndexOfAny(PortableInvalidNameChars) >= 0)
            {
                throw new InvalidDataException($"Unsafe path component '{component}' in package.");
            }

            path = Path.Combine(path, component);
        }

        return path;
    }

    public void Dispose()
    {
        _contentImage?.Dispose();
        _file.Dispose();
        _source.Dispose();
    }
}
