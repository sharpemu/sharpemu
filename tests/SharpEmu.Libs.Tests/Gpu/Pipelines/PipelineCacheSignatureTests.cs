// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A saved driver cache loads only when its signature line and payload hash match.
public sealed class PipelineCacheSignatureTests
{
    private static readonly byte[] Uuid = Enumerable.Range(0, PipelineCacheSignature.UuidSize).Select(index => (byte)(index * 17)).ToArray();

    private static string Signature(uint driverVersion = 0x00401000) => PipelineCacheSignature.Build(0x1002, 0x73BF, driverVersion, Uuid);

    [Fact]
    public void Build_NamesTheBuildAndTheDevice()
    {
        var signature = Signature();

        Assert.StartsWith($"SharpEmuPC1:{PipelineCacheSignature.BuildVersion}:00001002:000073bf:00401000:", signature);
        Assert.EndsWith("\n", signature);
        Assert.Contains(Convert.ToHexString(Uuid).ToLowerInvariant(), signature);
    }

    [Fact]
    public void ValidFile_Loads()
    {
        var payload = Encoding.ASCII.GetBytes("driver cache bytes");

        var file = PipelineCacheSignature.Wrap(Signature(), payload);

        Assert.True(PipelineCacheSignature.TryUnwrap(Signature(), file, out var loaded));
        Assert.Equal(payload, loaded);
    }

    [Fact]
    public void SignatureMismatch_Invalidates()
    {
        var file = PipelineCacheSignature.Wrap(Signature(), [1, 2, 3, 4]);

        Assert.False(PipelineCacheSignature.TryUnwrap(Signature(driverVersion: 0x00402000), file, out var loaded));
        Assert.Empty(loaded);
    }

    [Fact]
    public void HashMismatch_Invalidates()
    {
        var file = PipelineCacheSignature.Wrap(Signature(), [1, 2, 3, 4]);
        file[^1] ^= 0xFF;

        Assert.False(PipelineCacheSignature.TryUnwrap(Signature(), file, out _));
    }

    [Fact]
    public void ShortOrEmptyFile_Invalidates()
    {
        Assert.False(PipelineCacheSignature.TryUnwrap(Signature(), [], out _));
        Assert.False(PipelineCacheSignature.TryUnwrap(Signature(), Encoding.ASCII.GetBytes(Signature()), out _));
    }

    [Fact]
    public void EmptyPayload_RoundTrips()
    {
        var file = PipelineCacheSignature.Wrap(Signature(), []);

        Assert.True(PipelineCacheSignature.TryUnwrap(Signature(), file, out var loaded));
        Assert.Empty(loaded);
    }
}
