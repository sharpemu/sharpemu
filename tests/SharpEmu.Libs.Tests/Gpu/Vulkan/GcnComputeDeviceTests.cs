// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class GcnComputeDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    public void Address64LoadsIncludeBothCarriesAndIgnoreDescriptorSize(uint count)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        uint[] words = [0x7E0002FF, 0xFFFFFFF0, 0x7E0202FF, 14,
            count == 1 ? 0xE030812Cu : 0xE038812Cu, 0x08010400,
            count == 1 ? 0xE0700000u : 0xE0780000u, 0x80030400, 0xBF810000];
        Assert.True(GcnShaderDecoder.TryDecode(new CodeMemory(words), CodeMemory.Address, (uint)words.Length * 4,
            out var original, out var error), error);
        Assert.True(GcnStorageImageLowering.TryLower(original, out var program, out error), error);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, userDataCount: 16);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        const ulong guestBase = 0x11_0000_0000;
        var sourceBytes = new byte[(int)Gen5SpirvTranslator.DeviceAddressPageSize];
        uint[] expected = [0x11223344, 0x55667788, 0x99AABBCC, 0xDDEEFF00];
        for (var index = 0; index < expected.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(sourceBytes.AsSpan(0x13C + index * 4), expected[index]);
        var source = runner.CreateBuffer(sourceBytes);
        var table = runner.CreatePageTable((guestBase >> Gen5SpirvTranslator.DeviceAddressPageBits) + 1,
            [(guestBase, source, 0UL)]);
        var output = runner.CreateBuffer(count * 4);
        var userData = new uint[16];
        userData[4] = 0xFFFFFFF0;
        userData[5] = 1;
        userData[8] = 0x30;
        userData[14] = count * 4;
        harness.Run(() => runner.Dispatch(userData, new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = [output],
            [DescriptorBindingKind.DeviceAddressPageTable] = [table],
        }, 1));
        Assert.Equal(MemoryMarshal.AsBytes<uint>(expected.AsSpan(0, (int)count)).ToArray(), runner.ReadBack(output, 0, count * 4));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(1u, 1u, false)]
    [InlineData(1u, 1u, true)]
    [InlineData(17u, 2u, false)]
    [InlineData(17u, 2u, true)]
    public void UnfilteredFloatImageLoadsRespectPow2PaddedSlices(uint height, uint layers, bool pow2)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var y = height - 1;
        var layer = layers - 1;
        uint[] words = [0x7E0202FF, y, 0x7E0402FF, layer,
            0xF0000300 | (layers > 1 ? 1u << 14 : 0), 0x00020400,
            (26u << 25) | (10u << 17) | 131u, // lane * 8
            0xE0741000, 0x8004040A, 0xBF810000];
        Assert.True(GcnShaderDecoder.TryDecode(new CodeMemory(words), CodeMemory.Address, (uint)words.Length * 4,
            out var raw, out var error), error);
        Assert.True(GcnStorageImageLowering.TryLower(raw, out var program, out error), error);
        var userData = new uint[20];
        userData[8] = 0x10;
        userData[9] = (11u << 20) | (7u << 26);
        userData[10] = (height - 1) << 14;
        userData[11] = ((layers > 1 ? 13u : 9u) << 28) | (13u << 20) | (pow2 ? 1u << 25 : 0) | 0xFAC;
        userData[12] = (7u << 13) | (layers - 1);
        userData[13] = (layers - 1) << 13;
        userData[16] = 0x2000;
        userData[17] = 8u << 16;
        userData[18] = 64;
        userData[19] = (11u << 15) | (4u << 12) | 0xFAC;
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0xBB124, 0, 20,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize,
            normalizeImageDescriptor: GcnImageDescriptor.Normalize);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 20),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64, ThreadCountX = 64, WaveSize = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var inactiveImages = BindInactiveNativeImages(harness, request, snapshot);
        var physicalHeight = pow2 ? System.Numerics.BitOperations.RoundUpToPowerOf2(height) : height;
        physicalHeight = (physicalHeight + 7) & ~7u;
        var sourceBytes = new byte[8 * physicalHeight * layers * 8];
        var element = layer * 8 * physicalHeight + (y / 8) * 64 +
            ((y & 1) << 1) + ((y & 2) << 2) + ((y & 4) << 3);
        float[] expected = [0.25f, 2.5f];
        MemoryMarshal.AsBytes<float>(expected).CopyTo(sourceBytes.AsSpan((int)element * 8));
        var source = runner.CreateBuffer(sourceBytes);
        var output = runner.CreateBuffer(512);
        harness.Run(() => runner.Dispatch(userData,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [source, output] }, 1, boundImages: inactiveImages));
        Assert.Equal(MemoryMarshal.AsBytes<float>(expected).ToArray(), runner.ReadBack(output, 0, 8));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(0u, true, true, 0u, 64u)]
    [InlineData(0u, true, true, 32u, 32u)]
    [InlineData(0x20C6E32Cu, true, true, 0u, 64u)]
    [InlineData(0x20C6E32Cu, false, true, 0u, 64u)]
    [InlineData(0x20C6E32Cu, true, false, 0u, 64u)]
    public void ScatterCopyPreservesLargeOffsetsAndChecksTheBoundView(
        uint destinationWord, bool formatted, bool paired, uint pushCursor, uint boundWords)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        // The capture regression needs a >2 GiB allocation; keep ordinary CI runs small.
        if (destinationWord != 0 && Environment.GetEnvironmentVariable("SHARPEMU_TEST_LARGE_BUFFERS") != "1") return;
        uint[] words = [0xBEEB03FF, 12, 0x7E020210, 0xE0082000, 0x80020101, 0xBF8C0F70,
            0x7D8C0103, 0xBE80246A, 0x4A040500, 0xBF880008, 0xE0002000, 0x80010202,
            0x4A000300, 0x7D88000E, 0xBE80246A, 0xBF8C0F70, 0xE0102000, 0x80030200, 0xBF810000];
        if (!formatted) words[^3] = 0xE0702000;
        Assert.True(GcnShaderDecoder.TryDecode(new CodeMemory(words), CodeMemory.Address, (uint)words.Length * 4,
            out var raw, out var error), error);
        Assert.True(GcnStorageImageLowering.TryLower(raw, out var program, out error), error);
        uint[] userData = [0, 0, 0, 0, 0x1000, 4u << 16, 64, (4u << 15) | (4u << 12) | 4,
            0x2000, 12u << 16, 1, (13u << 15) | (4u << 12) | 0x1AC,
            0x3000, 4u << 16, destinationWord + 64, (4u << 15) | (4u << 12) | 4];
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0xBB123, 0, 16,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize,
            normalizeImageDescriptor: GcnImageDescriptor.Normalize);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false, pushCursor, usesBufferLengths: true);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, ThreadCountX = 64, WaveSize = 64, HostComputeSubgroup32 = paired,
            ComputeSystemRegisters = new Gen5ComputeSystemRegisters(16, null, null, null),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var command = runner.CreateBuffer(MemoryMarshal.AsBytes<uint>([destinationWord, 0, 63]));
        var values = Enumerable.Range(1, 64).Select(i => (uint)i).ToArray();
        var data = runner.CreateBuffer(MemoryMarshal.AsBytes<uint>(values));
        using var output = new GpuBuffer(harness.Device, harness.Scheduler, GpuBufferUsage.DeviceLocal, 0,
            GpuBuffer.AllFlags, (destinationWord + 64UL) * 4);
        harness.Run(() =>
        {
            output.Fill(destinationWord * 4UL, 256, 0xCDCDCDCD);
            runner.Dispatch(userData, new Dictionary<DescriptorBindingKind, GpuBuffer[]>
                { [DescriptorBindingKind.Buffers] = [command, data, output] }, 1,
                bufferRanges: [12, 256, (destinationWord + (ulong)boundWords) * 4]);
        });
        var actual = runner.ReadBack(output, destinationWord * 4UL, 256);
        for (var index = (int)boundWords; index < values.Length; index++) values[index] = 0xCDCDCDCD;
        Assert.Equal(MemoryMarshal.AsBytes<uint>(values).ToArray(), actual);
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void GcnOpcodesExecuteThroughTheSharedWave64VulkanBackend()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        static uint Vop2(uint op, uint dest, uint src0, uint src1) => (op << 25) | (dest << 17) | (src1 << 9) | src0;
        uint[] words =
        [
            Vop2(26, 10, 132, 0), // v10 = lane * 16
            0x7E040289, 0x7E060291, // v2 = 9, v3 = 17
            0xBEEA0480, // VCC = 0
            Vop2(0, 4, 258, 3),
            0xE0701000, 0x8001040A,
            0xBEEA04C1, // VCC = all ones
            Vop2(0, 4, 258, 3),
            0xE0701004, 0x8001040A,
            0x7E060288, // v3 = 8
            Vop2(30, 4, 132, 3),
            0xE0701008, 0x8001040A,
            Vop2(1, 8, 260, 128), // s8 = readlane(v4, 0)
            0x7E0A0208, // v5 = s8
            0xE070100C, 0x8001050A,
            0xBF810000,
        ];
        var memory = new CodeMemory(words);
        Assert.True(GcnShaderDecoder.TryDecode(memory, CodeMemory.Address, (uint)words.Length * 4,
            out var program, out var error), error);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, ThreadCountX = 64, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        const uint size = 64 * 16;
        var output = runner.CreateBuffer(size);
        var registers = new uint[256];
        registers[6] = size;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var actual = runner.ReadBack(output, 0, size);
        uint[] expected = [9, 17, 0xF00, 0xF00];
        for (var lane = 0; lane < 64; lane++)
            for (var component = 0; component < expected.Length; component++)
                Assert.Equal(expected[component], BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(lane * 16 + component * 4)));
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void FormattedGcnBufferLoadUsesTheConvertedDescriptorOnTheDevice()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        uint[] words =
        [
            (26u << 25) | (10u << 17) | 132u,
            0xE00C0000, 0x80010400, // buffer_load_format_xyzw v[4:7], s[4:7]
            0xE0781000, 0x8002040A, // buffer_store_dwordx4 v[4:7], v10, s[8:11]
            0xBF810000,
        ];
        Assert.True(GcnShaderDecoder.TryDecode(new CodeMemory(words), CodeMemory.Address, (uint)words.Length * 4,
            out var program, out var error), error);
        uint[] userData = [0, 0, 0, 0, 0x1000, 16u << 16, 1, (14u << 15) | (7u << 12) | 0xFAC,
            0x2000, 16u << 16, 64, (14u << 15) | (4u << 12) | 0xFAC];
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 1234, 0, 12,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize,
            normalizeImageDescriptor: GcnImageDescriptor.Normalize);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 12),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64, ThreadCountX = 64, WaveSize = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var source = runner.CreateBuffer(16);
        var output = runner.CreateBuffer(1024);
        float[] values = [0.25f, -2.5f, 123.75f, 1f];
        var sourceBytes = new byte[16];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(sourceBytes.AsSpan(index * 4), values[index]);
        source.Write(0, sourceBytes);
        harness.Run(() => runner.Dispatch(userData,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [source, output] }, 1));
        var actual = runner.ReadBack(output, 0, 1024);
        for (var lane = 0; lane < 64; lane++)
            Assert.Equal(sourceBytes, actual.AsSpan(lane * 16, 16).ToArray());
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(8u, 4u, 7u)]
    [InlineData(13u, 4u, 7u)]
    [InlineData(31u, 4u, 7u)]
    [InlineData(8u, 10u, 0u)]
    [InlineData(13u, 10u, 0u)]
    [InlineData(31u, 10u, 0u)]
    [InlineData(13u, 10u, 9u)]
    [InlineData(13u, 11u, 7u)]
    [InlineData(13u, 12u, 7u)]
    [InlineData(13u, 14u, 7u)]
    public void StorageImageStoreUsesLegacyPitchAndMicrotilesAndPreservesBufferGuards(uint tile, uint format, uint number)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        uint[] words = [0x7E0802FF, 0x3F800000, 0x7E0A02FF, 0x3F000000,
            0x7E0C02FF, 0x3E800000, 0x7E0E02FF, 0x3F400000, 0xF0200F00, 0x00020400, 0xBF810000];
        Assert.True(GcnShaderDecoder.TryDecode(new CodeMemory(words), CodeMemory.Address, (uint)words.Length * 4,
            out var raw, out var error), error);
        Assert.True(GcnStorageImageLowering.TryLower(raw, out var program, out error), error);
        const uint width = 7, height = 5, pitch = 16;
        byte[] pixel = format switch
        {
            4 => [0, 0, 128, 63],
            11 => [0, 0, 128, 63, 0, 0, 0, 63],
            12 => [0, 60, 0, 56, 0, 52, 0, 58],
            14 => [0, 0, 128, 63, 0, 0, 0, 63, 0, 0, 128, 62, 0, 0, 64, 63],
            _ => [255, 128, 64, 191],
        };
        var size = pitch * (tile == 13 ? 8u : height) * (uint)pixel.Length;
        uint[] userData = new uint[16];
        userData[8] = 0x1000;
        userData[9] = (format << 20) | (number << 26);
        userData[10] = (width - 1) | ((height - 1) << 14);
        userData[11] = (9u << 28) | (tile << 20) | 0xFAC;
        userData[12] = (pitch - 1) << 13;
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 5678, 0, 16,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize,
            normalizeImageDescriptor: GcnImageDescriptor.Normalize);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        Assert.Equal(size, Assert.Single(snapshot.Buffers)[2]);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = 8, ThreadCountX = 8, ThreadCountY = 8, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var inactiveImages = BindInactiveNativeImages(harness, request, snapshot);
        var output = runner.CreateBuffer(size);
        var expected = new byte[size];
        Array.Fill(expected, (byte)0xCD);
        output.Write(0, expected);
        harness.Run(() => runner.Dispatch(userData,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1, boundImages: inactiveImages));
        for (var y = 0u; y < height; y++)
            for (var x = 0u; x < width; x++)
            {
                uint element = y * pitch + x;
                if (tile == 13)
                {
                    element = ((y / 8) * (pitch / 8) + x / 8) * 64;
                    for (var bit = 0; bit < 3; bit++)
                        element |= ((x >> bit) & 1) << (bit * 2) | ((y >> bit) & 1) << (bit * 2 + 1);
                }
                pixel.CopyTo(expected.AsSpan((int)element * pixel.Length));
            }
        Assert.Equal(expected, runner.ReadBack(output, 0, size));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(1u, 0u, 1u)]
    [InlineData(2u, 7u, 2u)]
    [InlineData(4u, 7u, 4u)]
    [InlineData(11u, 7u, 8u)]
    [InlineData(12u, 7u, 8u)]
    [InlineData(14u, 7u, 16u)]
    public void ThickVolumeStoresUseAllThreeCoordinatesAndPreservePaddedTexels(uint format, uint number, uint bytes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        uint[] words =
        [
            0x4A080081, // v4 = x + 1
            0x7E080D04, // v4 = float(v4)
            0x100808FF, 0x3D800000, // v4 *= 1/16
            0xF0200100, 0x00020400, 0xBF810000,
        ];
        Assert.True(GcnShaderDecoder.TryDecode(new CodeMemory(words), CodeMemory.Address, (uint)words.Length * 4,
            out var raw, out var error), error);
        Assert.True(GcnStorageImageLowering.TryLower(raw, out var program, out error), error);
        const uint width = 7, height = 5, depth = 3, pitch = 16;
        var size = pitch * 8 * 4 * bytes;
        var userData = new uint[16];
        userData[8] = 0x1000;
        userData[9] = (format << 20) | (number << 26);
        userData[10] = (width - 1) | ((height - 1) << 14);
        userData[11] = (10u << 28) | (19u << 20) | 0xFAC;
        userData[12] = ((pitch - 1) << 13) | (depth - 1);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 5679, 0, 16,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize,
            normalizeImageDescriptor: GcnImageDescriptor.Normalize);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        Assert.Equal(size, Assert.Single(snapshot.Buffers)[2]);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = 8, LocalSizeZ = 4,
            ThreadCountX = 8, ThreadCountY = 8, ThreadCountZ = 4, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var inactiveImages = BindInactiveNativeImages(harness, request, snapshot);
        var output = runner.CreateBuffer(size);
        var expected = new byte[size];
        Array.Fill(expected, (byte)0xCD);
        output.Write(0, expected);
        harness.Run(() => runner.Dispatch(userData,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1, boundImages: inactiveImages));
        for (var z = 0u; z < depth; z++)
            for (var y = 0u; y < height; y++)
                for (var x = 0u; x < width; x++)
                {
                    var element = ((y / 8) * (pitch / 8) + x / 8) * 256;
                    element |= (x & 1) | ((y & 1) << 1) | ((x & 2) << (bytes >= 8 ? 2 : 1)) |
                        ((y & 2) << (bytes >= 4 ? 3 : 2)) | ((z & 1) << (bytes >= 8 ? 2 : bytes == 4 ? 3 : 4)) |
                        ((z & 2) << 4) | ((x & 4) << 4) | ((y & 4) << 5);
                    var destination = expected.AsSpan((int)(element * bytes));
                    destination[..(int)bytes].Clear();
                    var value = (x + 1) / 16f;
                    if (bytes == 1) destination[0] = (byte)MathF.Round(value * 255);
                    else if (format is 2 or 12) BinaryPrimitives.WriteUInt16LittleEndian(destination, BitConverter.HalfToUInt16Bits((Half)value));
                    else BinaryPrimitives.WriteSingleLittleEndian(destination, value);
                }
        Assert.Equal(expected, runner.ReadBack(output, 0, size));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(11u)]
    [InlineData(12u)]
    [InlineData(13u)]
    public void ArrayStoresUseTheViewBaseAndCubeDepthCountsWholeCubes(uint type)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        uint[] words = [0x7E0802FF, 0x3F800000, 0xF0204F00, 0x00020400, 0xBF810000];
        Assert.True(GcnShaderDecoder.TryDecode(new CodeMemory(words), CodeMemory.Address, (uint)words.Length * 4,
            out var raw, out var error), error);
        Assert.True(GcnStorageImageLowering.TryLower(raw, out var program, out error), error);
        const uint pitch = 16, width = 7, physicalLayers = 6, baseArray = 2, lastArray = 4;
        var height = type == 12 ? 1u : 5u;
        const uint size = pitch * 8 * physicalLayers * 4;
        var userData = new uint[16];
        userData[8] = 0x1000;
        userData[9] = (4u << 20) | (7u << 26);
        userData[10] = (width - 1) | ((height - 1) << 14);
        userData[11] = (type << 28) | (13u << 20) | 0xFAC;
        userData[12] = ((pitch - 1) << 13) | (type == 11 ? 0 : physicalLayers - 1);
        userData[13] = baseArray | (lastArray << 13);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 5680, 0, 16,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize,
            normalizeImageDescriptor: GcnImageDescriptor.Normalize);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        Assert.Equal(size, Assert.Single(snapshot.Buffers)[2]);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = 8, LocalSizeZ = type == 12 ? 1u : 4u,
            ThreadCountX = 8, ThreadCountY = 8, ThreadCountZ = type == 12 ? 1u : 4u, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var inactiveImages = BindInactiveNativeImages(harness, request, snapshot);
        var output = runner.CreateBuffer(size);
        var expected = new byte[size];
        Array.Fill(expected, (byte)0xCD);
        output.Write(0, expected);
        harness.Run(() => runner.Dispatch(userData,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1, boundImages: inactiveImages));
        for (var layer = baseArray; layer <= lastArray; layer++)
            for (var y = 0u; y < height; y++)
                for (var x = 0u; x < width; x++)
                {
                    var element = layer * pitch * 8;
                    for (var bit = 0; bit < 3; bit++)
                        element |= ((x >> bit) & 1) << (bit * 2) | ((y >> bit) & 1) << (bit * 2 + 1);
                    BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan((int)element * 4), 1);
                }
        Assert.Equal(expected, runner.ReadBack(output, 0, size));
        harness.AssertNoValidationMessages();
    }

    private static Dictionary<DescriptorBindingKind, DescriptorImageInfo[]> BindInactiveNativeImages(
        ImageTestHarness harness, ShaderCompileRequest request, ResourceSnapshot snapshot)
    {
        var bindings = new Dictionary<DescriptorBindingKind, DescriptorImageInfo[]>();
        foreach (var descriptor in request.Bindings.Descriptors)
        {
            var resourceClass = ImageDescriptorBinding.ResourceClass(descriptor.Kind);
            if (resourceClass == ImageResourceClass.None)
                continue;
            bindings[descriptor.Kind] = descriptor.Resources.Select(resource =>
            {
                Assert.All(snapshot.Images[(int)resource], word => Assert.Equal(0u, word));
                var info = request.Resources.Info.Images[(int)resource];
                var (format, guestFormat) = info.NumericClass switch
                {
                    ImageNumericClass.Float => (Format.R32Sfloat, GuestPixelFormat.Bits32Float),
                    ImageNumericClass.Uint => (Format.R32Uint, GuestPixelFormat.Bits32UInt),
                    ImageNumericClass.Sint => (Format.R32Sint, GuestPixelFormat.Bits32SInt),
                    _ => throw new InvalidOperationException($"Unexpected inactive image class: {info.NumericClass}"),
                };
                var viewType = info.Dimension switch
                {
                    ImageDimension.Dim2D => ImageViewType.Type2D,
                    ImageDimension.Dim2DArray => ImageViewType.Type2DArray,
                    ImageDimension.Dim3D => ImageViewType.Type3D,
                    _ => throw new InvalidOperationException($"Unexpected inactive image dimension: {info.Dimension}"),
                };
                var description = ImageCacheTestSupport.LinearRequest(0x100000, 4, format, guestFormat,
                    viewType == ImageViewType.Type3D ? GuestImageType.Color3D : GuestImageType.Color2D,
                    new Extent3D(1, 1, 1), 1, 4, 1).Description;
                var image = harness.CreateImage(description);
                harness.UploadImage(image, new byte[4], ImageTestHarness.WholeImageCopies(description, 0));
                return new DescriptorImageInfo
                {
                    ImageView = image.GetOrCreateView(ImageViewDescription.Default with
                    {
                        Format = format, Type = viewType,
                        Usage = resourceClass == ImageResourceClass.Storage ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit,
                    }),
                    ImageLayout = ImageLayout.General,
                };
            }).ToArray();
        }
        return bindings;
    }

    private sealed class CodeMemory(uint[] words) : ICpuMemory
    {
        public const ulong Address = 0x100000000;
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address != Address || destination.Length != words.Length * 4) return false;
            for (var index = 0; index < words.Length; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(destination[(index * 4)..], words[index]);
            return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
