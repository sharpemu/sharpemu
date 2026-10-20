// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

[Collection(SchedulingStateCollection.Name)]
public sealed class GlobalMemoryShaderTests(HeadlessVulkanFixture fixture, ITestOutputHelper output)
    : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const ulong BufferAddress = 0x2_0000_0000;
    private const int BufferBytes = 192;
    private const int MemoryOffset = 64;
    private const int RegisterOutputOffset = 128;
    private const uint SourceRegister = 4;
    private const uint DestinationRegister = 12;

    [Theory]
    [InlineData(false, true, 14u)]
    [InlineData(true, true, 14u)]
    [InlineData(false, false, 14u)]
    [InlineData(true, false, 14u)]
    [InlineData(false, true, 8u)]
    [InlineData(true, true, 8u)]
    [InlineData(false, false, 8u)]
    [InlineData(true, false, 8u)]
    public void MissingDevicePageRecordsOnlyActiveLaneAccesses(bool flatAddress, bool laneEnabled, uint opcode)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var shader = CompileShader(flatAddress, opcode, false, DestinationRegister, laneEnabled);
        var initial = CreateInput();
        var result = RunOnce(vulkan, shader, initial, traceMissingPage: true, expectFault: laneEnabled);
        if (!laneEnabled)
            Assert.Equal(ExpectedResult(initial, opcode, false, DestinationRegister, false), result);
    }

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
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var destination = sharesDataRegister ? SourceRegister : DestinationRegister;
        var initial = CreateInput();
        var expected = ExpectedResult(initial, opcode, returnsValue, destination, laneEnabled);
        var shader = CompileShader(usesFlatAddress, opcode, returnsValue, destination, laneEnabled);
        Assert.Equal(expected, RunOnce(vulkan, shader, initial));

        vulkan.AssertNoValidationMessages();
        output.WriteLine($"Verified opcode={opcode}, flat={usesFlatAddress}, return={returnsValue}, shared={sharesDataRegister}, active={laneEnabled} on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    private const uint IdentitySwizzle = 4 | (5 << 3) | (6 << 6) | (7 << 9);
    private const uint SwizzleXFromW = 7 | (5 << 3) | (6 << 6) | (7 << 9);
    private const uint Format32Float = 22;
    private const uint Format8x4Unorm = 56;
    private const uint Format8x4Uint = 60;
    private const uint Format32x4Float = 77;

    public static IEnumerable<object[]> TypedCases()
    {
        // instruction format, descriptor word 3, byte offset, expected result kind
        yield return [Format32x4Float, (Format8x4Unorm << 12) | IdentitySwizzle, (uint)MemoryOffset, "raw"];
        yield return [Format32Float, (Format32x4Float << 12) | SwizzleXFromW, (uint)MemoryOffset, "first"];
        yield return [Format32x4Float, 0u, (uint)MemoryOffset, "zero"];
        yield return [Format32x4Float, (Format8x4Unorm << 12) | IdentitySwizzle, 0xF00u, "zero"];
        yield return [Format8x4Unorm, (Format32x4Float << 12) | IdentitySwizzle, (uint)UnormOffset, "unorm"];
        // An element that crosses the end of the binding reads as zero in every component.
        yield return [Format32x4Float, (Format8x4Unorm << 12) | IdentitySwizzle, (uint)(BufferBytes - 8), "zero"];
    }

    // Bytes 0x00 and 0xFF convert to exact floats, so the comparison does not depend on the
    // device's division rounding.
    private const int UnormOffset = MemoryOffset + 32;
    private const uint UnormWord = 0x00FF_FF00;

    // The instruction format selects the components and their conversion; the descriptor only
    // decides whether the buffer is bound, and the binding decides the bounds.
    [Theory]
    [MemberData(nameof(TypedCases))]
    public void TypedBufferLoads_UseTheInstructionFormatOnTheDevice(
        uint instructionFormat, uint descriptorWord3, uint offset, string expectedKind)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var initial = CreateInput();
        WriteWord(initial, UnormOffset, UnormWord);
        var expected = ExpectedTypedResult(initial, expectedKind, (int)offset);
        var shader = CompileTypedShader(instructionFormat, descriptorWord3, offset);
        var result = RunOnce(vulkan, shader, initial);
        Assert.Equal(expected, result);
        output.WriteLine($"Verified typed format={instructionFormat}, descriptor=0x{descriptorWord3:X}, offset=0x{offset:X}, kind={expectedKind} on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    [Theory]
    [InlineData(true, 1u, 188u, IdentitySwizzle, true)]
    [InlineData(true, 2u, 184u, IdentitySwizzle, true)]
    [InlineData(true, 2u, 188u, IdentitySwizzle, false)]
    [InlineData(false, 1u, 188u, IdentitySwizzle, true)]
    [InlineData(false, 2u, 184u, IdentitySwizzle, true)]
    [InlineData(false, 2u, 188u, IdentitySwizzle, false)]
    [InlineData(false, 1u, 184u, 5u, true)]
    [InlineData(false, 2u, 188u, 4u | (4u << 3), true)]
    [InlineData(false, 2u, 3840u, 1u, true)]
    public void FormattedLoads_CheckOnlySelectedComponentsOnTheDevice(
        bool typed, uint componentCount, uint offset, uint selectors, bool inBounds)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var initial = CreateInput();
        WriteWord(initial, 184, 0x3F800000);
        WriteWord(initial, 188, 0x40000000);
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 16).CopyTo(expected.AsSpan(MemoryOffset, 16));
        for (uint destination = 0; destination < componentCount; destination++)
        {
            var selector = typed ? destination + 4 : (selectors >> (int)(destination * 3)) & 7;
            var value = selector == 1 ? 0x3F800000u :
                inBounds && selector >= 4 ? ReadWord(initial, checked((int)(offset + (selector - 4) * 4))) : 0u;
            WriteWord(expected, MemoryOffset + (int)destination * 4, value);
        }

        var shader = CompileBoundaryAccess(typed, store: false, componentCount, offset, selectors);
        Assert.Equal(expected, RunOnce(vulkan, shader, initial));
    }

    // A V# read from scalar-buffer data is used through its registers on the device. An 8-bit
    // element in the last dword of a buffer whose size is not a multiple of four is in bounds:
    // the hardware checks the element, not its dword (Ghost of Yotei's 19-byte state buffers).
    [Theory]
    [InlineData(14u, 19u, 5u)]
    [InlineData(16u, 19u, 0xFFFF_FF81u)]
    [InlineData(18u, 19u, 0xFFFF_FF81u)]
    [InlineData(18u, 18u, 0u)]
    public void DeviceDescriptorByteLoads_InTheLastPartialDwordReadMemory(uint offset, uint size, uint value)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var initial = CreateInput();
        initial.AsSpan(0, 20).Fill(0x81);
        initial[14] = 5;
        WriteWord(initial, DescriptorOffset, (uint)(BufferAddress & uint.MaxValue));
        WriteWord(initial, DescriptorOffset + 4, (uint)(BufferAddress >> 32));
        WriteWord(initial, DescriptorOffset + 8, size);
        WriteWord(initial, DescriptorOffset + 12, (Format8Sint << 12) | IdentitySwizzle);
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 16).CopyTo(expected.AsSpan(MemoryOffset, 16));
        WriteWord(expected, MemoryOffset, value);

        var words = new List<uint>
        {
            0xE0380000, 0x80000400,
            // v_readfirstlane_b32 s20, v0: a lane-derived offset the plan cannot resolve.
            0x7E000000u | (20u << 17) | (2u << 9) | 256u,
            // s_buffer_load_dwordx4 s[16:19], s[0:3], s20 offset:DescriptorOffset
            0xF4000000u | (10u << 18) | (16u << 6), (20u << 25) | DescriptorOffset,
            0xBF8CC07F,
            0xE0000000 | offset, 0x80040400,
            0xE0780000 | MemoryOffset, 0x80000400,
            0xBF810000,
        };
        var shader = CompileProgram(DecodeProgram(words), BaseScalars());
        Assert.Equal(expected, RunOnce(vulkan, shader, initial));
    }

    // A branch the plan cannot evaluate (its selector is a scalar load through a lane-derived
    // offset) picks the V#: from memory when the selector is non-zero, otherwise one built from
    // a lane value. The load reads whichever descriptor the registers hold when it runs, and
    // each dword past the descriptor's size reads as zero.
    [Theory]
    [InlineData(1u, 0u, 40u)]
    [InlineData(0u, 0u, 40u)]
    [InlineData(1u, 32u, 40u)]
    [InlineData(0u, 32u, 40u)]
    [InlineData(0u, 8u, 12u)]
    public void ControlFlowSelectedDescriptor_DwordLoadReadsTheSelectedBuffer(uint selector, uint offset, uint size)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        const int selectorOffset = 100;
        const uint memoryBase = 16;
        var initial = CreateInput();
        WriteWord(initial, selectorOffset, selector);
        WriteWord(initial, DescriptorOffset, (uint)((BufferAddress + memoryBase) & uint.MaxValue));
        WriteWord(initial, DescriptorOffset + 4, (uint)((BufferAddress + memoryBase) >> 32));
        WriteWord(initial, DescriptorOffset + 8, size);
        WriteWord(initial, DescriptorOffset + 12, 0);

        var words = new List<uint>
        {
            // v_readfirstlane_b32 s20, v0: a lane-derived offset the plan cannot resolve.
            0x7E000000u | (20u << 17) | (2u << 9) | 256u,
            // s_buffer_load_dword s21, s[0:3], s20 offset:selectorOffset
            0xF4000000u | (8u << 18) | (21u << 6), (20u << 25) | selectorOffset,
            0xBF8CC07F,
            // s_cmp_eq_u32 s21, 0 ; s_cbranch_scc1 +4 (to the lane-built descriptor)
            0xBF068015,
            0xBF850000u | 4,
            // s_buffer_load_dwordx4 s[16:19], s[0:3], s20 offset:DescriptorOffset
            0xF4000000u | (10u << 18) | (16u << 6), (20u << 25) | DescriptorOffset,
            0xBF8CC07F,
            // s_branch +7 (to the access)
            0xBF820000u | 7,
            // v_add_nc_u32 v1, BufferAddress.lo, v0 ; v_readfirstlane_b32 s16, v1
            0x4A0200FF, (uint)(BufferAddress & uint.MaxValue),
            0x7E000000u | (16u << 17) | (2u << 9) | 257u,
            // s_mov_b32 s17, BufferAddress.hi ; s_mov_b32 s18, size ; s_mov_b32 s19, 0
            0xBE910382,
            0xBE9203FF, size,
            0xBE930380,
            // buffer_load_dwordx3 v[4:6], v0, s[16:19], 0 offset:offset
            0xE0000000u | (15u << 18) | offset, 0x80040400,
            // buffer_store_dwordx3 v[4:6], v0, s[0:3], 0 offset:MemoryOffset
            0xE0000000u | (31u << 18) | MemoryOffset, 0x80000400,
            0xBF810000,
        };
        var program = DecodeProgram(words);
        var listing = string.Join(' ', program.Instructions.Select(instruction => instruction.Opcode));
        var load = program.Instructions.SingleOrDefault(instruction => instruction.Opcode == "BufferLoadDwordx3")
            ?? throw new Xunit.Sdk.XunitException(listing);
        Assert.Equal(3u, Assert.IsType<Gen5BufferMemoryControl>(load.Control).DwordCount);
        Assert.True(program.Instructions.Any(instruction => instruction.Opcode == "BufferStoreDwordx3"), listing);
        var shader = CompileProgram(program, BaseScalars());
        Assert.Contains(shader.Plan.Memory.Entries, memory => memory.Pc == load.Pc && memory.DeviceDescriptor);
        Assert.DoesNotContain(shader.Plan.Info.Buffers, buffer => buffer.FirstUsePc == load.Pc);

        var sourceBase = selector != 0 ? memoryBase : 0u;
        var expected = (byte[])initial.Clone();
        for (var index = 0u; index < 3; index++)
        {
            var at = offset + index * 4;
            WriteWord(expected, MemoryOffset + (int)index * 4,
                at + 4 <= size ? ReadWord(initial, (int)(sourceBase + at)) : 0u);
        }

        Assert.Equal(expected, RunOnce(vulkan, shader, initial));
    }

    private const uint Format8Sint = 6;

    // A run-time format reads a component through two aligned dwords: 32- and 16-bit
    // components at unaligned offsets, straddling dwords and the end of the buffer.
    [Theory]
    [InlineData(20u, 4, 1u, 8u)]
    [InlineData(20u, 4, 2u, 8u)]
    [InlineData(20u, 4, 3u, 8u)]
    [InlineData(20u, 4, 4u, 8u)]
    [InlineData(20u, 4, 1u, 5u)]
    [InlineData(20u, 4, 5u, 8u)]
    [InlineData(20u, 4, 17u, 19u)]
    [InlineData(20u, 4, 16u, 19u)]
    [InlineData(11u, 2, 3u, 8u)]
    [InlineData(11u, 2, 7u, 8u)]
    [InlineData(11u, 2, 7u, 9u)]
    [InlineData(11u, 2, 1u, 8u)]
    public void DeviceDescriptorFormattedLoads_StraddleDwordsAndTheBufferEnd(
        uint format, int componentBytes, uint offset, uint size)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var initial = CreateInput();
        for (var index = 0; index < 24; index++) initial[index] = (byte)(index * 0x13 + 0x21);
        WriteWord(initial, DescriptorOffset, (uint)(BufferAddress & uint.MaxValue));
        WriteWord(initial, DescriptorOffset + 4, (uint)(BufferAddress >> 32));
        WriteWord(initial, DescriptorOffset + 8, size);
        WriteWord(initial, DescriptorOffset + 12, (format << 12) | IdentitySwizzle);
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 16).CopyTo(expected.AsSpan(MemoryOffset, 16));
        var value = 0u;
        if (offset + (uint)componentBytes <= size)
        {
            for (var index = componentBytes - 1; index >= 0; index--) value = (value << 8) | initial[(int)offset + index];
        }

        WriteWord(expected, MemoryOffset, value);

        var words = new List<uint>
        {
            0xE0380000, 0x80000400,
            0x7E000000u | (20u << 17) | (2u << 9) | 256u,
            0xF4000000u | (10u << 18) | (16u << 6), (20u << 25) | DescriptorOffset,
            0xBF8CC07F,
            // An odd offset cannot ride the immediate field (its low bit extends the opcode), so
            // it goes through the scalar offset operand: inline constants 129..192 are 1..64.
            0xE0000000, ((0x80u + offset) << 24) | 0x040400,
            0xE0780000 | MemoryOffset, 0x80000400,
            0xBF810000,
        };
        var shader = CompileProgram(DecodeProgram(words), BaseScalars());
        Assert.Equal(expected, RunOnce(vulkan, shader, initial));
    }

    // The format exists only in the V# loaded at run time, so the whole element is decoded on
    // the device: four components, each converted with the number format the descriptor names.
    [Theory]
    [InlineData(14u, 7u, 0u)]
    [InlineData(14u, 7u, 3u)]
    [InlineData(10u, 0u, 0u)]
    [InlineData(10u, 0u, 5u)]
    [InlineData(10u, 4u, 3u)]
    [InlineData(5u, 0u, 2u)]
    public void DeviceDescriptorFormattedLoads_DecodeAFourComponentRunTimeFormat(
        uint dataFormat, uint numberFormat, uint offset)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        uint format = 0;
        for (uint candidate = 1; candidate < 128 && format == 0; candidate++)
        {
            if (Gfx10UnifiedFormat.TryDecode(candidate, out var decodedData, out var decodedNumber) &&
                decodedData == dataFormat && decodedNumber == numberFormat)
            {
                format = candidate;
            }
        }

        Assert.NotEqual(0u, format);
        const uint size = 64;
        var initial = CreateInput();
        for (var index = 0; index < 64; index++) initial[index] = (byte)(index * 0x13 + 0x21);
        WriteDeviceDescriptor(initial, size, (format << 12) | IdentitySwizzle);

        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 16).CopyTo(expected.AsSpan(MemoryOffset, 16));
        uint[] components = dataFormat switch
        {
            14 => Enumerable.Range(0, 4).Select(index => ReadWord(initial, (int)offset + index * 4)).ToArray(),
            10 => Enumerable.Range(0, 4).Select(index =>
                numberFormat == 0 ? BitConverter.SingleToUInt32Bits(initial[(int)offset + index] / 255f) : initial[(int)offset + index]).ToArray(),
            _ => Enumerable.Range(0, 4).Select(index =>
                index < 2 ? BitConverter.SingleToUInt32Bits(((initial[(int)offset + index * 2] | (initial[(int)offset + index * 2 + 1] << 8)) & 0xFFFF) / 65535f)
                    : index == 3 ? BitConverter.SingleToUInt32Bits(1f) : 0u).ToArray(),
        };
        for (var index = 0; index < 4; index++) WriteWord(expected, MemoryOffset + index * 4, components[index]);

        var words = new List<uint>
        {
            0xE0380000, 0x80000400,
            0x7E000000u | (20u << 17) | (2u << 9) | 256u,
            0xF4000000u | (10u << 18) | (16u << 6), (20u << 25) | DescriptorOffset,
            0xBF8CC07F,
            // buffer_load_format_xyzw v[4:7], v0, s[16:19], soffset=offset
            0xE0000000 | (3u << 18), ((0x80u + offset) << 24) | 0x040400,
            0xE0780000 | MemoryOffset, 0x80000400,
            0xBF810000,
        };
        var shader = CompileProgram(DecodeProgram(words), BaseScalars());
        var actual = RunOnce(vulkan, shader, initial);
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            if (index >= MemoryOffset && index < MemoryOffset + 16) continue;
            Assert.True(expected[index] == actual[index], $"byte {index}");
        }

        for (var index = 0; index < 4; index++)
        {
            var want = ReadWord(expected, MemoryOffset + index * 4);
            var got = ReadWord(actual, MemoryOffset + index * 4);
            // A normalized component divides on the device, which may round one ulp away.
            Assert.True(
                want == got || (numberFormat == 0 && Math.Abs((long)want - got) <= 1),
                $"component {index}: expected 0x{want:X8}, got 0x{got:X8}");
        }
    }

    // Every formatted load of a run-time format decodes its layout from one shared table and
    // joins two dwords, so a program of many loads stays small.
    [Fact]
    public void DeviceDescriptorFormattedLoads_StayCompactPerLoad()
    {
        int Words(int loads)
        {
            var words = new List<uint>
            {
                0xE0380000, 0x80000400,
                0x7E000000u | (20u << 17) | (2u << 9) | 256u,
                0xF4000000u | (10u << 18) | (16u << 6), (20u << 25) | DescriptorOffset,
                0xBF8CC07F,
            };
            for (var index = 0; index < loads; index++)
            {
                // buffer_load_format_xyzw v[4:7], v0, s[16:19], 0 offen: offset = index * 16
                words.Add(0xE0000000 | (3u << 18) | (uint)(index * 16));
                words.Add(0x80040400);
            }

            words.Add(0xE0780000 | MemoryOffset);
            words.Add(0x80000400);
            words.Add(0xBF810000);
            var plan = ShaderResourcePlan.Extract(DecodeProgram(words), ShaderStage.Compute, 1, 0, 20);
            var scalars = BaseScalars();
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(scalars[..20]),
                ref snapshot, ref specialization, out var failure), failure.ToString());
            var resources = ResourceMaterializer.ApplyTo(plan, specialization);
            var layout = BindingLayout.Allocate(resources.Info,
                BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 20), false,
                ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
            var request = new ShaderCompileRequest(plan, resources, layout) { TraceDeviceAddressFaults = true };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            return shader.Spirv.Length / sizeof(uint);
        }

        var one = Words(1);
        var many = Words(9);
        var perLoad = (many - one) / 8;
        output.WriteLine($"SPIR-V words: 1 load={one}, 9 loads={many}, per load={perLoad}");
        Assert.True(perLoad < PerLoadWordBudget, $"{perLoad} words per formatted load");
    }

    // Measured 729 (the shared function is emitted once), 2183 inline in a loop, 10242 unrolled
    // (fault tracing on, nine loads minus one over eight).
    private const int PerLoadWordBudget = 3000;

    [Theory]
    [InlineData(1u, 188u, true)]
    [InlineData(2u, 184u, true)]
    [InlineData(2u, 188u, false)]
    public void TypedStores_CheckOnlyTransferredComponentsOnTheDevice(uint componentCount, uint offset, bool inBounds)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var initial = CreateInput();
        var expected = (byte[])initial.Clone();
        if (inBounds)
            initial.AsSpan(0, checked((int)componentCount * 4)).CopyTo(expected.AsSpan((int)offset));
        var shader = CompileBoundaryAccess(typed: true, store: true, componentCount, offset, IdentitySwizzle);
        Assert.Equal(expected, RunOnce(vulkan, shader, initial));
    }

    private static ShaderFixture CompileBoundaryAccess(bool typed, bool store, uint componentCount, uint offset, uint selectors)
    {
        var words = new List<uint> { 0xE0380000, 0x80000400 };
        var opcode = componentCount - 1 + (store ? 4u : 0u);
        words.Add(typed
            ? 0xE8000000 | (Format32x4Float << 19) | (opcode << 16) | offset
            : 0xE0000000 | (opcode << 18) | offset);
        words.Add(0x80040400);
        if (!store)
        {
            words.Add(0xE0780000 | MemoryOffset);
            words.Add(0x80000400);
        }
        words.Add(0xBF810000);
        var scalars = BaseScalars();
        scalars[16] = scalars[0];
        scalars[17] = scalars[1];
        scalars[18] = BufferBytes;
        scalars[19] = (Format32x4Float << 12) | selectors;
        return CompileProgram(DecodeProgram(words), scalars);
    }

    private const uint Format8x2Unorm = 14;
    private const uint Format16Float = 13;
    private const uint Format8x4Snorm = 57;
    private const uint Format8x4Sint = 61;
    private const uint Format2x10x3Uint = 48;
    private const uint Format10x11x2Float = 43;
    private const uint BoundDescriptor = (Format32x4Float << 12) | IdentitySwizzle;
    private const float Half = 0.5f;

    public static IEnumerable<object[]> DescriptorStoreCases()
    {
        foreach (var testCase in TypedStoreCases())
        {
            var format = (uint)testCase[0];
            testCase[1] = (uint)testCase[1] == 0 ? 0u : (format << 12) | IdentitySwizzle;
            yield return testCase;
        }

        // 2_10_10_10 UNORM is R10G10B10A2: X=1023 in bits 0..9, Y=512 in 10..19, Z=0, W=3 in 30..31.
        yield return [50u, (50u << 12) | IdentitySwizzle, 7u, (uint)MemoryOffset,
            Floats(1f, Half, 0f, 1f), Bytes(0xFF, 0x03, 0x08, 0xC0)];
    }

    [Theory]
    [MemberData(nameof(DescriptorStoreCases))]
    public void FormattedBufferStores_UseDescriptorConversionWithoutChangingNeighborBytes(
        uint format, uint descriptorWord3, uint opcode, uint offset, uint[] sources, byte[]? expectedBytes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var initial = CreateInput();
        for (var index = 0; index < sources.Length; index++)
            WriteWord(initial, index * 4, sources[index]);
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 64).CopyTo(expected.AsSpan(RegisterOutputOffset, 64));
        expectedBytes?.CopyTo(expected.AsSpan((int)offset));

        var shader = CompileTypedShader(format, descriptorWord3, offset, opcode, SourceRegister, typed: false);
        Assert.Equal(expected, RunOnce(vulkan, shader, initial));
    }

    public static IEnumerable<object[]> TypedStoreCases()
    {
        // instruction format, descriptor word 3, opcode (4 = X .. 7 = XYZW), byte offset,
        // the four source registers, the bytes expected at the offset (null = nothing written)
        yield return [Format8x2Unorm, BoundDescriptor, 5u, (uint)MemoryOffset, Floats(1f, Half, 0, 0), Bytes(0xFF, 0x80)];
        yield return [Format16Float, BoundDescriptor, 4u, (uint)MemoryOffset, Floats(1f, 0, 0, 0), Bytes(0x00, 0x3C)];
        yield return [Format8x4Unorm, BoundDescriptor, 7u, (uint)MemoryOffset, Floats(1f, Half, 0f, -1f), Bytes(0xFF, 0x80, 0x00, 0x00)];
        yield return [Format8x4Snorm, BoundDescriptor, 7u, (uint)MemoryOffset, Floats(1f, -1f, Half, -Half), Bytes(0x7F, 0x81, 0x40, 0xC0)];
        yield return [Format8x4Sint, BoundDescriptor, 7u, (uint)MemoryOffset, Words(unchecked((uint)-1), 127, unchecked((uint)-128), 200), Bytes(0xFF, 0x7F, 0x80, 0x7F)];
        yield return [Format2x10x3Uint, BoundDescriptor, 7u, (uint)MemoryOffset, Words(1, 2, 3, 1), Bytes(0x09, 0x30, 0x40, 0x00)];
        yield return [Format10x11x2Float, BoundDescriptor, 7u, (uint)MemoryOffset, Floats(1f, 2f, Half, 0), Bytes(0xE0, 0x01, 0x10, 0x70)];
        yield return [Format32x4Float, (Format8x4Unorm << 12) | IdentitySwizzle, 7u, (uint)MemoryOffset, Words(0xABCD1200, 0xABCD1201, 0xABCD1202, 0xABCD1203), Bytes(0x00, 0x12, 0xCD, 0xAB, 0x01, 0x12, 0xCD, 0xAB, 0x02, 0x12, 0xCD, 0xAB, 0x03, 0x12, 0xCD, 0xAB)];
        yield return [Format32x4Float, 0u, 7u, (uint)MemoryOffset, Words(1, 2, 3, 4), null!];
        // An element that crosses the end of the binding is not written at all.
        yield return [Format32x4Float, BoundDescriptor, 7u, (uint)(BufferBytes - 8), Words(1, 2, 3, 4), null!];
        yield return [Format8x2Unorm, BoundDescriptor, 5u, (uint)(BufferBytes - 1), Floats(1f, 1f, 0, 0), null!];
    }

    // A typed store converts each register with the instruction's number format, packs the
    // components at their bit offsets and writes the whole element or nothing.
    [Theory]
    [MemberData(nameof(TypedStoreCases))]
    public void TypedBufferStores_EncodeComponentsByTheInstructionFormatOnTheDevice(
        uint instructionFormat, uint descriptorWord3, uint opcode, uint offset, uint[] sources, byte[]? expectedBytes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var initial = CreateInput();
        for (var index = 0; index < sources.Length; index++)
            WriteWord(initial, index * 4, sources[index]);
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 64).CopyTo(expected.AsSpan(RegisterOutputOffset, 64));
        expectedBytes?.CopyTo(expected.AsSpan((int)offset));

        var shader = CompileTypedShader(instructionFormat, descriptorWord3, offset, opcode, SourceRegister);
        var result = RunOnce(vulkan, shader, initial);
        Assert.Equal(expected, result);
        output.WriteLine($"Verified typed store format={instructionFormat}, descriptor=0x{descriptorWord3:X}, opcode={opcode}, offset={offset} on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    // Four invocations each store one byte of the same dword; every byte survives because
    // the sub-word merge is atomic. Typed 8-bit stores and BUFFER_STORE_BYTE share the path.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdjacentByteStores_FromSeparateInvocationsKeepEveryByteOnTheDevice(bool typed)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        const uint threadCount = 4;
        var initial = CreateInput();
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 64).CopyTo(expected.AsSpan(RegisterOutputOffset, 64));
        WriteWord(expected, RegisterOutputOffset, 0xFF);
        WriteWord(expected, MemoryOffset, 0xFFFF_FFFF);

        var shader = CompileAdjacentByteStoreShader(typed, threadCount);
        var result = RunOnce(vulkan, shader, initial, threadCount);
        Assert.Equal(expected, result);
        output.WriteLine($"Verified {threadCount} adjacent byte stores, typed={typed} on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    private static void WriteDeviceDescriptor(byte[] bytes, uint size, uint word3)
    {
        WriteWord(bytes, DescriptorOffset, (uint)(BufferAddress & uint.MaxValue));
        WriteWord(bytes, DescriptorOffset + 4, (uint)(BufferAddress >> 32));
        WriteWord(bytes, DescriptorOffset + 8, size);
        WriteWord(bytes, DescriptorOffset + 12, word3);
    }

    // The V# comes from memory through a lane-derived offset the plan cannot resolve, so the
    // store goes through the device address. v4..v7 hold the first four words of the buffer
    // (or 0xFF in v4 for the adjacent-byte case) and are dumped at RegisterOutputOffset.
    private static ShaderFixture CompileDeviceStoreShader(
        uint opcode, uint offset, uint threadCount = 1, bool vectorOffset = false, bool byteData = false)
    {
        var words = new List<uint>
        {
            0xE0380000, 0x80000400,
            0x7E000000u | (20u << 17) | (2u << 9) | 256u,
            0xF4000000u | (10u << 18) | (16u << 6), (20u << 25) | DescriptorOffset,
            0xBF8CC07F,
        };
        if (byteData)
        {
            words.Add(0x7E00_02FF | (SourceRegister << 17));
            words.Add(0xFF);
        }

        var accessPc = checked((uint)words.Count * 4);
        // An odd offset cannot ride the immediate field, so its low bit goes through the scalar operand.
        words.Add(0xE000_0000u | (opcode << 18) | (vectorOffset ? 1u << 12 : 0u) | (offset & ~1u));
        words.Add(((0x80u + (offset & 1u)) << 24) | ((16u / 4) << 16) | (SourceRegister << 8));
        words.Add(0xE078_0000 | RegisterOutputOffset);
        words.Add(0x8000_0400);
        words.Add(0xBF81_0000);

        var program = DecodeProgram(words);
        var access = Assert.Single(program.Instructions, instruction => instruction.Pc == accessPc);
        var control = Assert.IsType<Gen5BufferMemoryControl>(access.Control);
        Assert.False(control.Typed);
        Assert.Equal(opcode - 3, control.DwordCount);
        Assert.Equal(16u, control.ScalarResource);
        return CompileProgram(program, BaseScalars(), threadCount);
    }

    private static byte[] DeviceStoreInput(uint[] sources, uint size, uint descriptorWord3)
    {
        var initial = CreateInput();
        for (var index = 0; index < sources.Length; index++)
            WriteWord(initial, index * 4, sources[index]);
        WriteDeviceDescriptor(initial, size, descriptorWord3);
        return initial;
    }

    // The bound-buffer store cases, with the V# read through a device address: same bytes
    // written, neighbours untouched, an element past the end or an unbound V# writes nothing.
    [Theory]
    [MemberData(nameof(DescriptorStoreCases))]
    public void DeviceDescriptorFormattedStores_EncodeComponentsLikeBoundBuffers(
        uint format, uint descriptorWord3, uint opcode, uint offset, uint[] sources, byte[]? expectedBytes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var initial = DeviceStoreInput(sources, BufferBytes, descriptorWord3);
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 16).CopyTo(expected.AsSpan(RegisterOutputOffset, 16));
        expectedBytes?.CopyTo(expected.AsSpan((int)offset));

        var shader = CompileDeviceStoreShader(opcode, offset);
        Assert.Equal(expected, RunOnce(vulkan, shader, initial));
        output.WriteLine($"Verified device store format={format}, descriptor=0x{descriptorWord3:X}, opcode={opcode}, offset={offset} on {vulkan.DeviceName}.");
    }

    public static IEnumerable<object[]> UnalignedDeviceStoreCases()
    {
        // format, opcode, byte offset, sources, the bytes expected there
        yield return [Format8x4Unorm, 7u, 65u, Floats(1f, Half, 0f, -1f), Bytes(0xFF, 0x80, 0x00, 0x00)];
        yield return [Format8x4Unorm, 7u, 66u, Floats(1f, Half, 1f, 1f), Bytes(0xFF, 0x80, 0xFF, 0xFF)];
        yield return [Format8x4Unorm, 5u, 67u, Floats(1f, 1f, 0f, 0f), Bytes(0xFF, 0xFF)];
        yield return [Format16Float, 4u, 65u, Floats(1f, 0, 0, 0), Bytes(0x00, 0x3C)];
        yield return [Format16Float, 4u, 67u, Floats(-2f, 0, 0, 0), Bytes(0x00, 0xC0)];
        // 16_16 UNORM (23): both halves straddle a dword at an odd address.
        yield return [23u, 5u, 67u, Floats(1f, 0f, 0, 0), Bytes(0xFF, 0xFF, 0x00, 0x00)];
        // 16_16 SINT (28) clamps to the signed range.
        yield return [28u, 5u, 66u, Words(unchecked((uint)-5), 40000, 0, 0), Bytes(0xFB, 0xFF, 0xFF, 0x7F)];
        yield return [Format32x4Float, 7u, 66u, Words(0x11223344, 0x55667788, 0x99AABBCC, 0xDDEEFF00),
            Bytes(0x44, 0x33, 0x22, 0x11, 0x88, 0x77, 0x66, 0x55, 0xCC, 0xBB, 0xAA, 0x99, 0x00, 0xFF, 0xEE, 0xDD)];
        yield return [Format32Float, 4u, 71u, Words(0xA1B2C3D4, 0, 0, 0), Bytes(0xD4, 0xC3, 0xB2, 0xA1)];
        // 32_32 FLOAT-like pair (data format 11) keeps only the transferred X when opcode is X.
        yield return [64u, 4u, 65u, Words(0x01020304, 0x0A0B0C0D, 0, 0), Bytes(0x04, 0x03, 0x02, 0x01)];
        yield return [64u, 5u, 65u, Words(0x01020304, 0x0A0B0C0D, 0, 0),
            Bytes(0x04, 0x03, 0x02, 0x01, 0x0D, 0x0C, 0x0B, 0x0A)];
        // 2_10_10_10 UNORM, the same packed element shifted across a dword boundary.
        yield return [50u, 7u, 66u, Floats(1f, Half, 0f, 1f), Bytes(0xFF, 0x03, 0x08, 0xC0)];
        // 11_11_10 FLOAT (43) and 10_11_11 FLOAT (36) at an odd address.
        yield return [43u, 7u, 65u, Floats(1f, 2f, Half, 0), Bytes(0xE0, 0x01, 0x10, 0x70)];
        yield return [36u, 7u, 65u, Floats(1f, 2f, Half, 0), Bytes(0xC0, 0x03, 0x20, 0x70)];
    }

    // An unaligned element merges into two dwords; every byte around it keeps its value.
    [Theory]
    [MemberData(nameof(UnalignedDeviceStoreCases))]
    public void DeviceDescriptorFormattedStores_MergeUnalignedElementsWithoutTouchingNeighbours(
        uint format, uint opcode, uint offset, uint[] sources, byte[] expectedBytes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var initial = DeviceStoreInput(sources, BufferBytes, (format << 12) | IdentitySwizzle);
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 16).CopyTo(expected.AsSpan(RegisterOutputOffset, 16));
        expectedBytes.CopyTo(expected.AsSpan((int)offset));

        var shader = CompileDeviceStoreShader(opcode, offset);
        Assert.Equal(expected, RunOnce(vulkan, shader, initial));
    }

    // The element must fit the V# size as a whole: a store that would end one byte past
    // it writes nothing, and one ending on the last byte writes everything.
    [Theory]
    [InlineData(7u, 64u, 80u, true)]
    [InlineData(7u, 64u, 79u, false)]
    [InlineData(7u, 66u, 82u, true)]
    [InlineData(7u, 66u, 81u, false)]
    [InlineData(6u, 64u, 76u, true)]
    [InlineData(6u, 64u, 75u, false)]
    public void DeviceDescriptorFormattedStores_AreDroppedWhenTheElementPassesTheDescriptorSize(
        uint opcode, uint offset, uint size, bool written)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var sources = Words(0x11111111, 0x22222222, 0x33333333, 0x44444444);
        var initial = DeviceStoreInput(sources, size, (Format32x4Float << 12) | IdentitySwizzle);
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 16).CopyTo(expected.AsSpan(RegisterOutputOffset, 16));
        if (written) initial.AsSpan(0, (int)(opcode - 3) * 4).CopyTo(expected.AsSpan((int)offset));
        var shader = CompileDeviceStoreShader(opcode, offset);
        Assert.Equal(expected, RunOnce(vulkan, shader, initial));
    }

    // Four invocations store one byte each into the same dword through a device V#; the
    // merge is atomic, so every byte survives.
    [Fact]
    public void DeviceDescriptorFormattedStores_FromSeparateInvocationsKeepEveryByte()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        const uint format8Uint = 5;
        var initial = DeviceStoreInput([], BufferBytes, (format8Uint << 12) | IdentitySwizzle);
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 16).CopyTo(expected.AsSpan(RegisterOutputOffset, 16));
        WriteWord(expected, RegisterOutputOffset, 0xFF);
        WriteWord(expected, MemoryOffset, 0xFFFF_FFFF);

        // buffer_store_format_x v4, v0, s[16:19], 0 offen offset:MemoryOffset
        var shader = CompileDeviceStoreShader(4, MemoryOffset, threadCount: 4, vectorOffset: true, byteData: true);
        Assert.Equal(expected, RunOnce(vulkan, shader, initial, 4));
    }

    private static IEnumerable<uint> SweepFormats()
    {
        for (uint format = 1; format <= 0x7F; format++)
        {
            if (Gfx10UnifiedFormat.TryDecode(format, out var dataFormat, out _) &&
                Gfx10UnifiedFormat.ComponentCount(dataFormat) != 0)
                yield return format;
        }
    }

    // Every decodable format with floats, special floats and integers: the device store writes
    // the bytes the bound store writes, at an aligned and an unaligned element address.
    [Fact]
    public void DeviceDescriptorFormattedStores_MatchBoundStoresForEveryFormat()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var sourceSets = new[]
        {
            Floats(0.3f, -0.7f, 1.9f, 200.25f),
            [0x7FC00000u, 0xFF800000u, 0x00000003u, 0x80000001u],
            Words(0xFFFFFFF0u, 77u, 0x12345678u, 3u),
        };
        var mismatches = new List<string>();
        foreach (var format in SweepFormats())
        {
            var descriptor = (format << 12) | IdentitySwizzle;
            foreach (var opcode in new[] { 5u, 7u })
            {
                for (var set = 0; set < sourceSets.Length; set++)
                {
                    // A bound store of whole-dword components rounds the element down to a dword;
                    // the device store, like the device load, honours the exact byte address.
                    Gfx10UnifiedFormat.TryDecode(format, out var dataFormat, out _);
                    var offset = set == 1 && !Gfx10UnifiedFormat.HasWholeDwordComponents(dataFormat)
                        ? 67u
                        : (uint)MemoryOffset;
                    var initial = DeviceStoreInput(sourceSets[set], BufferBytes, descriptor);
                    var bound = RunOnce(vulkan, CompileTypedShader(format, descriptor, offset, opcode, SourceRegister, typed: false), initial);
                    var device = RunOnce(vulkan, CompileDeviceStoreShader(opcode, offset), initial);
                    if (!bound.AsSpan(0, RegisterOutputOffset).SequenceEqual(device.AsSpan(0, RegisterOutputOffset)))
                        mismatches.Add($"format={format} opcode={opcode} set={set} bound={Convert.ToHexString(bound.AsSpan(MemoryOffset, 24))} device={Convert.ToHexString(device.AsSpan(MemoryOffset, 24))}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    // A formatted store of a run-time format shares one component loop, so a program of many
    // stores stays small.
    [Fact]
    public void DeviceDescriptorFormattedStores_StayCompactPerStore()
    {
        int Words(int stores)
        {
            var words = new List<uint>
            {
                0xE0380000, 0x80000400,
                0x7E000000u | (20u << 17) | (2u << 9) | 256u,
                0xF4000000u | (10u << 18) | (16u << 6), (20u << 25) | DescriptorOffset,
                0xBF8CC07F,
            };
            for (var index = 0; index < stores; index++)
            {
                // buffer_store_format_xyzw v[4:7], v0, s[16:19], 0 offset: index * 16
                words.Add(0xE0000000 | (7u << 18) | (uint)(64 + index * 16));
                words.Add(0x80040400);
            }

            words.Add(0xBF810000);
            var plan = ShaderResourcePlan.Extract(DecodeProgram(words), ShaderStage.Compute, 1, 0, 20);
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(BaseScalars()[..20]),
                ref snapshot, ref specialization, out var failure), failure.ToString());
            var resources = ResourceMaterializer.ApplyTo(plan, specialization);
            var layout = BindingLayout.Allocate(resources.Info,
                BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 20), false,
                ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
            var request = new ShaderCompileRequest(plan, resources, layout) { TraceDeviceAddressFaults = true };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            return shader.Spirv.Length / sizeof(uint);
        }

        var one = Words(1);
        var many = Words(9);
        var perStore = (many - one) / 8;
        output.WriteLine($"SPIR-V words: 1 store={one}, 9 stores={many}, per store={perStore}");
        Assert.True(perStore < PerStoreWordBudget, $"{perStore} words per formatted store");
    }

    // Measured 1947 (fault tracing on, nine stores minus one over eight).
    private const int PerStoreWordBudget = 3000;

    private static uint[] Floats(float x, float y, float z, float w) =>
        [BitConverter.SingleToUInt32Bits(x), BitConverter.SingleToUInt32Bits(y), BitConverter.SingleToUInt32Bits(z), BitConverter.SingleToUInt32Bits(w)];

    private static uint[] Words(uint x, uint y, uint z, uint w) => [x, y, z, w];

    private static byte[] Bytes(params byte[] bytes) => bytes;

    // One plan resolves two descriptors; each format selects its own compiled specialization.
    [Fact]
    public void FormattedUntypedLoad_FollowsTheDescriptorAcrossSpecializations()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var shader = CompileDescriptorFollowingShader();
        // The integer 8_8_8_8 format converts without a division, so the result is exact.
        foreach (var (descriptorWord3, expectedKind) in new[]
                 {
                     ((Format8x4Uint << 12) | IdentitySwizzle, "uint8"),
                     ((Format32x4Float << 12) | IdentitySwizzle, "raw"),
                 })
        {
            var initial = CreateInput();
            WriteWord(initial, UnormOffset, UnormWord);
            WriteWord(initial, DescriptorOffset, (uint)(BufferAddress & uint.MaxValue));
            WriteWord(initial, DescriptorOffset + 4, (uint)(BufferAddress >> 32));
            WriteWord(initial, DescriptorOffset + 8, BufferBytes);
            WriteWord(initial, DescriptorOffset + 12, descriptorWord3);
            var expected = ExpectedTypedResult(initial, expectedKind, UnormOffset);
            Assert.Equal(expected, RunOnce(vulkan, shader, initial));
        }

        vulkan.AssertNoValidationMessages();
        output.WriteLine($"Verified one plan with two descriptor specializations on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    private const int DescriptorOffset = 48;

    private sealed record ShaderFixture(ShaderResourcePlan Plan, uint[] UserData, uint LocalSizeX);

    private static byte[] RunOnce(HeadlessVulkan vulkan, ShaderFixture fixture, byte[] initial, uint threadCount = 1, bool traceMissingPage = false, bool expectFault = true)
    {
        bool ReadWordFromFixture(ulong address, out uint value)
        {
            value = 0;
            if (address < BufferAddress || address - BufferAddress > (ulong)initial.Length - sizeof(uint)) return false;
            value = ReadWord(initial, (int)(address - BufferAddress));
            return true;
        }

        var plan = fixture.Plan;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var inputs = ResourceTestProgram.Inputs(fixture.UserData, ReadWordFromFixture, ReadWordFromFixture);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, out var failure), failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 20), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            TraceDeviceAddressFaults = traceMissingPage,
            LocalSizeX = fixture.LocalSizeX,
            ThreadCountX = threadCount,
            ThreadCountY = 1,
            ThreadCountZ = 1,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var records = runner.CreateBuffer(initial);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = snapshot.Buffers.Select(_ => records).ToArray(),
        };
        if (resources.Info.UsesDeviceAddresses)
        {
            var pageCount = (BufferAddress >> Gen5SpirvTranslator.DeviceAddressPageBits) + 1;
            bindings[DescriptorBindingKind.DeviceAddressPageTable] =
                [runner.CreatePageTable(pageCount, traceMissingPage ? [] : [(BufferAddress, records, 0ul)])];
            bindings[DescriptorBindingKind.FaultBuffer] = [runner.CreateBuffer(((pageCount + 31) / 32) * sizeof(uint) + (traceMissingPage ? 64UL : 0UL))];
        }

        var flattenedTable = snapshot.FlattenedResourceTable.ToArray();
        foreach (var slot in plan.WrittenRangeSlotByHandle.Values)
        {
            flattenedTable[slot] = (uint)(BufferAddress & uint.MaxValue);
            flattenedTable[slot + 1] = (uint)(BufferAddress >> 32);
            flattenedTable[slot + 2] = BufferBytes;
        }
        harness.Run(() => runner.Dispatch(fixture.UserData, bindings, 1, flattenedTable: flattenedTable));
        var result = harness.ReadBack(records.Handle, 0, BufferBytes);
        if (traceMissingPage)
        {
            var faults = bindings[DescriptorBindingKind.FaultBuffer][0];
            var diagnostic = harness.ReadBack(faults.Handle, faults.Size - 64, 32);
            Assert.Equal(expectFault ? 1u : 0u, ReadWord(diagnostic, 0));
            if (expectFault)
            {
                Assert.Equal(plan.Hash, (ulong)ReadWord(diagnostic, 4) | ((ulong)ReadWord(diagnostic, 8) << 32));
                var access = Assert.Single(plan.Graph.Program.Instructions, instruction => instruction.Control is Gen5GlobalMemoryControl);
                Assert.Equal(access.Pc, ReadWord(diagnostic, 12));
                Assert.Equal(BufferAddress + MemoryOffset, (ulong)ReadWord(diagnostic, 16) | ((ulong)ReadWord(diagnostic, 20) << 32));
                Assert.Equal((uint)plan.Stage, ReadWord(diagnostic, 24));
            }
            else
            {
                Assert.All(harness.ReadBack(faults.Handle, 0, faults.Size), value => Assert.Equal(0, value));
            }
        }
        harness.AssertNoValidationMessages();
        vulkan.AssertNoValidationMessages();
        return result;
    }

    private static byte[] ExpectedTypedResult(byte[] initial, string expectedKind, int loadOffset)
    {
        var expected = (byte[])initial.Clone();
        initial.AsSpan(0, 64).CopyTo(expected.AsSpan(RegisterOutputOffset, 64));
        var outputOffset = RegisterOutputOffset + checked((int)(DestinationRegister - SourceRegister) * 4);
        var loaded = new uint[4];
        switch (expectedKind)
        {
            case "raw":
                for (var component = 0; component < 4; component++)
                    loaded[component] = ReadWord(initial, loadOffset + component * 4);
                break;
            case "first":
                loaded[0] = ReadWord(initial, loadOffset);
                break;
            case "unorm":
                for (var component = 0; component < 4; component++)
                    loaded[component] = BitConverter.SingleToUInt32Bits(initial[loadOffset + component] / 255f);
                break;
            case "uint8":
                for (var component = 0; component < 4; component++)
                    loaded[component] = initial[loadOffset + component];
                break;
            case "zero":
                break;
            default:
                throw new InvalidOperationException("Unexpected typed load expectation.");
        }

        for (var component = 0; component < 4; component++)
            WriteWord(expected, outputOffset + component * 4, loaded[component]);
        return expected;
    }

    private static ShaderFixture CompileTypedShader(uint instructionFormat, uint descriptorWord3, uint offset) =>
        CompileTypedShader(instructionFormat, descriptorWord3, offset, opcode: 3, DestinationRegister);

    private static ShaderFixture CompileTypedShader(uint instructionFormat, uint descriptorWord3, uint offset, uint opcode, uint dataRegister, bool typed = true)
    {
        const uint descriptorRegister = 16;
        var words = new List<uint>();
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE038_0000 | (group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        var accessPc = checked((uint)words.Count * 4);
        // Keep the extended opcode bit clear; use the scalar operand for an odd byte offset.
        words.Add(typed
            ? 0xE800_0000u | (instructionFormat << 19) | (opcode << 16) | offset
            : 0xE000_0000u | (opcode << 18) | (offset & ~1u));
        var scalarOffsetOperand = typed ? 0x80u : 0x80u + (offset & 1u);
        words.Add((scalarOffsetOperand << 24) | ((descriptorRegister / 4) << 16) | (dataRegister << 8));
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE078_0000 | (RegisterOutputOffset + group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        words.Add(0xBF81_0000);

        var program = DecodeProgram(words);
        var access = Assert.Single(program.Instructions, instruction => instruction.Pc == accessPc);
        var control = Assert.IsType<Gen5BufferMemoryControl>(access.Control);
        Assert.Equal(typed, control.Typed);
        Assert.Equal(typed ? instructionFormat : 0u, control.TypedFormat);
        Assert.Equal(descriptorRegister, control.ScalarResource);

        var scalars = BaseScalars();
        scalars[descriptorRegister] = scalars[0];
        scalars[descriptorRegister + 1] = scalars[1];
        scalars[descriptorRegister + 2] = BufferBytes;
        scalars[descriptorRegister + 3] = descriptorWord3;
        return CompileProgram(program, scalars);
    }

    // v4 = 0xFF, then each thread stores one byte at MemoryOffset + v0 (its thread index).
    private static ShaderFixture CompileAdjacentByteStoreShader(bool typed, uint threadCount)
    {
        const uint descriptorRegister = 16;
        const uint format8Uint = 5;
        var words = new List<uint>();
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE038_0000 | (group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        words.Add(0x7E00_02FF | (SourceRegister << 17));
        words.Add(0xFF);
        var accessPc = checked((uint)words.Count * 4);
        const uint offsetEnabled = 1u << 12;
        words.Add(typed
            ? 0xE800_0000u | (format8Uint << 19) | (4u << 16) | offsetEnabled | MemoryOffset
            : 0xE000_0000u | (24u << 18) | offsetEnabled | MemoryOffset);
        words.Add((0x80u << 24) | ((descriptorRegister / 4) << 16) | (SourceRegister << 8));
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE078_0000 | (RegisterOutputOffset + group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        words.Add(0xBF81_0000);

        var program = DecodeProgram(words);
        var access = Assert.Single(program.Instructions, instruction => instruction.Pc == accessPc);
        var control = Assert.IsType<Gen5BufferMemoryControl>(access.Control);
        Assert.Equal(typed, control.Typed);
        Assert.Equal(typed ? "TBufferStoreFormatX" : "BufferStoreByte", access.Opcode);
        Assert.True(control.OffsetEnabled);
        Assert.Equal(0u, control.VectorAddress);

        var scalars = BaseScalars();
        scalars[descriptorRegister] = scalars[0];
        scalars[descriptorRegister + 1] = scalars[1];
        scalars[descriptorRegister + 2] = BufferBytes;
        scalars[descriptorRegister + 3] = BoundDescriptor;
        return CompileProgram(program, scalars, threadCount);
    }

    private static ShaderFixture CompileDescriptorFollowingShader()
    {
        var words = new List<uint>();
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE038_0000 | (group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        var loadDescriptorPc = checked((uint)words.Count * 4);
        // s_load_dwordx4 s[16:19], s[0:1], 0x30
        words.Add(0xF400_0000u | (2u << 18) | (16u << 6));
        words.Add(0xFA00_0000u | DescriptorOffset);
        var accessPc = checked((uint)words.Count * 4);
        // buffer_load_format_xyzw v[12:15], off, s[16:19], 0 offset:96
        words.Add(0xE000_0000u | (3u << 18) | UnormOffset);
        words.Add((0x80u << 24) | (4u << 16) | (DestinationRegister << 8));
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE078_0000 | (RegisterOutputOffset + group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        words.Add(0xBF81_0000);

        var program = DecodeProgram(words);
        var loadDescriptor = Assert.Single(program.Instructions, instruction => instruction.Pc == loadDescriptorPc);
        Assert.Equal("SLoadDwordx4", loadDescriptor.Opcode);
        var access = Assert.Single(program.Instructions, instruction => instruction.Pc == accessPc);
        var control = Assert.IsType<Gen5BufferMemoryControl>(access.Control);
        Assert.False(control.Typed);
        Assert.Equal(16u, control.ScalarResource);
        return CompileProgram(program, BaseScalars());
    }

    private static Gen5ShaderProgram DecodeProgram(List<uint> words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var code = new byte[words.Count * 4];
        for (var index = 0; index < words.Count; index++) WriteWord(code, index * 4, words[index]);
        Assert.True(memory.TryWrite(ShaderAddress, code));
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), ShaderAddress,
            out var program, out var decodeError), decodeError);
        return program;
    }

    private static uint[] BaseScalars()
    {
        var scalars = new uint[256];
        scalars[0] = (uint)(BufferAddress & uint.MaxValue);
        scalars[1] = (uint)(BufferAddress >> 32);
        scalars[2] = BufferBytes;
        return scalars;
    }

    // Every memory access of the program resolves to the one bound buffer.
    private static ShaderFixture CompileProgram(Gen5ShaderProgram program, uint[] scalars, uint localSizeX = 1)
    {
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 1, 0, 20);
        return new ShaderFixture(plan, scalars[..20], localSizeX);
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

    private static ShaderFixture CompileShader(bool usesFlatAddress, uint opcode, bool returnsValue, uint destination, bool laneEnabled)
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
        return CompileProgram(program, scalars);
    }

    private static uint ReadWord(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void WriteWord(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
