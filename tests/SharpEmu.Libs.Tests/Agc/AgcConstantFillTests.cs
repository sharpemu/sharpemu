// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Tests.Kernel;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AgcConstantFillTests
{
    [Theory]
    [InlineData(1024UL, "eligible", "records=64")]
    [InlineData(512UL, "eligible", "records=32")]
    [InlineData(15UL, "destination-size-short:15", "")]
    [InlineData(0UL, "destination-size-short:0", "")]
    [InlineData(0x1000000000UL, "eligible", "records=64")]
    public void FillUsesGuestExtentWithoutSnapshot(ulong size, string reason, string records)
    {
        const ulong address = 0x10000;
        uint[] scalars = [(uint)address, 16u << 16, 64, (75u << 12) | 4, 0, 0x3C000000, 0, 0x3C000000];
        var binding = new Gen5GlobalMemoryBinding(0, address, [20], [], 0, false, size)
        {
            Writable = true,
            WriteBackToGuest = true,
        };
        var evaluation = new Gen5ShaderEvaluation(scalars, scalars, [], [binding], new(8, null, null, null));
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, default, "VLshlAddU32", [], [S(8), new(Gen5OperandKind.EncodedConstant, 134), V(0)], [V(4)], null),
        };
        for (uint i = 0; i < 4; i++)
        {
            instructions.Add(new((i + 1) * 4, default, "VMovB32", [], [S(i + 4)], [V(i)], null));
        }
        instructions.Add(new(20, default, "BufferStoreFormatXyzw", [], [], [], new Gen5BufferMemoryControl(4, 4, 0, 0, 0, true, false, false, false)));
        instructions.Add(new(24, default, "SEndpgm", [], [], [], null));
        var program = new Gen5ShaderProgram(0x20000, instructions);
        var dispatchType = typeof(AgcExports).GetNestedType("ComputeDispatch", BindingFlags.NonPublic)!;
        var dispatch = Activator.CreateInstance(dispatchType, 1u, 1u, 1u, 0u, 0u, 0u, 64u, false, 64u, 1u, 1u)!;
        var diagnostic = typeof(AgcExports).GetMethod("GetConstantFillDiagnosticReason", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(reason, diagnostic.Invoke(null, [program, evaluation, dispatch, 64u, 1u, 1u]));

        var memory = new FakeCpuMemory(address, 1024);
        var context = new CpuContext(memory, Generation.Gen5);
        object?[] args = [context, program, evaluation, dispatch, 64u, 1u, 1u, 0L, ""];
        var submit = typeof(AgcExports).GetMethod("TrySubmitConstantFillKernel", BindingFlags.Static | BindingFlags.NonPublic)!;
        // The replacement runs in stream order on the caller: an eligible shape writes the pattern now.
        var applied = (bool)submit.Invoke(null, args)!;
        if (reason == "eligible")
        {
            Assert.True(applied);
            Assert.Contains(records, (string)args[8]!);
            Assert.Contains("pattern=0x3C000000000000003C00000000000000", (string)args[8]!);
            Span<byte> record = stackalloc byte[16];
            Assert.True(memory.TryRead(address, record));
            Assert.Equal(0x3C000000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
            Assert.Equal(0x3C000000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record[12..]));
        }
        else
        {
            Assert.False(applied);
            Assert.Equal("", args[8]);
        }
    }

    private static Gen5Operand S(uint index) => new(Gen5OperandKind.ScalarRegister, index);
    private static Gen5Operand V(uint index) => new(Gen5OperandKind.VectorRegister, index);
}
