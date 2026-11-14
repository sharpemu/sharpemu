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

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedDeviceLoadState
{
    // Tests that set Gen5SpirvTranslator.SharedDeviceLoads run alone: no other test translates while it differs.
    public const string Name = "SharedDeviceLoads";
}

// A linked program's device dword loads compiled with the shared load function off and on
// (SharedDeviceLoads), run on the same pages and lanes; every result and fault word must match. The loads:
// S_LOAD_DWORDX4/X2/X8 through run-time addresses (one at the top of the 48-bit space, which wraps to page 0),
// S_BUFFER_LOAD_DWORDX8, BUFFER_LOAD_DWORDX4 and BUFFER_LOAD_FORMAT_XYZW through a run-time V#. Cases: all 128 unified
// formats; data inside one page, 20 bytes before a mapped and an unmapped second page, num_records inside the lanes, a base
// above the page table; 32 lanes at 7-byte steps.
[Collection(SharedDeviceLoadState.Name)]
public sealed class SharedDeviceLoadDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong PageSize = Gen5SpirvTranslator.DeviceAddressPageSize;
    private const ulong GuestBase = 64 * PageSize;
    private const ulong TableEntries = 4096;
    private const uint ResultRegister = 4;
    private const uint Lanes = 32;
    private const uint ResultBytes = Lanes * 128;

    [Fact]
    public void DeviceLoadsMatchWithAndWithoutTheSharedFunction()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = LinkedProgram();
        var (previous, previousRanges) = (Gen5SpirvTranslator.SharedDeviceLoads, Gen5SpirvTranslator.SharedDeviceRanges);
        Compiled before, after;
        try
        {
            // One call per dword (the range functions off, whatever SHARPEMU_SHARED_DEVICE_RANGES says).
            Gen5SpirvTranslator.SharedDeviceRanges = false;
            Gen5SpirvTranslator.SharedDeviceLoads = false;
            before = Compile(program);
            Gen5SpirvTranslator.SharedDeviceLoads = true;
            after = Compile(program);
        }
        finally
        {
            (Gen5SpirvTranslator.SharedDeviceLoads, Gen5SpirvTranslator.SharedDeviceRanges) = (previous, previousRanges);
        }

        output.WriteLine($"module: {before.Spirv.Length} B -> {after.Spirv.Length} B");
        Assert.True(after.Spirv.Length < before.Spirv.Length, "the loads go through the shared function");
        Compare(vulkan!, before, after);
    }

    // The same program with the shared function, its dword ranges one call per dword and one call per range.
    [Fact]
    public void DeviceRangesMatchWithAndWithoutTheRangeFunctions()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = LinkedProgram();
        var (previousLoads, previousRanges) = (Gen5SpirvTranslator.SharedDeviceLoads, Gen5SpirvTranslator.SharedDeviceRanges);
        Compiled before, after;
        try
        {
            Gen5SpirvTranslator.SharedDeviceLoads = true;
            Gen5SpirvTranslator.SharedDeviceRanges = false;
            before = Compile(program);
            Gen5SpirvTranslator.SharedDeviceRanges = true;
            after = Compile(program);
        }
        finally
        {
            (Gen5SpirvTranslator.SharedDeviceLoads, Gen5SpirvTranslator.SharedDeviceRanges) = (previousLoads, previousRanges);
        }

        output.WriteLine($"module: {before.Spirv.Length} B -> {after.Spirv.Length} B");
        Assert.Contains("loadDeviceDwords8", System.Text.Encoding.ASCII.GetString(after.Spirv));
        Compare(vulkan!, before, after);
    }

    private void Compare(HeadlessVulkan vulkan, Compiled before, Compiled after)
    {
        (ulong Base, uint Records, bool SecondPage)[] cases =
        [
            (GuestBase + 512, 0x10000, true),
            (GuestBase + PageSize - 20, 0x10000, true),
            (GuestBase + PageSize - 20, 0x10000, false),
            (GuestBase + 512, 150, true),
            (0x0000_1000_0000_0000, 0x10000, true),
            (0x0000_FFFF_FFFF_FFF0, 0x10000, true),
        ];
        long dispatches = 0, words = 0, mismatches = 0, live = 0, faulted = 0;
        using var harness = new ImageTestHarness(vulkan);
        var first = ImageTestHarness.Pattern((int)PageSize, 31);
        var second = ImageTestHarness.Pattern((int)PageSize, 32);
        for (uint format = 0; format < 128; format++)
        {
            using var beforeRunner = new LayoutComputeRunner(harness, before.Request, before.Spirv);
            using var afterRunner = new LayoutComputeRunner(harness, after.Request, after.Spirv);
            foreach (var (dataBase, records, secondPage) in cases)
            {
                var page = (byte[])first.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0), (uint)dataBase);
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), (uint)(dataBase >> 32) & 0xFFFF);
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), records);
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(12), 4 | (5 << 3) | (6 << 6) | (7 << 9) | (format << 12));
                BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(16), dataBase);
                var a = Run(harness, beforeRunner, before, page, secondPage ? second : null);
                var b = Run(harness, afterRunner, after, page, secondPage ? second : null);
                dispatches++;
                for (var index = 0; index < a.Result.Length; index++)
                {
                    words++;
                    if (a.Result[index] != b.Result[index]) mismatches++;
                    if (a.Result[index] != 0) live++;
                }

                for (var index = 0; index < a.Faults.Length; index++)
                {
                    if (a.Faults[index] != b.Faults[index]) mismatches++;
                    if (a.Faults[index] != 0) faulted++;
                }

                if (mismatches != 0)
                {
                    var at = Enumerable.Range(0, a.Result.Length).FirstOrDefault(index => a.Result[index] != b.Result[index]);
                    Assert.Fail($"format {format} base 0x{dataBase:X} records {records} second page {secondPage}: {mismatches} differences, " +
                        $"first result word {at}: {a.Result[at]:X8} vs {b.Result[at]:X8}");
                }
            }
        }

        harness.AssertNoValidationMessages();
        output.WriteLine($"{dispatches} dispatch pairs, {words} result words compared ({live} nonzero), {faulted} nonzero fault words, " +
            $"{mismatches} mismatches on {vulkan!.DeviceName}");
        Assert.Equal(0, mismatches);
        Assert.True(live > 0 && faulted > 0, "the cases read data and record faults");
    }

    private sealed record Compiled(ShaderResourcePlan Plan, ShaderCompileRequest Request, byte[] Spirv, ulong[] CallArguments, ulong[] CallTargets);

    private static Compiled Compile(Gen5ShaderProgram program)
    {
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = Lanes, ThreadCountX = Lanes, ScratchDwords = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return new Compiled(plan, request, shader.Spirv, Gen5ShaderCallLinker.CallEntryArguments(program), Gen5ShaderCallLinker.CallTargets(program));
    }

    private static Gen5ShaderProgram LinkedProgram()
    {
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
            ScalarBufferLoad(52, 20, 40, count: 8),
            Vop2(60, "VMulU32U24", 5, Operand(7), Gen5Operand.Vector(0)),
            BufferAccess(64, "BufferLoadDwordx4", 20, dwords: 4, vectorData: 8, offsetEnabled: true, vectorAddress: 5),
            BufferAccess(72, "BufferLoadFormatXyzw", 20, dwords: 4, vectorData: 12, offsetEnabled: true, vectorAddress: 5),
        };
        uint pc = 80;
        for (uint register = 0; register < 16; register++, pc += 4)
        {
            instructions.Add(MoveVectorFromScalar(pc, 16 + register, 32 + register));
        }

        instructions.Add(Vop2(pc, "VLshlrevB32", 6, Operand(7), Gen5Operand.Vector(0)));
        pc += 4;
        for (uint part = 0; part < 6; part++, pc += 8)
        {
            instructions.Add(BufferAccess(pc, "BufferStoreDwordx4", ResultRegister, offset: (int)(part * 16), dwords: 4,
                vectorData: 8 + part * 4, offsetEnabled: true, vectorAddress: 6));
        }

        instructions.Add(EndProgram(pc));
        var caller = Program(instructions.ToArray());
        var function = Program(Sop1(0, "SSetpcB64", 0, Gen5Operand.Scalar(14)));
        var linked = Gen5ShaderCallLinker.Link(caller, [new(12, 14, 24, 14, 16, [new ShaderCallTarget(0x2000, 1, function)])]);
        Assert.True(Gen5ShaderCallLinker.IsLinked(linked));
        return linked;
    }

    private static (uint[] Result, uint[] Faults) Run(ImageTestHarness harness, LayoutComputeRunner runner, Compiled compiled,
        byte[] firstPage, byte[]? secondPage)
    {
        var result = runner.CreateBuffer(ResultBytes);
        var fault = runner.CreateBuffer(TableEntries / 8 + 64);
        var pages = new List<(ulong, GpuBuffer, ulong)> { (GuestBase, runner.CreateBuffer(firstPage, PageSize), 0) };
        if (secondPage is not null) pages.Add((GuestBase + PageSize, runner.CreateBuffer(secondPage, PageSize), 0));
        var table = runner.CreatePageTable(TableEntries, pages);
        var registers = new uint[256];
        registers[0] = (uint)GuestBase;
        registers[1] = (uint)(GuestBase >> 32);
        registers[ResultRegister + 2] = ResultBytes;
        var bound = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = [result],
            [DescriptorBindingKind.DeviceAddressPageTable] = [table],
            [DescriptorBindingKind.FaultBuffer] = [fault],
        };
        harness.Run(() => runner.Dispatch(registers, bound, 1, flattenedTable: new uint[Math.Max(compiled.Plan.FlattenedTableReservedCount, 1)],
            callArguments: compiled.CallArguments, callTargets: compiled.CallTargets));
        return (Words(runner.ReadBack(result, 0, ResultBytes)), Words(runner.ReadBack(fault, 0, fault.Size)));
    }

    private static uint[] Words(byte[] bytes)
    {
        var words = new uint[bytes.Length / 4];
        for (var index = 0; index < words.Length; index++) words[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4));
        return words;
    }
}
