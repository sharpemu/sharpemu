// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace SharpEmu.HLE.FileSystem.Pkg;

// A fake/debug PS5 package ("\x7FFIH") opened for reading. Retail packages and packages whose outer
// PFS is encrypted are refused at Open: their files need console keys. Extraction decodes the files
// block by block on several threads (a big file is split into ranges too), so a package of any
// size needs only a few hundred KiB of memory per worker.
public sealed class Ps5Package : IGamePackage, IPackageContent
{
    private const string ImageFileName = "pfs_image.dat";
    private const string LayoutFileName = "naps_pkg_layout.dat";
    // A layout is a few MiB at most; anything larger is not a NAPS file.
    private const long MaxLayoutSize = 256L << 20;
    // Logical range of one parallel work item: large enough to keep seeks rare, small enough that a
    // few huge files still spread over every worker.
    private const long DefaultChunkBytes = 64L << 20;

    private readonly PackageSource _source;
    private readonly Ps5Fih _fih;
    private readonly Ps5Cnt _cnt;
    private readonly Ps5OuterPfs _outer;
    private readonly string? _paramTitle;
    private readonly string? _paramTitleId;

    private Ps5InnerImage? _inner;
    private List<Ps5InnerFile>? _files;
    private long _filesSize;

    private Ps5Package(PackageSource source, Ps5Fih fih, Ps5Cnt cnt, Ps5OuterPfs outer)
    {
        _source = source;
        _fih = fih;
        _cnt = cnt;
        _outer = outer;
        foreach (var file in cnt.Files)
        {
            if (file.Name == "param.json")
            {
                (_paramTitleId, _paramTitle) = ReadParamJson(file.Bytes);
                break;
            }
        }
    }

    public string ContentId => _cnt.ContentId;

    // param.json titleId, else the title id inside the content id (UP9000-PPSA01234_00-...).
    public string TitleId =>
        !string.IsNullOrWhiteSpace(_paramTitleId) ? _paramTitleId : ContentId.Length >= 16 ? ContentId.Substring(7, 9) : string.Empty;

    // param.json localizedParameters[defaultLanguage].titleName, else the en-US one.
    public string? Title => _paramTitle;

    // Nothing in the container reliably tells a game from a patch or DLC, so this stays null (a game).
    public string? Category => null;

    public bool IsGameApplication => true;

    // The plain sce_sys files of the container, names relative to sce_sys.
    internal IReadOnlyList<Ps5CntFile> MetadataFiles => _cnt.Files;

    public string GetFolderName(string fallback) =>
        PkgPackage.ToFolderName(Title) ?? PkgPackage.ToFolderName(TitleId) ?? fallback;

    public static Ps5Package Open(string path) => Open(new PackageSource(path));

    // Takes ownership of `source`.
    internal static Ps5Package Open(PackageSource source)
    {
        try
        {
            var fih = Ps5Fih.Read(source);
            var cnt = Ps5Cnt.Read(source, fih.CntOffset);
            var outer = Ps5OuterPfs.Open(source, fih.PfsOffset, fih.PfsSize, fih.SuperblockOffset);
            return new Ps5Package(source, fih, cnt, outer);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    // Size on disk of everything Extract writes (sce_sys metadata plus the game files), computed
    // without extracting. It decodes the inner image's metadata tail to walk the tree.
    public long GetInstalledSize()
    {
        LoadTree();
        return _filesSize + _cnt.FilesSize;
    }

    public void Extract(string destination, IProgress<PkgExtractProgress>? progress = null, CancellationToken cancellation = default) =>
        Extract(destination, progress, cancellation, PkgPackage.ProgressStepBytes, PkgPackage.GetAvailableSpace(destination));

    internal void Extract(
        string destination,
        IProgress<PkgExtractProgress>? progress,
        CancellationToken cancellation,
        long progressStepBytes,
        long? availableBytes) =>
        Extract(
            destination,
            progress,
            cancellation,
            progressStepBytes,
            availableBytes,
            DefaultChunkBytes,
            Math.Clamp(Environment.ProcessorCount / 2, 1, 8));

    internal void Extract(
        string destination,
        IProgress<PkgExtractProgress>? progress,
        CancellationToken cancellation,
        long progressStepBytes,
        long? availableBytes,
        long chunkBytes,
        int maxParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelism, 1);
        var (image, files) = LoadTree();
        var total = _filesSize;

        // Resolve every destination first: an unsafe name must fail before anything is written.
        var metadataRoot = Path.Combine(destination, "sce_sys");
        var metadata = _cnt.Files
            .Select(file => (Path: PkgPackage.ResolveSafe(metadataRoot, file.Name.Split('/')), file.Bytes))
            .ToList();
        var targets = files
            .Select(file => (Path: PkgPackage.ResolveSafe(destination, file.Components), File: file))
            .ToList();

        var needed = total + _cnt.FilesSize;
        if (availableBytes is { } available && available < needed)
        {
            throw new IOException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Not enough free disk space: need {needed / (1024.0 * 1024 * 1024):0.00} GB, {available / (1024.0 * 1024 * 1024):0.00} GB free."));
        }

        Directory.CreateDirectory(destination);
        foreach (var (path, bytes) in metadata)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        targets = DeduplicateTargets(targets);

        // Create every file at its final size first, so workers only write inside existing files and
        // never race to create or grow one.
        var items = new List<WorkItem>();
        foreach (var (path, file) in targets)
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var created = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                MarkSparse(created);
                RandomAccess.SetLength(created, file.Size);
            }

            var relative = Path.GetRelativePath(destination, path);
            for (long offset = 0; offset < file.Size; offset += chunkBytes)
            {
                items.Add(new WorkItem(path, relative, file.Logical, offset, Math.Min(chunkBytes, file.Size - offset)));
            }
        }

