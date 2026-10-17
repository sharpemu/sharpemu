// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// The read-ahead hint is advisory, so it cannot change what a read returns -
// only how long it waits. These pin that: a hint never alters or truncates the
// bytes a positional read produces, and an out-of-range or stale hint is
// absorbed rather than thrown at the caller.
// Runs in the kernel-state collection: these cases are self-contained, but the
// AMPR streaming tests they would otherwise run beside depend on process-wide
// registry state that only serialization keeps stable.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class HostReadAheadTests : IDisposable
{
    private readonly string _path;

    public HostReadAheadTests()
    {
        _path = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-readahead-{Guid.NewGuid():N}.bin");
        var payload = new byte[256 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = unchecked((byte)i);
        }

        File.WriteAllBytes(_path, payload);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void Advise_LeavesTheFollowingReadsBytesUnchanged()
    {
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);
        var first = new byte[64 * 1024];
        Assert.Equal(first.Length, RandomAccess.Read(handle, first, 0));

        HostReadAhead.Advise(handle, first.Length, HostReadAhead.WindowBytes);

        var second = new byte[64 * 1024];
        Assert.Equal(second.Length, RandomAccess.Read(handle, second, first.Length));
        for (var i = 0; i < second.Length; i++)
        {
            Assert.Equal(unchecked((byte)(first.Length + i)), second[i]);
        }
    }

    [Theory]
    [InlineData(0, 0)]              // nothing to prefetch
    [InlineData(-1, 4096)]          // negative offset
    [InlineData(0, -4096)]          // negative count
    [InlineData(1L << 40, 4096)]    // far past the end of the file
    public void Advise_AbsorbsHintsItCannotServe(long offset, int count)
    {
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);

        HostReadAhead.Advise(handle, offset, count);

        // The file is still readable: a refused hint must not disturb the handle.
        var buffer = new byte[16];
        Assert.Equal(buffer.Length, RandomAccess.Read(handle, buffer, 0));
    }

    [Fact]
    public void Advise_IgnoresAClosedHandle()
    {
        var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);
        handle.Dispose();

        HostReadAhead.Advise(handle, 0, HostReadAhead.WindowBytes);
    }
}
