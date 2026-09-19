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
    private const uint Format10x3x2Uint = 48;
    private const uint Format11x2x10Float = 43;
    private const uint BoundDescriptor = (Format32x4Float << 12) | IdentitySwizzle;
    private const float Half = 0.5f;

    public static IEnumerable<object[]> TypedStoreCases()
    {
        // instruction format, descriptor word 3, opcode (4 = X .. 7 = XYZW), byte offset,
        // the four source registers, the bytes expected at the offset (null = nothing written)
        yield return [Format8x2Unorm, BoundDescriptor, 5u, (uint)MemoryOffset, Floats(1f, Half, 0, 0), Bytes(0xFF, 0x80)];
        yield return [Format16Float, BoundDescriptor, 4u, (uint)MemoryOffset, Floats(1f, 0, 0, 0), Bytes(0x00, 0x3C)];
        yield return [Format8x4Unorm, BoundDescriptor, 7u, (uint)MemoryOffset, Floats(1f, Half, 0f, -1f), Bytes(0xFF, 0x80, 0x00, 0x00)];
        yield return [Format8x4Snorm, BoundDescriptor, 7u, (uint)MemoryOffset, Floats(1f, -1f, Half, -Half), Bytes(0x7F, 0x81, 0x40, 0xC0)];
        yield return [Format8x4Sint, BoundDescriptor, 7u, (uint)MemoryOffset, Words(unchecked((uint)-1), 127, unchecked((uint)-128), 200), Bytes(0xFF, 0x7F, 0x80, 0x7F)];
        yield return [Format10x3x2Uint, BoundDescriptor, 7u, (uint)MemoryOffset, Words(1, 2, 3, 1), Bytes(0x01, 0x08, 0x30, 0x40)];
        yield return [Format11x2x10Float, BoundDescriptor, 7u, (uint)MemoryOffset, Floats(1f, 2f, Half, 0), Bytes(0xC0, 0x03, 0x20, 0x70)];
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
            bindings[DescriptorBindingKind.FaultBuffer] = [runner.CreateBuffer(((pageCount + 31) / 32) * sizeof(uint) + (traceMissingPage ? 32UL : 0UL))];
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
            var diagnostic = harness.ReadBack(faults.Handle, faults.Size - 32, 32);
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

    private static ShaderFixture CompileTypedShader(uint instructionFormat, uint descriptorWord3, uint offset, uint opcode, uint dataRegister)
    {
        const uint descriptorRegister = 16;
        var words = new List<uint>();
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE038_0000 | (group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        var accessPc = checked((uint)words.Count * 4);
        // tbuffer_load/store_format_* v[data..], off, s[16:19], 0 format:instructionFormat offset:offset
        words.Add(0xE800_0000u | (instructionFormat << 19) | (opcode << 16) | offset);
        words.Add((0x80u << 24) | ((descriptorRegister / 4) << 16) | (dataRegister << 8));
        for (uint group = 0; group < 4; group++)
        {
            words.Add(0xE078_0000 | (RegisterOutputOffset + group * 16));
            words.Add(0x8000_0000 | ((SourceRegister + group * 4) << 8));
        }
        words.Add(0xBF81_0000);

        var program = DecodeProgram(words);
        var access = Assert.Single(program.Instructions, instruction => instruction.Pc == accessPc);
        var control = Assert.IsType<Gen5BufferMemoryControl>(access.Control);
        Assert.True(control.Typed);
        Assert.Equal(instructionFormat, control.TypedFormat);
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