        progress?.Report(new PkgExtractProgress(0, total, string.Empty));
        var tracker = new ProgressTracker(progress, total, progressStepBytes);
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = cancellation };
        try
        {
            // Load-balanced partitioning hands out one item at a time, so a few big files still keep
            // every worker busy.
            Parallel.ForEach(
                Partitioner.Create(items, loadBalance: true),
                options,
                image.CreateReader,
                (item, _, reader) =>
                {
                    WriteRange(reader, item, cancellation, tracker);
                    return reader;
                },
                _ => { });
        }
        catch (AggregateException aggregate)
        {
            // Callers catch the specific exception types, not AggregateException.
            cancellation.ThrowIfCancellationRequested();
            var inner = aggregate.Flatten().InnerExceptions;
            ExceptionDispatchInfo.Capture(inner.FirstOrDefault(e => e is not OperationCanceledException) ?? inner[0]).Throw();
        }

        progress?.Report(new PkgExtractProgress(total, total, string.Empty));
    }

    private static void WriteRange(Ps5InnerImage.BlockReader reader, WorkItem item, CancellationToken cancellation, ProgressTracker tracker)
    {
        cancellation.ThrowIfCancellationRequested();
        using var output = File.OpenHandle(item.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var position = item.Offset;
        reader.CopyTo(item.Logical + item.Offset, item.Length, chunk =>
        {
            RandomAccess.Write(output, chunk, position);
            position += chunk.Length;
            tracker.Add(chunk.Length, item.Relative);
            cancellation.ThrowIfCancellationRequested();
        });
    }

    // Two inner files can map to one host path (names differing only by case on Windows). The old
    // sequential loop let the later one overwrite the earlier, so only the last occurrence is kept.
    private static List<(string Path, Ps5InnerFile File)> DeduplicateTargets(List<(string Path, Ps5InnerFile File)> targets)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var kept = new List<(string Path, Ps5InnerFile File)>(targets.Count);
        for (var i = targets.Count - 1; i >= 0; i--)
        {
            if (seen.Add(targets[i].Path))
            {
                kept.Add(targets[i]);
            }
        }

        kept.Reverse();
        return kept;
    }

    // Workers write the ranges of one file out of order. NTFS zero-fills everything between the
    // last written byte and a write past it, so a non-sparse file would get most of its bytes
    // written twice; a sparse file skips that fill. The file ends fully written, so no hole is
    // left. Volumes without sparse support (FAT, exFAT) just keep the zero fill.
    private static void MarkSparse(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = DeviceIoControl(handle, FsctlSetSparse, 0, 0, 0, 0, out _, 0);
        }
    }

    private const uint FsctlSetSparse = 0x000900C4;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device,
        uint controlCode,
        nint inBuffer,
        uint inBufferSize,
        nint outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    private readonly record struct WorkItem(string Path, string Relative, long Logical, long Offset, long Length);

    // Progress from several threads: bytes add up atomically and a report goes out at most every
    // `step` bytes, under a lock, with a BytesDone that never goes backwards.
    private sealed class ProgressTracker(IProgress<PkgExtractProgress>? progress, long total, long step)
    {
        private readonly object _gate = new();
        private long _done;
        private long _reported;

        public void Add(int bytes, string file)
        {
            var done = Interlocked.Add(ref _done, bytes);
            if (progress is null || done - Volatile.Read(ref _reported) < step)
            {
                return;
            }

            lock (_gate)
            {
                // Read again under the lock: other workers may have added bytes meanwhile.
                var current = Interlocked.Read(ref _done);
                if (current - _reported < step)
                {
                    return;
                }

                _reported = current;
                progress.Report(new PkgExtractProgress(current, total, file));
            }
        }
    }

    PackageSource IPackageContent.Source => _source;

    IReadOnlyList<PackageContentFile> IPackageContent.ListContentFiles()
    {
        var (_, files) = LoadTree();
        var content = new List<PackageContentFile>(_cnt.Files.Count + files.Count);
        foreach (var file in _cnt.Files)
        {
            PkgPackage.ResolveSafe(string.Empty, ["sce_sys", .. file.Name.Split('/')]);
            content.Add(new PackageContentFile("sce_sys/" + file.Name, file.Bytes.Length, file.Bytes, null));
        }

        foreach (var file in files)
        {
            PkgPackage.ResolveSafe(string.Empty, file.Components);
            content.Add(new PackageContentFile(string.Join('/', file.Components), file.Size, null, file));
        }

        return content;
    }

    long IPackageContent.SourceEnd(PackageContentFile file, long offset, long length) =>
        file.Native is Ps5InnerFile inner ? LoadTree().Image.SourceEnd(inner.Logical + offset, length) : 0;

    IPackageContentReader IPackageContent.CreateContentReader() => new ContentReader(LoadTree().Image.CreateReader());

    private sealed class ContentReader(Ps5InnerImage.BlockReader reader) : IPackageContentReader
    {
        public void Read(PackageContentFile file, long offset, Memory<byte> destination)
        {
            var inner = (Ps5InnerFile)file.Native!;
            var done = 0;
            reader.CopyTo(inner.Logical + offset, destination.Length, chunk =>
            {
                chunk.CopyTo(destination.Span[done..]);
                done += chunk.Length;
            });
            if (done != destination.Length)
            {
                throw new InvalidDataException("The inner image returned fewer bytes than asked for.");
            }
        }

        public void Dispose()
        {
        }
    }

    // Opens the inner image and lists its files once; both GetInstalledSize and Extract need them.
    internal (Ps5InnerImage Image, List<Ps5InnerFile> Files) LoadTree()
    {
        if (_inner is not null && _files is not null)
        {
            return (_inner, _files);
        }

        var imageInode = _outer.Find(ImageFileName)
            ?? throw new InvalidDataException($"The PS5 package's outer PFS has no {ImageFileName}.");
        var layoutInode = _outer.Find(LayoutFileName)
            ?? throw new InvalidDataException($"The PS5 package's outer PFS has no {LayoutFileName}; images without a NAPS layout are not supported.");
        var layoutFile = _outer.OpenFile(layoutInode);
        if (layoutFile.Size > MaxLayoutSize)
        {
            throw new InvalidDataException($"{LayoutFileName} is far larger than any layout.");
        }

        var inner = Ps5InnerImage.Open(_outer.OpenFile(imageInode), layoutFile.ReadAll());
        var files = inner.ListFiles();
        long size = 0;
        foreach (var file in files)
        {
            // Every name goes through ResolveSafe again at Extract; the size can be summed now.
            size += file.Size;
        }

        _inner = inner;
        _files = files;
        _filesSize = size;
        return (inner, files);
    }

    private static (string? TitleId, string? Title) ReadParamJson(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var titleId = root.TryGetProperty("titleId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            string? title = null;
            if (root.TryGetProperty("localizedParameters", out var localized) && localized.ValueKind == JsonValueKind.Object)
            {
                var defaultLanguage = localized.TryGetProperty("defaultLanguage", out var language) && language.ValueKind == JsonValueKind.String
                    ? language.GetString()
                    : null;
                // defaultLanguage sits inside localizedParameters; a copy at the top level is tolerated too.
                if (defaultLanguage is null && root.TryGetProperty("defaultLanguage", out var rootLanguage) && rootLanguage.ValueKind == JsonValueKind.String)
                {
                    defaultLanguage = rootLanguage.GetString();
                }

                foreach (var key in new[] { defaultLanguage, "en-US" })
                {
                    if (key is not null
                        && localized.TryGetProperty(key, out var entry)
                        && entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("titleName", out var name)
                        && name.ValueKind == JsonValueKind.String)
                    {
                        title = name.GetString();
                        break;
                    }
                }
            }

            return (titleId, title);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public void Dispose() => _source.Dispose();
}
