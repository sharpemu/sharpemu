// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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

    // A cached function is decoded again only when a word its decode read has changed. Resident code compares in bulk;
    // code the host cannot copy is compared a word at a time.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FunctionCache_DecodesAgainOnlyWhenTheCodeChanges(bool resident)
    {
        // S_NOP 0; S_MOV_B32 s0, literal; the literal; S_SETPC_B64.
        var code = new Dictionary<ulong, uint> { [0x10000] = 0xBF800000, [0x10004] = 0xBE8003FF, [0x10008] = 0x12345678, [0x1000C] = 0xBE80200E };
        var reads = 0;
        bool Read(ulong address, out uint word)
        {
            reads++;
            return code.TryGetValue(address, out word);
        }

        bool Resident(ulong address, Span<byte> destination, bool clean)
        {
            if (!resident) return false;
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!code.TryGetValue(address + (ulong)offset, out var word)) return false;
                BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), word);
            }

            return true;
        }

        var cache = new ShaderFunctionCache(Resident);
        var first = cache.Read(0x10000, Read);
        Assert.True(first.Complete, first.Error);
        Assert.Equal(0x10000ul, Assert.Single(first.Runs).Address);
        reads = 0;
        Assert.Same(first, cache.Read(0x10000, Read));
        Assert.Equal(resident ? 0 : 4, reads);

        code[0x10008] = 0x87654321;
        var changed = cache.Read(0x10000, Read);
        Assert.NotSame(first, changed);
        Assert.Equal(0x87654321u, changed.Program.Instructions[1].Words[1]);
        Assert.Same(changed, cache.Read(0x10000, Read));
    }

    // A function whose read fails is not kept, so the next lookup reads it again.
    [Fact]
    public void FunctionCache_KeepsNoIncompleteFunction()
    {
        var readable = false;
        var cache = new ShaderFunctionCache(null);
        bool Read(ulong address, out uint word)
        {
            word = 0xBE80200E;
            return readable;
        }

        Assert.False(cache.Read(0x10000, Read).Complete);
        readable = true;
        Assert.True(cache.Read(0x10000, Read).Complete);
    }
}
