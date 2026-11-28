// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using SharpEmu.Logging;
using ZstdSharp;

namespace SharpEmu.HLE.FileSystem.Pkg;

// Thrown when a consuming conversion runs out of disk space. Nothing is lost: the journal, the
// partial archive and what is left of the package resume the conversion when it runs again.
public sealed class PkgConversionPausedException(string message, Exception? inner) : IOException(message, inner);

// Converts a fake PS4 or PS5 .pkg straight into a .zar, without an extracted folder.
//
// Files go into the archive's data stream in ascending order of where their bytes sit in the
// package, so each 1 MiB offset-record group (16 blocks of 64 KiB) needs a compact stretch of the
// package. For every group the plan knows the highest package byte decoding it reads (its end).
//
// Direct mode writes the groups in logical order and leaves the package alone.
//
// Consume mode shrinks the package as its data lands in the archive, so the package, a folder and
// the archive never need to fit on the drive together. Groups are processed in descending end
// order, in batches covering a window of the package. Each batch: decode and compress in
// parallel, append to <dest>.partial, flush it to stable storage, append the batch's placements to
// <dest>.journal and flush that, and only then truncate the package to the highest end of the
// groups still to do. The journal starts with a snapshot of every package range the parser read
// while opening the package, so a later run reopens the shrunk package exactly, re-derives the
// same plan, checks it against the journal and continues after the last committed batch. The
// reader resolves every group through its record's base offset, so groups may sit in the data
// section in any order.
public static class PkgZarConverter
{
    public const long DefaultWindowBytes = 256L << 20;

    private static readonly SharpEmuLogger Log = SharpEmuLog.For("PkgZar");

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string PartialPath(string destination) => destination + ".partial";

    public static string JournalPath(string destination) => destination + ".journal";

    public static void Convert(
        string packagePath,
        string destinationPath,
        bool consumeSource,
        IProgress<PkgExtractProgress>? progress = null,
        CancellationToken cancellation = default) =>
        Convert(packagePath, destinationPath, new PkgZarOptions { ConsumeSource = consumeSource }, progress, cancellation);

    // The destination of an unfinished consuming conversion of this package: a journal beside it
    // that names it. Null when there is none.
    public static string? FindPendingDestination(string packagePath)
    {
        var package = Path.GetFullPath(packagePath);
        var directory = Path.GetDirectoryName(package)!;
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var journal in Directory.EnumerateFiles(directory, "*.zar.journal"))
        {
            try
            {
                if (string.Equals(Journal.ReadPackagePath(journal), package, PathComparison))
                {
                    return journal[..^".journal".Length];
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Warn($"Skipping unreadable journal {journal}: {exception.Message}");
            }
        }

        return null;
    }

