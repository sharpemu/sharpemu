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
public sealed class GlobalMemoryShaderTests(HeadlessVulkanFixture fixture, ITestOutputHelper output)
    : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const ulong BufferAddress = 0x2_0000_1000;
    private const int BufferBytes = 192;
    private const int MemoryOffset = 64;
    private const int RegisterOutputOffset = 128;
    private const uint SourceRegister = 4;
    private const uint DestinationRegister = 12;

    public static IEnumerable<object[]> MemoryCases()
    {
        foreach (var usesFlatAddress in new[] { false, true })
        {
            foreach (var opcode in new uint[] { 8, 9, 10, 11, 12, 13, 14, 15, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 50, 56 })
            {
                yield return [usesFlatAddress, opcode, false, false, true];
                if (opcode is 50 or 56) yield return [usesFlatAddress, opcode, true, false, true];
            }

            yield return [usesFlatAddress, 14u, false, true, true];
            yield return [usesFlatAddress, 37u, false, true, true];
            yield return [usesFlatAddress, 50u, true, true, true];
            yield return [usesFlatAddress, 56u, true, true, true];
            foreach (var opcode in new uint[] { 14, 30, 50, 56 })
                yield return [usesFlatAddress, opcode, opcode >= 50, false, false];
        }
    }

    [Theory]
    [MemberData(nameof(MemoryCases))]
    public void MemoryInstructions_PreserveDataAndRegisterResultsOnTheDevice(
        bool usesFlatAddress, uint opcode, bool returnsValue, bool sharesDataRegister, bool laneEnabled)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        if (!vulkan.ShaderInt64)
        {
            Assert.False(ReferenceShaders.Required, "The translated shader test requires shaderInt64.");
            return;
        }

        var destination = sharesDataRegister ? SourceRegister : DestinationRegister;
        var initial = CreateInput();
        var expected = ExpectedResult(initial, opcode, returnsValue, destination, laneEnabled);
        var shader = CompileShader(usesFlatAddress, opcode, returnsValue, destination, laneEnabled);
        using (var harness = new ImageTestHarness(vulkan))
        using (var runner = new TilerComputeRunner(harness))
        {
            var pipeline = runner.CreatePipeline(shader, []);
            var records = harness.Upload(initial);
            var unusedArguments = harness.Upload(new byte[TileTransferArguments.Size]);
            // The shared runner's first three push words supply the compute thread limits.
            var limits = new TileTransferArguments { SourceBase = 1, DestinationBase = 1, Width = 1 };
            harness.Run(() => runner.Dispatch(pipeline, records, records, unusedArguments, 0, limits, 1, 1, 1));
            Assert.Equal(expected, harness.ReadBack(records.Handle, 0, BufferBytes));
            harness.AssertNoValidationMessages();
        }

        vulkan.AssertNoValidationMessages();
        output.WriteLine($"Verified opcode={opcode}, flat={usesFlatAddress}, return={returnsValue}, shared={sharesDataRegister}, active={laneEnabled} on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    private static byte[] CreateInput()
    {
        var bytes = new byte[BufferBytes];
        Array.Fill(bytes, (byte)0xCD);
        for (var index = 0; index < 16; index++)
            WriteWord(bytes, index * 4, 0xABCD_1200u + (uint)index);
        WriteWord(bytes, 0, 0xF000_0005);
        WriteWord(bytes, MemoryOffset, 0x8000_FFE1);
        WriteWord(bytes, MemoryOffset + 4, 0x1234_5678);
        WriteWord(bytes, MemoryOffset + 8, 0xFEDC_BA98);
        WriteWord(bytes, MemoryOffset + 12, 0x8765_4321);
        return bytes;
    }

    private static byte[] ExpectedResult(byte[] initial, uint opcode, bool returnsValue, uint destination, bool laneEnabled)
    {
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 64).CopyTo(expected.AsSpan(RegisterOutputOffset, 64));
        if (!laneEnabled) return expected;

        var outputOffset = RegisterOutputOffset + checked((int)(destination - SourceRegister) * 4);
        var original = ReadWord(initial, MemoryOffset);
        var source = ReadWord(initial, 0);
        var componentCount = opcode switch { 13 or 29 => 2, 14 or 30 => 4, 15 or 31 => 3, _ => 1 };
        if (opcode is 50 or 56)
        {
            WriteWord(expected, MemoryOffset, opcode == 50 ? unchecked(original + source) : Math.Max(original, source));
            if (returnsValue) WriteWord(expected, outputOffset, original);
        }
        else if (opcode is >= 24 and <= 31)
        {
            var shifted = opcode is 25 or 27 ? source >> 16 : source;
            if (opcode is 24 or 25) expected[MemoryOffset] = (byte)shifted;
            else if (opcode is 26 or 27) BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(MemoryOffset), (ushort)shifted);
            else initial.AsSpan(0, componentCount * 4).CopyTo(expected.AsSpan(MemoryOffset));
        }
        else if (opcode is >= 12 and <= 15)
        {
            initial.AsSpan(MemoryOffset, componentCount * 4).CopyTo(expected.AsSpan(outputOffset));
        }
        else
        {
            var loaded = opcode switch
            {
                8 or 32 or 33 => (uint)(byte)original,
                9 or 34 or 35 => unchecked((uint)(sbyte)original),
                10 or 36 or 37 => (uint)(ushort)original,
                11 => unchecked((uint)(short)original),
                _ => throw new InvalidOperationException("Unexpected memory opcode."),
            };
            if (opcode >= 32)
            {
                var previous = ReadWord(expected, outputOffset);
                loaded = (opcode & 1) != 0
                    ? (previous & 0xFFFF) | ((loaded & 0xFFFF) << 16)
                    : (previous & 0xFFFF_0000) | (loaded & 0xFFFF);
            }
            WriteWord(expected, outputOffset, loaded);
        }
        return expected;
    }

    private static byte[] CompileShader(bool usesFlatAddress, uint opcode, bool returnsValue, uint destination, bool laneEnabled)
    {
        var words = new List<uint>();
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE038_0000 | (group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        if (usesFlatAddress)
        {
            words.Add(0x7E00_0200 | (22u << 17) | (128 + MemoryOffset));
            words.Add(0xD70F_6A14);
            words.Add(16u | ((256u + 22) << 9));
            words.Add(0x7E00_0200 | (21u << 17) | 17u);
        }
        else
        {
            words.Add(0x7E00_0200 | (20u << 17) | (128 + MemoryOffset));
        }
        if (!laneEnabled) words.Add(0xBEFE_0380);
        var accessPc = checked((uint)words.Count * 4);
        words.Add(0xDC00_0000u | (opcode << 18) | (usesFlatAddress ? 0 : 2u << 14) | (returnsValue ? 1u << 16 : 0));
        words.Add((destination << 24) | (16u << 16) | (SourceRegister << 8) | 20u);
        if (!laneEnabled) words.Add(0xBEFE_03C1);
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE078_0000 | (RegisterOutputOffset + group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        words.Add(0xBF81_0000);

        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var code = new byte[words.Count * 4];
        for (var index = 0; index < words.Count; index++) WriteWord(code, index * 4, words[index]);
        Assert.True(memory.TryWrite(ShaderAddress, code));
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), ShaderAddress,
            out var program, out var decodeError), decodeError);
        var access = Assert.Single(program.Instructions, instruction => instruction.Pc == accessPc);
        var control = Assert.IsType<Gen5GlobalMemoryControl>(access.Control);
        Assert.Equal(SourceRegister, control.SourceVectorRegister);
        Assert.Equal(destination, control.DestinationVectorRegister);
        Assert.Equal(16u, control.ScalarAddress);
        Assert.Equal(usesFlatAddress, control.UsesFlatAddress);

        var scalars = new uint[256];
        scalars[0] = (uint)(BufferAddress & uint.MaxValue);
        scalars[1] = (uint)(BufferAddress >> 32);
        scalars[2] = BufferBytes;
        scalars[16] = scalars[0];
        scalars[17] = scalars[1];
        var accessPcs = program.Instructions.Where(instruction => instruction.Control is Gen5GlobalMemoryControl or Gen5BufferMemoryControl)
            .Select(instruction => instruction.Pc).ToArray();
        var binding = new Gen5GlobalMemoryBinding(0, BufferAddress, accessPcs, [], 0, false, BufferBytes) { Writable = true };
        var state = new Gen5ShaderState(program, scalars[..18], null);
        var evaluation = new Gen5ShaderEvaluation(scalars, scalars, [], [binding]);
        Assert.True(Gen5SpirvTranslator.TryCompileComputeShader(state, evaluation, 1, 1, 1, out var compiled, out var error), error);
        return compiled.Spirv;
    }

    private static uint ReadWord(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void WriteWord(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
