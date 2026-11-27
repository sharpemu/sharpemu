// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32.SafeHandles;

namespace SharpEmu.HLE.FileSystem.Pkg;

// Positional reads over the package file; every read is range checked. Both package formats read
// through it, which lets a pkg to zar conversion that consumes its source:
//   - record every byte range the parser reads while opening the package (TakeSnapshot),
//   - serve those ranges from a snapshot once the file has been truncated below them,
//   - keep reporting the original length, so parsing a shrunk package sees the same file,
//   - refuse any read of file bytes at or past the frontier (the length the file was cut to).
// Reads may run on several threads; the snapshot is immutable once set.
internal sealed class PackageSource : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly object _gate = new();
    private List<(long Offset, long End)>? _recorded;
    // Sorted, non-overlapping.
    private (long Offset, byte[] Bytes)[] _snapshot = [];
    private long _frontier;

    public PackageSource(string path)
        : this(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess), null)
    {
    }

    private PackageSource(SafeFileHandle handle, long? originalLength)
    {
        _handle = handle;
        Length = originalLength ?? RandomAccess.GetLength(handle);
        _frontier = Length;
    }

    // The original package length: the file's own length, or the length recorded before it was cut.
    public long Length { get; }

    // File bytes at or past this offset are gone (or about to be); reading them is a bug.
    public long Frontier
    {
        get => Volatile.Read(ref _frontier);
        set => Volatile.Write(ref _frontier, value);
    }

    public IReadOnlyList<(long Offset, byte[] Bytes)> Snapshot => _snapshot;

    // Called with (offset, length) of every read that goes to the file, after the frontier check.
    public Action<long, long>? FileReadObserver { get; set; }

    // A package cut by a consuming conversion: `snapshot` serves the ranges the parser reads, the
    // file serves the rest below `frontier`, and Length stays `originalLength`.
    public static PackageSource OpenTruncated(string path, long originalLength, IReadOnlyList<(long Offset, byte[] Bytes)> snapshot, long frontier)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        var source = new PackageSource(handle, originalLength);
        source.SetSnapshot(snapshot);
        source.Frontier = Math.Min(frontier, RandomAccess.GetLength(handle));
        return source;
    }

    // Starts recording the ranges reads touch.
    public void StartRecording()
    {
        lock (_gate)
        {
            _recorded = [];
        }
    }

    // Stops recording and reads every recorded range (merged) from the file: the bytes a parse of
    // this package needs. The snapshot then serves those ranges from memory.
    public IReadOnlyList<(long Offset, byte[] Bytes)> TakeSnapshot()
    {
        List<(long Offset, long End)> ranges;
        lock (_gate)
        {
            ranges = _recorded ?? throw new InvalidOperationException("Recording was not started.");
            _recorded = null;
        }

        ranges.Sort((left, right) => left.Offset.CompareTo(right.Offset));
        var merged = new List<(long Offset, long End)>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Offset <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Offset, Math.Max(merged[^1].End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        var snapshot = new List<(long Offset, byte[] Bytes)>(merged.Count);
        foreach (var (offset, end) in merged)
        {
            var bytes = new byte[end - offset];
            ReadFile(bytes, offset);
            snapshot.Add((offset, bytes));
        }

        SetSnapshot(snapshot);
        return _snapshot;
    }

    private void SetSnapshot(IReadOnlyList<(long Offset, byte[] Bytes)> snapshot)
    {
        var sorted = snapshot.OrderBy(range => range.Offset).ToArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            if (sorted[i].Offset < 0 || sorted[i].Bytes.Length > Length - sorted[i].Offset ||
                (i > 0 && sorted[i - 1].Offset + sorted[i - 1].Bytes.Length > sorted[i].Offset))
            {
                throw new InvalidDataException("The package snapshot ranges overlap or lie outside the package.");
            }
        }

        _snapshot = sorted;
    }

    // Reads up to buffer.Length bytes at offset; short only at Length.
    public int ReadAt(Span<byte> buffer, long offset)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (offset >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, Length - offset);
        Record(offset, count);
        var snapshot = _snapshot;
        var position = offset;
        var end = offset + count;
        while (position < end)
        {
            var index = LastStartingAtOrBefore(snapshot, position);
            if (index >= 0 && position < snapshot[index].Offset + snapshot[index].Bytes.Length)
            {
                var (rangeOffset, bytes) = snapshot[index];
                var take = (int)(Math.Min(end, rangeOffset + bytes.Length) - position);
                bytes.AsSpan((int)(position - rangeOffset), take).CopyTo(buffer[(int)(position - offset)..]);
                position += take;
                continue;
            }

            var next = index + 1 < snapshot.Length ? Math.Min(end, snapshot[index + 1].Offset) : end;
            var frontier = Frontier;
            if (next > frontier)
            {
                throw new InvalidDataException(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Read of package bytes 0x{position:X}..0x{next:X} reaches past 0x{frontier:X}, where the package was already consumed."));
            }

            FileReadObserver?.Invoke(position, next - position);
            ReadFile(buffer.Slice((int)(position - offset), (int)(next - position)), position);
            position = next;
        }

        return count;
    }

    // The last snapshot range starting at or before `position`, or -1.
    private static int LastStartingAtOrBefore((long Offset, byte[] Bytes)[] snapshot, long position)
    {
        int low = 0, high = snapshot.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (snapshot[mid].Offset <= position)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low - 1;
    }

    private void Record(long offset, long count)
    {
        if (_recorded is null)
        {
            return;
        }

        lock (_gate)
        {
            _recorded?.Add((offset, offset + count));
        }
    }

    private void ReadFile(Span<byte> buffer, long offset)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = RandomAccess.Read(_handle, buffer[done..], offset + done);
            if (read == 0)
            {
                throw new InvalidDataException("The package file ended unexpectedly.");
            }

            done += read;
        }
    }

    public void ReadExactAt(Span<byte> buffer, long offset)
    {
        if (offset < 0 || offset > Length || buffer.Length > Length - offset)
        {
            throw new InvalidDataException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Read of {buffer.Length:N0} bytes at 0x{offset:X} is past the end of the {Length:N0}-byte package."));
        }

        ReadAt(buffer, offset);
    }

    public byte[] ReadBytes(long length, long offset)
    {
        if (length < 0 || length > int.MaxValue || offset < 0 || offset > Length || length > Length - offset)
        {
            throw new InvalidDataException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Read of {length:N0} bytes at 0x{offset:X} is past the end of the {Length:N0}-byte package."));
        }

        var bytes = new byte[length];
        ReadExactAt(bytes, offset);
        return bytes;
    }

    public void Dispose() => _handle.Dispose();
}

// A read-only, seekable Stream over a PackageSource for readers written against Stream (the PS4
// package). Each instance keeps its own position, so one per thread may share the source.
internal sealed class PackageSourceStream(PackageSource source) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => source.Length;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = source.ReadAt(buffer, _position);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => source.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
