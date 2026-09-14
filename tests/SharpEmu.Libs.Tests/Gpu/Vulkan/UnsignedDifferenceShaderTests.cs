// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

[Collection(SchedulingStateCollection.Name)]
public sealed class UnsignedDifferenceShaderTests(HeadlessVulkanFixture fixture, ITestOutputHelper output)
    : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const ulong BufferAddress = 0x2_0000_0000;
    private const uint Sentinel = 0xCAFE_BABE;
    private const int RecordBytes = 16;

    [Fact]
    public void SumOfAbsoluteDifferences_MatchesUnsignedBoundaryResultsOnTheDevice()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        if (!vulkan.ShaderInt64)
        {
            Assert.False(ReferenceShaders.Required, "The translated shader test requires shaderInt64.");
            return;
        }

        uint[] operands = [0, 1, 0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFE, uint.MaxValue];
        uint[] accumulators = [0, 0x8000_0000, uint.MaxValue];
        var recordCount = operands.Length * operands.Length * accumulators.Length;
        var inputBytes = new byte[recordCount * RecordBytes];
        var expectedBytes = new byte[inputBytes.Length];
        var recordIndex = 0;
        foreach (var left in operands)
        foreach (var right in operands)
        foreach (var accumulator in accumulators)
        {
            var record = inputBytes.AsSpan(recordIndex * RecordBytes, RecordBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(record, left);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], right);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], accumulator);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], Sentinel);
            record.CopyTo(expectedBytes.AsSpan(recordIndex * RecordBytes));
            var expected = unchecked((uint)(Math.Abs((long)left - right) + accumulator));
            BinaryPrimitives.WriteUInt32LittleEndian(expectedBytes.AsSpan(recordIndex * RecordBytes + 12), expected);
            recordIndex++;
        }

        var shader = CompileBoundaryShader(inputBytes.Length, (uint)recordCount);
        using (var harness = new ImageTestHarness(vulkan))
        using (var runner = new TilerComputeRunner(harness))
        {
            var pipeline = runner.CreatePipeline(shader, []);
            var records = harness.Upload(inputBytes);
            var unusedArguments = harness.Upload(new byte[TileTransferArguments.Size]);
            // The shared runner's first three push words supply the compute thread limits.
            var limits = new TileTransferArguments { SourceBase = (uint)recordCount, DestinationBase = 1, Width = 1 };
            harness.Run(() => runner.Dispatch(pipeline, records, records, unusedArguments, 0, limits, 1, 1, 1));
            var actualBytes = harness.ReadBack(records.Handle, 0, (ulong)inputBytes.Length);
            Assert.Equal(expectedBytes, actualBytes);
            harness.AssertNoValidationMessages();
        }

        vulkan.AssertNoValidationMessages();
        output.WriteLine($"Verified {recordCount} unsigned boundary cases on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    private static byte[] CompileBoundaryShader(int bufferBytes, uint threadCount)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        uint[] words = [0xD15D_000F, 0x10Cu | (0x10Du << 9) | (0x10Eu << 18), 0xBF81_0000];
        var code = new byte[words.Length * sizeof(uint)];
        for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(wordIndex * sizeof(uint)), words[wordIndex]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, code));
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), ShaderAddress,
            out var decoded, out var decodeError), decodeError);
        var addition = decoded.Instructions[0];
        Assert.Equal("VSadU32", addition.Opcode);
        Assert.Equal([Gen5Operand.Vector(12), Gen5Operand.Vector(13), Gen5Operand.Vector(14)], addition.Sources);
        Assert.Equal([Gen5Operand.Vector(15)], addition.Destinations);

        var program = new Gen5ShaderProgram(ShaderAddress,
        [
            new(0, Gen5ShaderEncoding.Vop2, "VLshlrevB32", [0],
                [Gen5Operand.Source(132), Gen5Operand.Vector(0)], [Gen5Operand.Vector(10)], null),
            new(4, Gen5ShaderEncoding.Mubuf, "BufferLoadDwordx3", [0, 0], [], [],
                new Gen5BufferMemoryControl(3, 10, 12, 0, 0, false, true, false, false)),
            addition with { Pc = 12 },
            new(20, Gen5ShaderEncoding.Mubuf, "BufferStoreDword", [0, 0], [], [],
                new Gen5BufferMemoryControl(1, 10, 15, 0, 12, false, true, false, false)),
            decoded.Instructions[1] with { Pc = 28 },
        ]);
        var scalars = new uint[256];
        scalars[0] = (uint)(BufferAddress & uint.MaxValue);
        scalars[1] = (uint)(BufferAddress >> 32);
        scalars[2] = (uint)bufferBytes;
        var binding = new Gen5GlobalMemoryBinding(0, BufferAddress, [4, 20], [], 0, false, (ulong)bufferBytes) { Writable = true };
        var state = new Gen5ShaderState(program, scalars[..4], null);
        var evaluation = new Gen5ShaderEvaluation(scalars, scalars, [], [binding]);
        Assert.True(Gen5SpirvTranslator.TryCompileComputeShader(state, evaluation, threadCount, 1, 1,
            out var compiled, out var compileError), compileError);
        return compiled.Spirv;
    }
}
