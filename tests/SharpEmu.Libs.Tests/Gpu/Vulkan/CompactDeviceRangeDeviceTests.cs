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

// A linked program reads consecutive dwords through the page table as one range, which resolves its first and last dword
// only. S_LOAD_DWORDX8 starts 20 bytes before a page end, so dwords 0-4 lie in the first page and 5-7 in the next;
// BUFFER_LOAD_DWORDX4 through a run-time V# does the same per lane at 4-byte steps. With the next page mapped every dword
// reads its bytes; without it the dwords there read zero and its page is reported missing. With the inline range, through
// the shared load function (SharedDeviceLoads), and through one shared function per range (SharedDeviceRanges).
[Collection(SharedDeviceLoadState.Name)]
public sealed class CompactDeviceRangeDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong PageSize = Gen5SpirvTranslator.DeviceAddressPageSize;
    private const ulong GuestBase = 64 * PageSize;
    private const ulong DataAddress = GuestBase + PageSize - 20;
    private const ulong TableEntries = 4096;
    private const uint ResultRegister = 4;
    private const uint Lanes = 8;
    private const uint ResultBytes = Lanes * 64;

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void RangesAcrossAPageEndReadEachDwordFromItsPage(bool secondPageMapped, bool sharedLoads, bool sharedRanges)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var instructions = new List<Gen5ShaderInstruction>
        {
            MoveScalar(0, 14, 0x2000), MoveScalar(4, 15, 0), MoveScalar(8, 24, 1),
            Sop1(12, "SSwappcB64", 14, Gen5Operand.Scalar(14)),
            Vop2(16, "VAddU32", 2, Operand((uint)GuestBase), Gen5Operand.Vector(0)),
            ReadFirstLane(20, 12, 2),
            MoveScalar(24, 13, (uint)(GuestBase >> 32)),
            ScalarLoad(28, 12, 20, count: 4),
            ScalarLoad(36, 12, 26, count: 2, immediateOffset: 16),
            ScalarLoad(44, 26, 32, count: 8),
            Vop2(52, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(56, "BufferLoadDwordx4", 20, dwords: 4, vectorData: 8, offsetEnabled: true, vectorAddress: 5),
        };
        uint pc = 64;
        for (uint register = 0; register < 8; register++, pc += 4)
        {
            instructions.Add(MoveVectorFromScalar(pc, 12 + register, 32 + register));
        }

        instructions.Add(Vop2(pc, "VLshlrevB32", 6, Operand(6), Gen5Operand.Vector(0)));
        pc += 4;
        for (uint part = 0; part < 3; part++, pc += 8)
        {
            instructions.Add(BufferAccess(pc, "BufferStoreDwordx4", ResultRegister, offset: (int)(part * 16), dwords: 4,
                vectorData: 8 + part * 4, offsetEnabled: true, vectorAddress: 6));
        }

        instructions.Add(EndProgram(pc));
        var function = Program(Sop1(0, "SSetpcB64", 0, Gen5Operand.Scalar(14)));
        var program = Gen5ShaderCallLinker.Link(Program(instructions.ToArray()),
            [new(12, 14, 24, 14, 16, [new ShaderCallTarget(0x2000, 1, function)])]);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = Lanes, ThreadCountX = Lanes, ScratchDwords = 32 };
        var (previous, previousRanges) = (Gen5SpirvTranslator.SharedDeviceLoads, Gen5SpirvTranslator.SharedDeviceRanges);
        Gen5SpirvShader shader;
        string error;
        try
        {
            Gen5SpirvTranslator.SharedDeviceLoads = sharedLoads;
            Gen5SpirvTranslator.SharedDeviceRanges = sharedRanges;
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out shader, out error), error);
        }
        finally
        {
            (Gen5SpirvTranslator.SharedDeviceLoads, Gen5SpirvTranslator.SharedDeviceRanges) = (previous, previousRanges);
        }

        var first = ImageTestHarness.Pattern((int)PageSize, 41);
        var second = ImageTestHarness.Pattern((int)PageSize, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(0), (uint)DataAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(4), (uint)(DataAddress >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(8), 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(12), 4 | (5 << 3) | (6 << 6) | (7 << 9) | (20u << 12));
        BinaryPrimitives.WriteUInt64LittleEndian(first.AsSpan(16), DataAddress);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ResultBytes);
        var fault = runner.CreateBuffer(TableEntries / 8 + 64);
        var pages = new List<(ulong, GpuBuffer, ulong)> { (GuestBase, runner.CreateBuffer(first, PageSize), 0) };
        if (secondPageMapped) pages.Add((GuestBase + PageSize, runner.CreateBuffer(second, PageSize), 0));
        var registers = new uint[256];
        registers[0] = (uint)GuestBase;
        registers[1] = (uint)(GuestBase >> 32);
        registers[ResultRegister + 2] = ResultBytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]>
            {
                [DescriptorBindingKind.Buffers] = [result],
                [DescriptorBindingKind.DeviceAddressPageTable] = [runner.CreatePageTable(TableEntries, pages)],
                [DescriptorBindingKind.FaultBuffer] = [fault],
            }, 1, flattenedTable: new uint[Math.Max(plan.FlattenedTableReservedCount, 1)],
            callArguments: Gen5ShaderCallLinker.CallEntryArguments(program), callTargets: Gen5ShaderCallLinker.CallTargets(program)));
        var stored = runner.ReadBack(result, 0, ResultBytes);
        var faults = runner.ReadBack(fault, 0, fault.Size);
        harness.AssertNoValidationMessages();

        // The guest dword at a byte offset from DataAddress: from the first page, the second, or zero where unmapped.
        uint Expected(int offset)
        {
            var address = (int)(PageSize - 20) + offset;
            if (address + 4 <= (int)PageSize) return BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(address));
            return secondPageMapped ? BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(address - (int)PageSize)) : 0u;
        }

        uint Stored(uint lane, uint dword) => BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan((int)(lane * 64 + dword * 4)));
        for (uint lane = 0; lane < Lanes; lane++)
        {
            for (uint dword = 0; dword < 4; dword++)
            {
                Assert.Equal(Expected((int)(lane * 4 + dword * 4)), Stored(lane, dword));
            }

            for (uint dword = 0; dword < 8; dword++)
            {
                Assert.Equal(Expected((int)(dword * 4)), Stored(lane, 4 + dword));
            }
        }

        var pageIndex = (GuestBase + PageSize) >> Gen5SpirvTranslator.DeviceAddressPageBits;
        var faultWord = BinaryPrimitives.ReadUInt32LittleEndian(faults.AsSpan((int)(pageIndex / 32) * 4));
        Assert.Equal(secondPageMapped ? 0u : 1u << (int)(pageIndex % 32), faultWord & (1u << (int)(pageIndex % 32)));
        output.WriteLine($"Verified the page-end ranges (second page mapped: {secondPageMapped}) on {vulkan!.DeviceName}.");
    }
}
