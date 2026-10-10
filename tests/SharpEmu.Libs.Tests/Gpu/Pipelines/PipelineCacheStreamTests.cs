// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Large titles' driver caches pass 2 GiB, so they are written and read as streams from
// native memory. The file format stays the one Wrap and TryUnwrap use.
public sealed unsafe class PipelineCacheStreamTests
{
    private static readonly string Signature =
        PipelineCacheSignature.Build(0x10DE, 0x2C05, 0x0123, new byte[PipelineCacheSignature.UuidSize]);

    private static byte[] Payload() => Encoding.ASCII.GetBytes("driver cache bytes, streamed");

    [Fact]
    public void StreamedFile_RoundTrips()
    {
        var payload = Payload();
        using var stream = new MemoryStream();
        fixed (byte* data = payload)
            PipelineCacheSignature.WriteTo(stream, Signature, data, (ulong)payload.Length);

        stream.Position = 0;
        Assert.True(PipelineCacheSignature.TryReadFrom(stream, Signature, out var read, out var length));
        try
        {
            Assert.Equal(payload, new ReadOnlySpan<byte>(read, (int)length).ToArray());
        }
        finally
        {
            NativeMemory.Free(read);
        }
    }

    // Caches saved by earlier builds keep loading.
    [Fact]
    public void FileWrittenWithWrap_ReadsAsAStream()
    {
        var payload = Payload();
        using var stream = new MemoryStream(PipelineCacheSignature.Wrap(Signature, payload));
        Assert.True(PipelineCacheSignature.TryReadFrom(stream, Signature, out var read, out var length));
        try
        {
            Assert.Equal(payload, new ReadOnlySpan<byte>(read, (int)length).ToArray());
        }
        finally
        {
            NativeMemory.Free(read);
        }
    }

    [Fact]
    public void CorruptedPayload_IsRejected()
    {
        var file = PipelineCacheSignature.Wrap(Signature, Payload());
        file[^1] ^= 0xFF;
        using var stream = new MemoryStream(file);
        Assert.False(PipelineCacheSignature.TryReadFrom(stream, Signature, out _, out _));
    }

    [Fact]
    public void OtherDevice_IsRejected()
    {
        var other = PipelineCacheSignature.Build(0x1002, 0x73BF, 0x0123, new byte[PipelineCacheSignature.UuidSize]);
        using var stream = new MemoryStream(PipelineCacheSignature.Wrap(other, Payload()));
        Assert.False(PipelineCacheSignature.TryReadFrom(stream, Signature, out _, out _));
    }
}
