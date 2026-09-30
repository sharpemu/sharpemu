// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ShaderFunctionReaderTests
{
    [Fact]
    public void ReturnStopsCaptureBeforeTrailingWords()
    {
        var reads = 0;
        var result = ShaderFunctionReader.Read(0x10000, (ulong address, out uint word) =>
        {
            reads++;
            Assert.Equal(0x10000ul, address);
            word = 0xBE80200E;
            return true;
        });

        Assert.True(result.Complete, result.Error);
        Assert.Equal("SSetpcB64", Assert.Single(result.Program.Instructions).Opcode);
        Assert.Equal(new byte[] { 0x0E, 0x20, 0x80, 0xBE }, result.Bytes);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ReadFailureRetainsOnlyTheCapturedPrefix()
    {
        var reads = 0;
        var result = ShaderFunctionReader.Read(0x10000, (ulong address, out uint word) =>
        {
            reads++;
            word = 0xBF800000;
            return address == 0x10000;
        });

        Assert.False(result.Complete);
        Assert.Contains("read-failed", result.Error);
        Assert.Equal(new byte[] { 0, 0, 0x80, 0xBF }, result.Bytes);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void InvalidAddressDoesNotReadGuestMemory()
    {
        var result = ShaderFunctionReader.Read(1ul << 48,
            (ulong address, out uint word) => throw new InvalidOperationException("Unexpected guest read."));

        Assert.False(result.Complete);
        Assert.Empty(result.Bytes);
        Assert.Contains("invalid function address", result.Error);
    }
}