    internal static void Convert(
        string packagePath,
        string destinationPath,
        PkgZarOptions options,
        IProgress<PkgExtractProgress>? progress,
        CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WindowBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxParallelism, 1);
        var package = Path.GetFullPath(packagePath);
        var destination = Path.GetFullPath(destinationPath);
        if (options.ConsumeSource)
        {
            ConvertConsuming(package, destination, options, progress, cancellation);
        }
        else
        {
            ConvertDirect(package, destination, options, progress, cancellation);
        }
    }

    // ---- plan ----------------------------------------------------------------------------------

    internal sealed record PlannedFile(PackageContentFile File, string[] Parts, long DataOffset, long SourceKey);

    internal sealed class Plan
    {
        public required List<PlannedFile> Files { get; init; }

        public required long DataSize { get; init; }

        // Per group: the package offset just past the last byte decoding it reads.
        public required long[] GroupEnds { get; init; }

        public required byte[] Fingerprint { get; init; }

        public int GroupCount => GroupEnds.Length;
    }

    private static IPackageContent OpenContent(PackageSource source)
    {
        var package = GamePackage.Open(source);
        if (!package.IsGameApplication)
        {
            var category = package.Category;
            package.Dispose();
            throw new InvalidDataException($"The package is not a base game (category '{category}'). Patches and DLC packages are not supported yet.");
        }

        return (IPackageContent)package;
    }

    internal static Plan BuildPlan(IPackageContent content, long windowBytes)
    {
        var listed = content.ListContentFiles();
        // The archive looks names up without case, so of two files mapping to one path the later
        // one wins, as the later write wins on a case-insensitive drive.
        var last = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < listed.Count; index++)
        {
            last[listed[index].Path] = index;
        }

        var kept = new List<(PackageContentFile File, long Key, int Index)>();
        for (var index = 0; index < listed.Count; index++)
        {
            if (last[listed[index].Path] == index)
            {
                var file = listed[index];
                kept.Add((file, content.SourceEnd(file, 0, file.Size), index));
            }
        }

        kept.Sort((left, right) => left.Key != right.Key ? left.Key.CompareTo(right.Key) : left.Index.CompareTo(right.Index));
        var files = new List<PlannedFile>(kept.Count);
        long dataSize = 0;
        foreach (var (file, key, _) in kept)
        {
            files.Add(new PlannedFile(file, file.Path.Split('/'), dataSize, key));
            dataSize += file.Size;
        }

        var groupCount = (int)((dataSize + ZArchiveWriter.RecordSpan - 1) / ZArchiveWriter.RecordSpan);
        var ends = new long[groupCount];
        var at = 0;
        for (var group = 0; group < groupCount; group++)
        {
            var start = (long)group * ZArchiveWriter.RecordSpan;
            var stop = Math.Min(dataSize, start + ZArchiveWriter.RecordSpan);
            while (files[at].DataOffset + files[at].File.Size <= start)
            {
                at++;
            }

            long end = 0;
            for (var index = at; index < files.Count && files[index].DataOffset < stop; index++)
            {
                var file = files[index];
                var from = Math.Max(start, file.DataOffset);
                var to = Math.Min(stop, file.DataOffset + file.File.Size);
                if (to > from)
                {
                    end = Math.Max(end, content.SourceEnd(file.File, from - file.DataOffset, to - from));
                }
            }

            ends[group] = end;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(long value) => hash.AppendData(BitConverter.GetBytes(value));
        Add(files.Count);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.File.Path));
            Add(file.File.Size);
            Add(file.DataOffset);
            Add(file.SourceKey);
        }

        Add(dataSize);
        Add(windowBytes);
        foreach (var end in ends)
        {
            Add(end);
        }

        return new Plan { Files = files, DataSize = dataSize, GroupEnds = ends, Fingerprint = hash.GetHashAndReset() };
    }

    // Groups in descending end order, cut into batches that each span at most `windowBytes` of the
    // package and at most `windowBytes` of archive data.
    internal static List<int[]> BuildBatches(Plan plan, long windowBytes)
    {
        var order = Enumerable.Range(0, plan.GroupCount)
            .OrderByDescending(group => plan.GroupEnds[group])
            .ThenByDescending(group => group)
            .ToList();
        var batches = new List<int[]>();
        var current = new List<int>();
        long top = 0;
        foreach (var group in order)
        {
            if (current.Count > 0 &&
                (top - plan.GroupEnds[group] >= windowBytes || (long)(current.Count + 1) * ZArchiveWriter.RecordSpan > windowBytes))
            {
                batches.Add([.. current]);
                current.Clear();
            }

            if (current.Count == 0)
            {
                top = plan.GroupEnds[group];
            }

            current.Add(group);
        }

        if (current.Count > 0)
        {
            batches.Add([.. current]);
        }

        return batches;
    }

    // ---- encoding ------------------------------------------------------------------------------

    internal readonly record struct EncodedGroup(byte[] Data, int Length, ushort[] Sizes);

    // Decode and compress state for one thread.
    private sealed class GroupEncoder(IPackageContent content) : IDisposable
    {
        private readonly IPackageContentReader _reader = content.CreateContentReader();
        private readonly Compressor _compressor = new(ZArchiveWriter.CompressionLevel);
        private readonly byte[] _logical = new byte[ZArchiveWriter.RecordSpan];
        private readonly byte[] _encoded = new byte[ZArchiveWriter.EncodeBufferSize];

        public EncodedGroup Encode(Plan plan, int group)
        {
            var start = (long)group * ZArchiveWriter.RecordSpan;
            var length = (int)Math.Min(ZArchiveWriter.RecordSpan, plan.DataSize - start);
            Fill(plan, start, length);
            var blocks = (length + ZArchive.BlockSize - 1) / ZArchive.BlockSize;
            Array.Clear(_logical, length, blocks * ZArchive.BlockSize - length);
            var data = new byte[blocks * ZArchive.BlockSize];
            var sizes = new ushort[ZArchiveWriter.BlocksPerRecord];
            var written = 0;
            for (var block = 0; block < blocks; block++)
            {
                var size = ZArchiveWriter.EncodeBlock(_compressor, _logical.AsSpan(block * ZArchive.BlockSize, ZArchive.BlockSize), _encoded);
                _encoded.AsSpan(0, size).CopyTo(data.AsSpan(written));
                written += size;
                sizes[block] = (ushort)(size - 1);
            }

            return new EncodedGroup(data, written, sizes);
        }

        private void Fill(Plan plan, long start, int length)
        {
            var files = plan.Files;
            // The last file starting at or before `start`.
            int low = 0, high = files.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (files[mid].DataOffset <= start)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            var position = start;
            var stop = start + length;
            for (var index = Math.Max(0, low - 1); position < stop && index < files.Count; index++)
            {
                var file = files[index];
                var fileEnd = file.DataOffset + file.File.Size;
                if (fileEnd <= position)
                {
                    continue;
                }

                var count = (int)(Math.Min(stop, fileEnd) - position);
                var target = _logical.AsMemory((int)(position - start), count);
                var offset = position - file.DataOffset;
                if (file.File.Bytes is { } bytes)
                {
                    bytes.AsSpan((int)offset, count).CopyTo(target.Span);
                }
                else
                {
                    _reader.Read(file.File, offset, target);
                }

                position += count;
            }

            if (position != stop)
            {
                throw new InvalidDataException("The archive plan does not cover its data.");
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _compressor.Dispose();
        }
    }

    // Encodes `groups` on several threads and hands each result to `write` in list order. At most
    // a few groups per worker are held in memory at once.
    private sealed class GroupPipeline(IPackageContent content, int parallelism) : IDisposable
    {
        private readonly ConcurrentBag<GroupEncoder> _encoders = [];

        public void Run(Plan plan, IReadOnlyList<int> groups, Action<int, EncodedGroup> write, CancellationToken cancellation)
        {
            var window = parallelism * 2;
            var results = new EncodedGroup[window];
            for (var first = 0; first < groups.Count; first += window)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = Math.Min(window, groups.Count - first);
                try
                {
                    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellation }, index =>
                    {
                        if (!_encoders.TryTake(out var encoder))
                        {
                            encoder = new GroupEncoder(content);
                        }

                        try
                        {
                            results[index] = encoder.Encode(plan, groups[first + index]);
                        }
                        finally
                        {
                            _encoders.Add(encoder);
                        }
                    });
                }
                catch (AggregateException aggregate)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var inner = aggregate.Flatten().InnerExceptions;
                    ExceptionDispatchInfo.Capture(inner.FirstOrDefault(e => e is not OperationCanceledException) ?? inner[0]).Throw();
                }

                for (var index = 0; index < count; index++)
                {
                    write(groups[first + index], results[index]);
                    results[index] = default;
                }
            }
        }

        public void Dispose()
        {
            while (_encoders.TryTake(out var encoder))
            {
                encoder.Dispose();
            }
        }
    }

    private static IEnumerable<(string[] Parts, ulong Offset, ulong Size)> TreeEntries(Plan plan) =>
        plan.Files.Select(file => (file.Parts, (ulong)file.DataOffset, (ulong)file.File.Size));

    private static long GroupBytes(Plan plan, int group) =>
        Math.Min(ZArchiveWriter.RecordSpan, plan.DataSize - (long)group * ZArchiveWriter.RecordSpan);

    // ---- direct mode ---------------------------------------------------------------------------

    private static void ConvertDirect(string package, string destination, PkgZarOptions options, IProgress<PkgExtractProgress>? progress, CancellationToken cancellation)
    {
        if (File.Exists(JournalPath(destination)))
        {
            throw new IOException($"{JournalPath(destination)} records an unfinished consuming conversion; run it again with the source consumed to resume it.");
        }

        var source = new PackageSource(package);
        using var content = (IDisposable)OpenContent(source);
        var packageContent = (IPackageContent)content;
        var plan = BuildPlan(packageContent, options.WindowBytes);
        // Both are compressed, so the archive comes out near the package's size, never far above
        // the files it holds.
        var estimate = Math.Min(plan.DataSize, source.Length);
        if (options.AvailableSpace(destination) is { } available && available < estimate)
        {
            throw new IOException(NotEnoughSpace(estimate, available));
        }

        var partial = PartialPath(destination);
        try
        {
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var pipeline = new GroupPipeline(packageContent, options.MaxParallelism))
            {
                var records = new (ulong BaseOffset, ushort[] Sizes)[plan.GroupCount];
                long done = 0;
                var tracker = new ProgressStep(progress, plan.DataSize);
                tracker.Report(0);
                pipeline.Run(plan, Enumerable.Range(0, plan.GroupCount).ToArray(), (group, encoded) =>
                {
                    records[group] = ((ulong)output.Position, encoded.Sizes);
                    output.Write(encoded.Data, 0, encoded.Length);
                    done += GroupBytes(plan, group);
                    tracker.Report(done);
                }, cancellation);
                ZArchiveWriter.WriteTrailer(output, (ulong)output.Position, records, TreeEntries(plan));
                output.Flush(flushToDisk: true);
                tracker.Report(plan.DataSize, force: true);
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch (Exception exception)
        {
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
            }

            if (exception is IOException io && IsOutOfSpace(io))
            {
                throw new IOException("Out of disk space while writing the archive; the partial archive was removed and the package is untouched.", exception);
            }

            throw;
        }
    }

    // ---- consume mode --------------------------------------------------------------------------

    private static void ConvertConsuming(string package, string destination, PkgZarOptions options, IProgress<PkgExtractProgress>? progress, CancellationToken cancellation)
    {
        try
        {
            RunConsuming(package, destination, options, progress, cancellation);
        }
        catch (IOException exception) when (IsOutOfSpace(exception) && exception is not PkgConversionPausedException)
        {
            throw new PkgConversionPausedException(
                "Out of disk space: the conversion is paused and nothing is lost. Free some space and run the same conversion again to resume.",
                exception);
        }
    }

    private static void RunConsuming(string package, string destination, PkgZarOptions options, IProgress<PkgExtractProgress>? progress, CancellationToken cancellation)
    {
        var journalPath = JournalPath(destination);
        var partialPath = PartialPath(destination);
        PackageSource source;
        Journal journal;
        var committed = new List<Journal.Batch>();
        if (File.Exists(journalPath))
        {
            var state = Journal.Read(journalPath);
            // Released again below unless the conversion takes it over.
            using var pending = new JournalLease(state.Journal);
            if (!string.Equals(state.Header.PackagePath, package, PathComparison))
            {
                throw new InvalidDataException(
                    $"{journalPath} belongs to a conversion of {state.Header.PackagePath}, not {package}. Finish that conversion or remove the journal.");
            }

            if (state.FinishedLength is { } recordedLength)
            {
                pending.Dispose();
                FinishCommitted(package, destination, journalPath, partialPath, recordedLength);
                return;
            }

            if (!File.Exists(package))
            {
                throw new InvalidDataException($"{journalPath} records an unfinished conversion, but the package {package} is gone.");
            }

            var length = new FileInfo(package).Length;
            var frontier = state.Batches.Count > 0 ? state.Batches[^1].Frontier : state.Header.OriginalLength;
            if (length > state.Header.OriginalLength || length < frontier)
            {
                throw new InvalidDataException(
                    $"The package is {length:N0} bytes, but the journal recorded {state.Header.OriginalLength:N0} bytes cut to {frontier:N0}: it is not the package this conversion started from.");
            }

            if (!SnapshotMatchesFile(package, state.Header.Snapshot, length))
            {
                throw new InvalidDataException($"The package bytes differ from those recorded in {journalPath}: it is not the package this conversion started from.");
            }

            source = PackageSource.OpenTruncated(package, state.Header.OriginalLength, state.Header.Snapshot, frontier);

            committed.AddRange(state.Batches);
            journal = pending.Take();
            Log.Info($"Resuming the conversion of {package} after {committed.Count} committed batches.");
        }
        else
        {
            source = new PackageSource(package);
            source.StartRecording();
            journal = null!;
        }

        source.FileReadObserver = options.SourceReadObserver is { } observer ? (offset, count) => observer(offset, count, source.Frontier) : null;
        using var content = (IDisposable)OpenContent(source);
        var packageContent = (IPackageContent)content;
        long finishedLength;
        Plan plan;
        List<int[]> batches;
        try
        {
            plan = BuildPlan(packageContent, options.WindowBytes);
            batches = BuildBatches(plan, options.WindowBytes);
        }
        catch
        {
            journal?.Dispose();
            throw;
        }

        try
        {
            if (journal is null)
            {
                var snapshot = source.TakeSnapshot();
                var snapshotBytes = snapshot.Sum(range => (long)range.Bytes.Length);
                var needed = Math.Min(options.WindowBytes, plan.DataSize) + snapshotBytes + (64L << 20);
                if (options.AvailableSpace(destination) is { } available && available < needed)
                {
                    throw new IOException(NotEnoughSpace(needed, available));
                }

                journal = Journal.Create(journalPath, new Journal.HeaderData(package, source.Length, options.WindowBytes, plan.GroupCount, plan.Fingerprint, snapshot));
                options.Observer?.Invoke(PkgZarStage.JournalCreated, -1, source.Frontier);
            }
            else
            {
                CheckResumable(journal, plan, batches, committed, options.WindowBytes);
            }

            finishedLength = ConsumeBatches(package, partialPath, source, packageContent, plan, batches, journal, committed, options, progress, cancellation);
        }
        finally
        {
            journal?.Dispose();
        }

        // Every handle on the package and the partial archive is closed before they move.
        content.Dispose();
        FinishCommitted(package, destination, journalPath, partialPath, finishedLength);
        options.Observer?.Invoke(PkgZarStage.Finished, batches.Count, 0);
    }

    // Disposes a journal on an early exit unless Take hands it on.
    private sealed class JournalLease(Journal journal) : IDisposable
    {
        private Journal? _journal = journal;

        public Journal Take()
        {
            var taken = _journal!;
            _journal = null;
            return taken;
        }

        public void Dispose() => _journal?.Dispose();
    }

    private static void CheckResumable(Journal journal, Plan plan, List<int[]> batches, List<Journal.Batch> committed, long windowBytes)
    {
        var header = journal.Header;
        if (header.WindowBytes != windowBytes || header.GroupCount != plan.GroupCount || !header.Fingerprint.AsSpan().SequenceEqual(plan.Fingerprint))
        {
            throw new InvalidDataException("The package no longer yields the plan recorded in the journal; refusing to resume.");
        }

        if (committed.Count > batches.Count)
        {
            throw new InvalidDataException("The journal records more batches than the plan has.");
        }

        long end = 0;
        for (var index = 0; index < committed.Count; index++)
        {
            var batch = committed[index];
            if (batch.Index != index || !batch.Groups.Select(group => group.Group).SequenceEqual(batches[index]) || batch.ZarStart != end)
            {
                throw new InvalidDataException($"Journal batch {index} does not match the plan; refusing to resume.");
            }

            end = batch.ZarEnd;
        }
    }

    // Returns the finished archive's length.
    private static long ConsumeBatches(
        string package,
        string partialPath,
        PackageSource source,
        IPackageContent content,
        Plan plan,
        List<int[]> batches,
        Journal journal,
        List<Journal.Batch> committed,
        PkgZarOptions options,
        IProgress<PkgExtractProgress>? progress,
        CancellationToken cancellation)
    {
        var records = new (ulong BaseOffset, ushort[] Sizes)[plan.GroupCount];
        long done = 0;
        foreach (var batch in committed)
        {
            foreach (var placement in batch.Groups)
            {
                records[placement.Group] = ((ulong)placement.ZarOffset, placement.Sizes);
                done += GroupBytes(plan, placement.Group);
            }
        }

        var committedEnd = committed.Count > 0 ? committed[^1].ZarEnd : 0;
        using var output = new FileStream(partialPath, committed.Count > 0 ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        if (output.Length < committedEnd)
        {
            throw new InvalidDataException($"{partialPath} is {output.Length:N0} bytes, shorter than the {committedEnd:N0} bytes the journal committed.");
        }

        // Anything past the last commit belongs to a batch that never finished; it is redone.
        output.SetLength(committedEnd);
        output.Position = committedEnd;
        var tracker = new ProgressStep(progress, plan.DataSize);
        tracker.Report(done);
        using (var cutter = File.OpenHandle(package, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var packageLength = RandomAccess.GetLength(cutter);
            void Cut(long frontier)
            {
                source.Frontier = Math.Min(source.Frontier, frontier);
                if (frontier < packageLength)
                {
                    RandomAccess.SetLength(cutter, frontier);
                    packageLength = frontier;
                }
            }

            // A crash between a commit and its truncation leaves the package longer than committed.
            if (committed.Count > 0)
            {
                Cut(committed[^1].Frontier);
            }

            using var pipeline = new GroupPipeline(content, options.MaxParallelism);
            for (var index = committed.Count; index < batches.Count; index++)
            {
                var groups = batches[index];
                var start = output.Position;
                var placements = new List<Journal.Placement>(groups.Length);
                pipeline.Run(plan, groups, (group, encoded) =>
                {
                    placements.Add(new Journal.Placement(group, output.Position, encoded.Sizes));
                    output.Write(encoded.Data, 0, encoded.Length);
                }, cancellation);
                options.Observer?.Invoke(PkgZarStage.BatchWritten, index, packageLength);
                output.Flush(flushToDisk: true);
                var frontier = index + 1 < batches.Count ? plan.GroupEnds[batches[index + 1][0]] : 0;
                journal.Append(new Journal.Batch(index, start, output.Position, frontier, placements));
                foreach (var placement in placements)
                {
                    records[placement.Group] = ((ulong)placement.ZarOffset, placement.Sizes);
                    done += GroupBytes(plan, placement.Group);
                }

                options.Observer?.Invoke(PkgZarStage.BatchCommitted, index, packageLength);
                Cut(frontier);
                options.Observer?.Invoke(PkgZarStage.BatchTruncated, index, packageLength);
                tracker.Report(done);
            }
        }

        options.Observer?.Invoke(PkgZarStage.DataComplete, batches.Count, source.Frontier);
        ZArchiveWriter.WriteTrailer(output, (ulong)output.Position, records, TreeEntries(plan));
        output.Flush(flushToDisk: true);
        options.Observer?.Invoke(PkgZarStage.TrailerWritten, batches.Count, source.Frontier);
        journal.AppendFinished(output.Length);
        options.Observer?.Invoke(PkgZarStage.JournalFinished, batches.Count, source.Frontier);
        tracker.Report(plan.DataSize, force: true);
        return output.Length;
    }

    // The archive is complete on disk and the journal says so: move it into place, then drop the
    // package and the journal. Each step can be repeated after a crash.
    private static void FinishCommitted(string package, string destination, string journalPath, string partialPath, long finishedLength)
    {
        if (File.Exists(partialPath))
        {
            var length = new FileInfo(partialPath).Length;
            if (length != finishedLength)
            {
                throw new InvalidDataException($"{partialPath} is {length:N0} bytes, but the journal finished it at {finishedLength:N0}.");
            }

            File.Move(partialPath, destination, overwrite: true);
        }
        else if (!File.Exists(destination))
        {
            throw new InvalidDataException($"The journal says {destination} was finished, but neither it nor {partialPath} exists.");
        }

        File.Delete(package);
        File.Delete(journalPath);
    }

    private static bool SnapshotMatchesFile(string package, IReadOnlyList<(long Offset, byte[] Bytes)> snapshot, long length)
    {
        using var handle = File.OpenHandle(package, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (var (offset, bytes) in snapshot)
        {
            var count = (int)Math.Clamp(length - offset, 0, bytes.Length);
            if (count == 0)
            {
                continue;
            }

            var actual = new byte[count];
            var done = 0;
            while (done < count)
            {
                var read = RandomAccess.Read(handle, actual.AsSpan(done), offset + done);
                if (read == 0)
                {
                    return false;
                }

                done += read;
            }

            if (!actual.AsSpan().SequenceEqual(bytes.AsSpan(0, count)))
            {
                return false;
            }
        }

        return true;
    }

    // ENOSPC/EDQUOT on Unix (the errno becomes the HResult), ERROR_DISK_FULL/ERROR_HANDLE_DISK_FULL on Windows.
    internal static bool IsOutOfSpace(IOException exception) =>
        exception.HResult is 28 or 69 or 122 or unchecked((int)0x80070070) or unchecked((int)0x80070027);

    private static string NotEnoughSpace(long needed, long available) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Not enough free disk space: need {needed / (1024.0 * 1024 * 1024):0.00} GB, {available / (1024.0 * 1024 * 1024):0.00} GB free.");

    // Reports at most every 16 MiB of archive data, never going backwards.
    private sealed class ProgressStep(IProgress<PkgExtractProgress>? progress, long total)
    {
        private long _reported = -1;

        public void Report(long done, bool force = false)
        {
            if (progress is null || (!force && _reported >= 0 && done - _reported < PkgPackage.ProgressStepBytes))
            {
                return;
            }

            _reported = done;
            progress.Report(new PkgExtractProgress(done, total, string.Empty));
        }
    }

    // ---- journal -------------------------------------------------------------------------------

    // <dest>.journal: a header written once (through a temporary file, so it is whole or absent),
    // then one record per committed batch and a final record once the archive is complete. Every
    // part carries a SHA-256; a record cut short by a crash is the uncommitted tail and is dropped.
    internal sealed class Journal : IDisposable
    {
        private const uint Magic = 0x4A5A4553; // "SEZJ"
        private const uint Version = 1;
        private const uint BatchKind = 1;
        private const uint FinishedKind = 2;
        private const int HashBytes = 32;

        private readonly FileStream _stream;

        private Journal(FileStream stream, HeaderData header)
        {
            _stream = stream;
            Header = header;
        }

        public HeaderData Header { get; }

        public sealed record HeaderData(
            string PackagePath,
            long OriginalLength,
            long WindowBytes,
            int GroupCount,
            byte[] Fingerprint,
            IReadOnlyList<(long Offset, byte[] Bytes)> Snapshot);

        public readonly record struct Placement(int Group, long ZarOffset, ushort[] Sizes);

        public sealed record Batch(int Index, long ZarStart, long ZarEnd, long Frontier, IReadOnlyList<Placement> Groups);

        public sealed record State(Journal Journal, HeaderData Header, List<Batch> Batches, long? FinishedLength);

        public static Journal Create(string path, HeaderData header)
        {
            var body = new MemoryStream();
            using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(header.PackagePath);
                writer.Write(header.OriginalLength);
                writer.Write(header.WindowBytes);
                writer.Write(header.GroupCount);
                writer.Write(header.Fingerprint);
                writer.Write(header.Snapshot.Count);
                foreach (var (offset, bytes) in header.Snapshot)
                {
                    writer.Write(offset);
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                }
            }

            body.Write(SHA256.HashData(body.GetBuffer().AsSpan(0, (int)body.Length)));
            var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                body.Position = 0;
                body.CopyTo(file);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            stream.Position = stream.Length;
            return new Journal(stream, header);
        }

        public static string ReadPackagePath(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != Version)
            {
                throw new InvalidDataException($"{path} is not a conversion journal this version understands.");
            }

            return reader.ReadString();
        }

        public static State Read(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
                HeaderData header;
                try
                {
                    if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != Version)
                    {
                        throw new InvalidDataException($"{path} is not a conversion journal this version understands.");
                    }

                    var packagePath = reader.ReadString();
                    var originalLength = reader.ReadInt64();
                    var window = reader.ReadInt64();
                    var groupCount = reader.ReadInt32();
                    var fingerprint = reader.ReadBytes(HashBytes);
                    var count = reader.ReadInt32();
                    if (count < 0)
                    {
                        throw new InvalidDataException($"{path} has a damaged header.");
                    }

                    var snapshot = new List<(long Offset, byte[] Bytes)>(count);
                    for (var index = 0; index < count; index++)
                    {
                        var offset = reader.ReadInt64();
                        var length = reader.ReadInt32();
                        snapshot.Add((offset, reader.ReadBytes(length)));
                    }

                    header = new HeaderData(packagePath, originalLength, window, groupCount, fingerprint, snapshot);
                }
                catch (EndOfStreamException)
                {
                    throw new InvalidDataException($"{path} has a truncated header.");
                }

                var headerEnd = (int)reader.BaseStream.Position;
                if (headerEnd + HashBytes > bytes.Length ||
                    !SHA256.HashData(bytes.AsSpan(0, headerEnd)).AsSpan().SequenceEqual(bytes.AsSpan(headerEnd, HashBytes)))
                {
                    throw new InvalidDataException($"{path} has a damaged header; refusing to guess which bytes of the package are gone.");
                }

                var batches = new List<Batch>();
                long? finished = null;
                var position = headerEnd + HashBytes;
                while (position < bytes.Length)
                {
                    if (bytes.Length - position < 8)
                    {
                        break;
                    }

                    var kind = BitConverter.ToUInt32(bytes, position);
                    var length = BitConverter.ToUInt32(bytes, position + 4);
                    if (length > bytes.Length - position - 8 - HashBytes)
                    {
                        break;
                    }

                    var end = position + 8 + (int)length;
                    if (!SHA256.HashData(bytes.AsSpan(position, 8 + (int)length)).AsSpan().SequenceEqual(bytes.AsSpan(end, HashBytes)))
                    {
                        if (end + HashBytes == bytes.Length)
                        {
                            break;
                        }

                        throw new InvalidDataException($"{path} has a damaged record at 0x{position:X}; refusing to resume.");
                    }

                    if (finished is not null)
                    {
                        throw new InvalidDataException($"{path} has records after its finished record.");
                    }

                    var payload = new BinaryReader(new MemoryStream(bytes, position + 8, (int)length));
                    if (kind == BatchKind)
                    {
                        var index = payload.ReadInt32();
                        var start = payload.ReadInt64();
                        var stop = payload.ReadInt64();
                        var frontier = payload.ReadInt64();
                        var count = payload.ReadInt32();
                        var placements = new List<Placement>(count);
                        for (var group = 0; group < count; group++)
                        {
                            var number = payload.ReadInt32();
                            var offset = payload.ReadInt64();
                            var sizes = new ushort[ZArchiveWriter.BlocksPerRecord];
                            for (var block = 0; block < sizes.Length; block++)
                            {
                                sizes[block] = payload.ReadUInt16();
                            }

                            placements.Add(new Placement(number, offset, sizes));
                        }

                        batches.Add(new Batch(index, start, stop, frontier, placements));
                    }
                    else if (kind == FinishedKind)
                    {
                        finished = payload.ReadInt64();
                    }
                    else
                    {
                        throw new InvalidDataException($"{path} has an unknown record kind {kind}.");
                    }

                    position = end + HashBytes;
                }

                if (position < bytes.Length)
                {
                    Log.Warn($"{path}: dropping {bytes.Length - position} bytes of a record a crash cut short (its batch is redone).");
                    stream.SetLength(position);
                }

                stream.Position = position;
                var journal = new Journal(stream, header);
                return new State(journal, header, batches, finished);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void Append(Batch batch)
        {
            var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(batch.Index);
                writer.Write(batch.ZarStart);
                writer.Write(batch.ZarEnd);
                writer.Write(batch.Frontier);
                writer.Write(batch.Groups.Count);
                foreach (var placement in batch.Groups)
                {
                    writer.Write(placement.Group);
                    writer.Write(placement.ZarOffset);
                    foreach (var size in placement.Sizes)
                    {
                        writer.Write(size);
                    }
                }
            }

            AppendRecord(BatchKind, payload.ToArray());
        }

        public void AppendFinished(long zarLength) => AppendRecord(FinishedKind, BitConverter.GetBytes(zarLength));

        private void AppendRecord(uint kind, byte[] payload)
        {
            var record = new byte[8 + payload.Length + HashBytes];
            BitConverter.TryWriteBytes(record.AsSpan(0, 4), kind);
            BitConverter.TryWriteBytes(record.AsSpan(4, 4), (uint)payload.Length);
            payload.CopyTo(record, 8);
            SHA256.HashData(record.AsSpan(0, 8 + payload.Length), record.AsSpan(8 + payload.Length));
            _stream.Write(record);
            _stream.Flush(flushToDisk: true);
        }

        public void Dispose() => _stream.Dispose();
    }
}

internal enum PkgZarStage
{
    JournalCreated,
    BatchWritten,
    BatchCommitted,
    BatchTruncated,
    DataComplete,
    TrailerWritten,
    JournalFinished,
    Finished,
}

internal sealed class PkgZarOptions
{
    public bool ConsumeSource { get; init; }

    // Package bytes one consume batch spans (and archive bytes it writes) before the package is cut.
    public long WindowBytes { get; init; } = PkgZarConverter.DefaultWindowBytes;

    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    // Free bytes for the destination, or null when unknown (no check).
    public Func<string, long?> AvailableSpace { get; init; } = PkgPackage.GetAvailableSpace;

    // (stage, batch index, package length) at each step of a consuming conversion; throwing from it
    // simulates a crash at that point.
    public Action<PkgZarStage, int, long>? Observer { get; init; }

    // (offset, length, frontier) of every read that goes to the package file.
    public Action<long, long, long>? SourceReadObserver { get; init; }
}
