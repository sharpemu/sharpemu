// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// What s_barrier publishes is whatever the guest waited for before it.
public sealed class Gen5BarrierMemoryScopeTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint Barrier = 0xBF8A0000;
    private const uint EndProgram = 0xBF810000;
    private const uint WaitLgkmOnly = 0xBF8CC07F;   // s_waitcnt lgkmcnt(0)
    private const uint WaitVectorMemory = 0xBF8C0F70; // s_waitcnt vmcnt(0)

    private const uint AcquireRelease = 0x8;
    private const uint UniformMemory = 0x40;
    private const uint WorkgroupMemory = 0x100;
    private const uint ImageMemory = 0x800;

    [Theory]
    [InlineData(WaitLgkmOnly, AcquireRelease | WorkgroupMemory)]
    [InlineData(WaitVectorMemory, AcquireRelease | UniformMemory | WorkgroupMemory | ImageMemory)]
    public void BarrierPublishesWhatTheWaitCountWaitedFor(uint wait, uint expected)
    {
        var program = Decode([wait, Barrier, EndProgram]);
        Assert.Equal("SBarrier", program.Instructions[1].Opcode);

        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(expected, ControlBarrierSemantics(shader.Spirv));
    }

    private const uint WaitStoresZeroSopk = 0xBBFD0000; // s_waitcnt_vscnt null, 0
    private const uint WaitLgkmZeroSopk = 0xBD7D0000;   // s_waitcnt_lgkmcnt null, 0

    // GFX10 tracks stores with vscnt, and a drained counter stays drained for the block.
    [Theory]
    [InlineData(new[] { WaitStoresZeroSopk, WaitLgkmOnly }, AcquireRelease | UniformMemory | WorkgroupMemory | ImageMemory)]
    [InlineData(new[] { WaitVectorMemory, WaitLgkmOnly }, AcquireRelease | UniformMemory | WorkgroupMemory | ImageMemory)]
    [InlineData(new[] { WaitLgkmZeroSopk }, AcquireRelease | WorkgroupMemory)]
    public void StoreWaitsCountAndStayCountedAcrossLaterWaits(uint[] waits, uint expected)
    {
        var program = Decode([.. waits, Barrier, EndProgram]);
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(expected, ControlBarrierSemantics(shader.Spirv));
    }

    // A barrier with no wait before it in its block keeps the conservative semantics: the
    // publish may have happened in a block the translator cannot order against this one.
    [Fact]
    public void BarrierWithoutAWaitCountKeepsDeviceMemoryOrdering()
    {
        var program = Decode([Barrier, EndProgram]);
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(
            AcquireRelease | UniformMemory | WorkgroupMemory | ImageMemory,
            ControlBarrierSemantics(shader.Spirv));
    }

    // The value of the memory-semantics operand of the module's single OpControlBarrier.
    private static uint ControlBarrierSemantics(byte[] spirv)
    {
        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var constants = new Dictionary<uint, uint>();
        uint? semanticsId = null;
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = (int)(words[offset] >> 16);
            var opcode = (ushort)(words[offset] & 0xFFFF);
            if (opcode == (ushort)SpirvOp.Constant && wordCount == 4)
            {
                constants[words[offset + 2]] = words[offset + 3];
            }
            else if (opcode == (ushort)SpirvOp.ControlBarrier)
            {
                Assert.Null(semanticsId);
                semanticsId = words[offset + 3];
            }

            offset += wordCount;
        }

        Assert.NotNull(semanticsId);
        return constants[semanticsId.Value];
    }

    private static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        return program;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < baseAddress || address - baseAddress > int.MaxValue)
            {
                return false;
            }

            offset = (int)(address - baseAddress);
            return offset <= _storage.Length - length;
        }
    }
}
