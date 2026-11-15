// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A formatted store through a run-time V# branches on the V#'s format; the formats of one store shape share a branch. Each
// format must still store its own encoding: 8 (1), 8_8 (14) and 8_8_8_8 (56) UNORM share the X store's branch, 8 SNORM (2),
// 16 FLOAT (13) and 32 FLOAT (22) do not. Four lanes store 0.5, 1, -1 and NaN, one dword apart, into a page of 0xCC bytes;
// only each element's bytes change.
public sealed class DeviceFormatStoreDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong PageSize = Gen5SpirvTranslator.DeviceAddressPageSize;
    private const ulong GuestBase = 64 * PageSize;
    private const ulong TableEntries = 4096;
    private const int StoreOffset = 512;
    private const int SourceOffset = 1024;
    private static readonly uint[] Values = [0x3F000000, 0x3F800000, 0xBF800000, 0x7FC00000];

    [Theory]
    [InlineData(1u, new uint[] { 0x80, 0xFF, 0x00, 0x00 }, 1)]
    [InlineData(14u, new uint[] { 0x80, 0xFF, 0x00, 0x00 }, 1)]
    [InlineData(56u, new uint[] { 0x80, 0xFF, 0x00, 0x00 }, 1)]
    [InlineData(2u, new uint[] { 0x40, 0x7F, 0x81, 0x00 }, 1)]
    [InlineData(13u, new uint[] { 0x3800, 0x3C00, 0xBC00 }, 2)]
    [InlineData(22u, new uint[] { 0x3F000000, 0x3F800000, 0xBF800000, 0x7FC00000 }, 4)]
    public void FormatsStoreTheirOwnEncodingThroughARuntimeDescriptor(uint format, uint[] expected, int elementBytes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Vop2(0, "VAddU32", 2, Operand((uint)GuestBase), Gen5Operand.Vector(0)),
            ReadFirstLane(4, 12, 2),
            MoveScalar(8, 13, (uint)(GuestBase >> 32)),
            ScalarLoad(12, 12, 20, count: 4),
            Vop2(20, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            GlobalMemory(24, "GlobalLoadDword", 0, 5, 8, 8, offset: SourceOffset),
            BufferAccess(32, "BufferStoreFormatX", 20, dwords: 1, vectorData: 8, offsetEnabled: true, vectorAddress: 5),
            EndProgram(40));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 4, ThreadCountX = 4, ScratchDwords = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var page = Enumerable.Repeat((byte)0xCC, (int)PageSize).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0), (uint)GuestBase + StoreOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), (uint)(GuestBase >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(12), 4 | (5 << 3) | (6 << 6) | (7 << 9) | (format << 12));
        for (var lane = 0; lane < Values.Length; lane++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(SourceOffset + lane * 4), Values[lane]);
        }

        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var pageBuffer = runner.CreateBuffer(page, PageSize);
        var fault = runner.CreateBuffer(TableEntries / 8 + 64);
        var table = runner.CreatePageTable(TableEntries, [(GuestBase, pageBuffer, 0ul)]);
        var registers = new uint[256];
        registers[0] = (uint)GuestBase;
        registers[1] = (uint)(GuestBase >> 32);
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]>
            {
                [DescriptorBindingKind.DeviceAddressPageTable] = [table],
                [DescriptorBindingKind.FaultBuffer] = [fault],
            }, 1, flattenedTable: new uint[Math.Max(plan.FlattenedTableReservedCount, 1)]));
        var stored = runner.ReadBack(pageBuffer, 0, PageSize);
        harness.AssertNoValidationMessages();
        for (var lane = 0; lane < expected.Length; lane++)
        {
            var dword = BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(StoreOffset + lane * 4));
            var elementMask = elementBytes == 4 ? uint.MaxValue : (1u << (elementBytes * 8)) - 1;
            Assert.Equal(expected[lane], dword & elementMask);
            Assert.Equal(0xCCCCCCCCu & ~elementMask, dword & ~elementMask);
        }

        output.WriteLine($"Verified format {format} on {vulkan!.DeviceName}.");
    }
}
