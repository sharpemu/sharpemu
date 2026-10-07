// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// Device-address loads, stores and atomics compiled through a compile request and run
// against a page table on the device.
public sealed class DeviceAddressShaderTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong PageSize = Gen5SpirvTranslator.DeviceAddressPageSize;
    private const ulong GuestBase = 64 * PageSize;
    private const ulong TableEntries = 4096;
    private const uint ResultRegister = 4;
    private const uint ResultBytes = 256;
    private const uint AddressLow = 0;
    private const uint AddressHigh = 1;
    private const uint OffsetRegister = 3;

    [Fact]
    public void PageBits_MatchTheHostCache() =>
        Assert.Equal(GuestBufferCache.CachingPageBits, Gen5SpirvTranslator.DeviceAddressPageBits);

    [Fact]
    public void GlobalLoadThroughThePageTable_ReturnsTheGuestBytes()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var program = LoadProgram("GlobalLoadDword", offset: 8);
        var run = new Run(vulkan, program);
        var data = run.MapPage(GuestBase, Pattern(64));
        run.Dispatch(GuestBase);
        Assert.Equal(ReadWord(data, 8), run.ResultWord(0));
        Assert.Equal(0u, run.FaultWord(GuestBase));
        run.Finish(output, nameof(GlobalLoadThroughThePageTable_ReturnsTheGuestBytes));
    }

    [Fact]
    public void MissingPage_RecordsAFaultAndReadsZero()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var run = new Run(vulkan, LoadProgram("GlobalLoadDword", offset: 8));
        run.Dispatch(GuestBase);
        Assert.Equal(0u, run.ResultWord(0));
        Assert.Equal(1u << (int)((GuestBase >> Gen5SpirvTranslator.DeviceAddressPageBits) & 31), run.FaultWord(GuestBase));
        run.Finish(output, nameof(MissingPage_RecordsAFaultAndReadsZero));
    }

    [Fact]
    public void MissingPagesSharingOneBitmapWord_AllRecordFaults()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        // Each lane loads from its own page; the 32 pages share one fault word.
        var program = Program(
            Vop2(0, "VLshlrevB32", OffsetRegister, Operand(Gen5SpirvTranslator.DeviceAddressPageBits), Gen5Operand.Vector(0)),
            GlobalMemory(4, "GlobalLoadDword", AddressLow, OffsetRegister, 1, 1),
            Vop2(12, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(16, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 1, offsetEnabled: true, vectorAddress: 5),
            EndProgram(24));
        var run = new Run(vulkan, program, threadCount: 32);
        run.Dispatch(GuestBase);
        Assert.Equal(uint.MaxValue, run.FaultWord(GuestBase));
        for (uint lane = 0; lane < 32; lane++)
        {
            Assert.Equal(0u, run.ResultWord(lane * 4));
        }

        run.Finish(output, nameof(MissingPagesSharingOneBitmapWord_AllRecordFaults));
    }

    [Fact]
    public void AddressAboveTheTable_ReadsZeroWithoutTouchingEitherBuffer()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var run = new Run(vulkan, LoadProgram("GlobalLoadDword", offset: 0));
        run.Dispatch(0x0000_1000_0000_0000);
        Assert.Equal(0u, run.ResultWord(0));
        Assert.All(run.FaultWords(), word => Assert.Equal(0u, word));
        run.Finish(output, nameof(AddressAboveTheTable_ReadsZeroWithoutTouchingEitherBuffer));
    }

    [Fact]
    public void LoadCrossingAPageBoundary_ResolvesBothPages()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var program = LoadProgram("GlobalLoadUshort", offset: (int)PageSize - 1);
        var run = new Run(vulkan, program);
        var first = run.MapPage(GuestBase, Pattern((int)PageSize, seed: 1));
        var second = run.MapPage(GuestBase + PageSize, Pattern(64, seed: 2));
        run.Dispatch(GuestBase);
        Assert.Equal((uint)(first[PageSize - 1] | (second[0] << 8)), run.ResultWord(0));

        var missingSecond = new Run(vulkan, program);
        var only = missingSecond.MapPage(GuestBase, Pattern((int)PageSize, seed: 3));
        missingSecond.Dispatch(GuestBase);
        Assert.Equal((uint)only[PageSize - 1], missingSecond.ResultWord(0));
        Assert.NotEqual(0u, missingSecond.FaultWord(GuestBase + PageSize));
        run.Finish(output, nameof(LoadCrossingAPageBoundary_ResolvesBothPages));
        missingSecond.Finish(output, nameof(LoadCrossingAPageBoundary_ResolvesBothPages));
    }

    [Fact]
    public void MisalignedSubwordLoads_MergeTwoDwords()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var run = new Run(vulkan, LoadProgram("GlobalLoadUshort", offset: 3));
        var data = run.MapPage(GuestBase, Pattern(64));
        run.Dispatch(GuestBase);
        Assert.Equal((uint)(data[3] | (data[4] << 8)), run.ResultWord(0));
        run.Finish(output, nameof(MisalignedSubwordLoads_MergeTwoDwords));
    }

    [Fact]
    public void GpuComputedAddress_LoadsThroughThePageTable()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        // The full address pair is per lane: base + lane * 4 with a carried high dword.
        var program = Program(
            Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            Vop2(4, "VAddCoU32", 6, Gen5Operand.Scalar(AddressLow), Gen5Operand.Vector(5)),
            Vop2(8, "VAddCoCiU32", 7, Gen5Operand.Scalar(AddressHigh), Operand(0)),
            GlobalMemory(12, "GlobalLoadDword", NullOperand, 6, 1, 1),
            BufferAccess(20, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 1, offsetEnabled: true, vectorAddress: 5),
            EndProgram(28));
        var run = new Run(vulkan, program, threadCount: 8);
        Assert.Contains(run.Plan.DeviceAddressRanges, range => !range.Plannable);
        var data = run.MapPage(GuestBase, Pattern(64));
        run.Dispatch(GuestBase);
        for (uint lane = 0; lane < 8; lane++)
        {
            Assert.Equal(ReadWord(data, (int)lane * 4), run.ResultWord(lane * 4));
        }

        run.Finish(output, nameof(GpuComputedAddress_LoadsThroughThePageTable));
    }

    [Fact]
    public void GlobalStoreThroughThePageTable_Lands()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var run = new Run(vulkan, StoreProgram("GlobalStoreDword", offset: 8, value: 0xCAFE_F00D));
        Assert.Single(run.Plan.WrittenRangeSlotByHandle);
        run.MapPage(GuestBase, new byte[64]);
        run.Dispatch(GuestBase, writtenRange: (GuestBase, 64));
        Assert.Equal(0xCAFE_F00Du, run.PageWord(GuestBase, 8));
        run.Finish(output, nameof(GlobalStoreThroughThePageTable_Lands));
    }

    [Fact]
    public void AtomicThroughThePageTable_ReturnsTheOldValue()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var run = new Run(vulkan, AtomicProgram(offset: 4, value: 5));
        var initial = new byte[64];
        WriteWord(initial, 4, 100);
        run.MapPage(GuestBase, initial);
        run.Dispatch(GuestBase, writtenRange: (GuestBase, 64));
        Assert.Equal(100u, run.ResultWord(0));
        Assert.Equal(105u, run.PageWord(GuestBase, 4));
        run.Finish(output, nameof(AtomicThroughThePageTable_ReturnsTheOldValue));
    }

    [Fact]
    public void OutOfRangeWrite_IsSkippedWhenTheNeighbouringPageIsMapped()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var run = new Run(vulkan, StoreProgram("GlobalStoreDword", offset: (int)PageSize, value: 0xDEAD_BEEF));
        run.MapPage(GuestBase, new byte[64]);
        var neighbour = run.MapPage(GuestBase + PageSize, Pattern(64, seed: 7));
        run.Dispatch(GuestBase, writtenRange: (GuestBase, PageSize));
        Assert.Equal(ReadWord(neighbour, 0), run.PageWord(GuestBase + PageSize, 0));
        run.Finish(output, nameof(OutOfRangeWrite_IsSkippedWhenTheNeighbouringPageIsMapped));
    }

    [Fact]
    public void SkippedAtomic_LeavesTheDestinationRegisterUnchanged()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var run = new Run(vulkan, AtomicProgram(offset: 64, value: 5, preset: 0x1234));
        var initial = new byte[128];
        WriteWord(initial, 64, 100);
        run.MapPage(GuestBase, initial);
        run.Dispatch(GuestBase, writtenRange: (GuestBase, 64));
        Assert.Equal(0x1234u, run.ResultWord(0));
        Assert.Equal(100u, run.PageWord(GuestBase, 64));
        run.Finish(output, nameof(SkippedAtomic_LeavesTheDestinationRegisterUnchanged));
    }

    [Theory]
    [InlineData("GlobalStoreDword", 60, 64ul, true)]
    [InlineData("GlobalStoreDword", 0, 4ul, true)]
    [InlineData("GlobalStoreDwordx2", 0, 4ul, false)]
    [InlineData("GlobalStoreDwordx2", 60, 64ul, false)]
    public void WrittenRangeBounds_AdmitOnlyWritesInsideTheRange(string opcode, int offset, ulong rangeSize, bool lands)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var run = new Run(vulkan, StoreProgram(opcode, offset, value: 0x5A5A_0001));
        run.MapPage(GuestBase, new byte[128]);
        run.Dispatch(GuestBase, writtenRange: (GuestBase, rangeSize));
        Assert.Equal(lands ? 0x5A5A_0001u : 0u, run.PageWord(GuestBase, (ulong)offset));
        if (opcode.EndsWith("x2", StringComparison.Ordinal))
        {
            Assert.Equal(lands ? 0x5A5A_0002u : 0u, run.PageWord(GuestBase, (ulong)offset + 4));
        }

        run.Finish(output, nameof(WrittenRangeBounds_AdmitOnlyWritesInsideTheRange));
    }

    [Fact]
    public void WriteAddressNearTheTopOfTheLowSpace_DoesNotWrap()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        // A 32-bit sum of base and offset would wrap to a small address; the 64-bit check does not.
        const ulong topBase = 0xFFFF_FFF0;
        var skipped = new Run(vulkan, StoreProgram("GlobalStoreDwordx2", offset: 12, value: 0x7777_0001), tableEntries: (topBase >> Gen5SpirvTranslator.DeviceAddressPageBits) + 1);
        skipped.MapPage(topBase & ~(PageSize - 1), new byte[(int)PageSize]);
        skipped.Dispatch(topBase, writtenRange: (topBase, 16));
        Assert.Equal(0u, skipped.PageWord(topBase, 12));

        var lands = new Run(vulkan, StoreProgram("GlobalStoreDword", offset: 12, value: 0x7777_0001), tableEntries: (topBase >> Gen5SpirvTranslator.DeviceAddressPageBits) + 1);
        lands.MapPage(topBase & ~(PageSize - 1), new byte[(int)PageSize]);
        lands.Dispatch(topBase, writtenRange: (topBase, 16));
        Assert.Equal(0x7777_0001u, lands.PageWord(topBase, 12));
        skipped.Finish(output, nameof(WriteAddressNearTheTopOfTheLowSpace_DoesNotWrap));
        lands.Finish(output, nameof(WriteAddressNearTheTopOfTheLowSpace_DoesNotWrap));
    }

    // ---- programs ----

    // v3 = 0; v1 = load(s[0:1] + v3 + offset); result[0] = v1.
    private static Gen5ShaderProgram LoadProgram(string opcode, int offset) => Program(
        MoveVector(0, OffsetRegister, 0),
        GlobalMemory(4, opcode, AddressLow, OffsetRegister, 1, 1, offset),
        BufferAccess(12, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 1),
        EndProgram(20));

    // v3 = 0; v1 (and v2) = value (+1); store to s[0:1] + v3 + offset.
    private static Gen5ShaderProgram StoreProgram(string opcode, int offset, uint value)
    {
        var dwords = opcode.EndsWith("x2", StringComparison.Ordinal) ? 2u : 1u;
        return Program(
            MoveVector(0, OffsetRegister, 0),
            MoveVector(8, 1, value),
            MoveVector(16, 2, value + 1),
            GlobalMemory(24, opcode, AddressLow, OffsetRegister, 1, 1, offset, dwords),
            EndProgram(32));
    }

    // v3 = 0; v2 = value; v1 = preset; v1 = atomic_add(s[0:1] + offset, v2) glc; result[0] = v1.
    private static Gen5ShaderProgram AtomicProgram(int offset, uint value, uint preset = 0) => Program(
        MoveVector(0, OffsetRegister, 0),
        MoveVector(8, 2, value),
        MoveVector(16, 1, preset),
        GlobalMemory(24, "GlobalAtomicAdd", AddressLow, OffsetRegister, 1, 2, offset, glc: true),
        BufferAccess(32, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 1),
        EndProgram(40));

    private static byte[] Pattern(int length, uint seed = 5) => ImageTestHarness.Pattern(length, seed);

    private static uint ReadWord(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static void WriteWord(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    // One compiled program with its harness, page table, fault buffer and result buffer.
    private sealed class Run
    {
        private readonly ImageTestHarness _harness;
        private readonly LayoutComputeRunner _runner;
        private readonly GpuBuffer _result;
        private readonly GpuBuffer _fault;
        private readonly Dictionary<ulong, GpuBuffer> _pages = [];
        private readonly ulong _tableEntries;
        private GpuBuffer? _pageTable;

        public Run(HeadlessVulkan vulkan, Gen5ShaderProgram program, uint threadCount = 1, ulong tableEntries = TableEntries)
        {
            var (plan, resources, layout) = Prepare(program);
            Plan = plan;
            Request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = threadCount, ThreadCountX = threadCount };
            Assert.True(resources.Info.UsesDeviceAddresses);
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request, out var shader, out var error), error);
            _harness = new ImageTestHarness(vulkan);
            _runner = new LayoutComputeRunner(_harness, Request, shader.Spirv);
            _result = _runner.CreateBuffer(ResultBytes);
            _fault = _runner.CreateBuffer(tableEntries / 8);
            _tableEntries = tableEntries;
        }

        public ShaderResourcePlan Plan { get; }

        public ShaderCompileRequest Request { get; }

        public byte[] MapPage(ulong guestPage, byte[] bytes)
        {
            _pages[guestPage] = _runner.CreateBuffer(bytes, PageSize);
            return bytes;
        }

        public void Dispatch(ulong guestAddress, (ulong Base, ulong Size)? writtenRange = null)
        {
            _pageTable = _runner.CreatePageTable(_tableEntries, _pages.Select(page => (page.Key, page.Value, 0ul)));
            var registers = new uint[256];
            registers[AddressLow] = (uint)guestAddress;
            registers[AddressHigh] = (uint)(guestAddress >> 32);
            registers[ResultRegister + 2] = ResultBytes;
            var table = new uint[Math.Max(Plan.FlattenedTableReservedCount, 1)];
            if (writtenRange is { } range)
            {
                var slot = Plan.WrittenRangeSlotByHandle.Values.Single();
                table[slot] = (uint)range.Base;
                table[slot + 1] = (uint)(range.Base >> 32);
                table[slot + 2] = (uint)range.Size;
            }

            var bound = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
            {
                [DescriptorBindingKind.Buffers] = [_result],
                [DescriptorBindingKind.DeviceAddressPageTable] = [_pageTable],
                [DescriptorBindingKind.FaultBuffer] = [_fault],
            };
            _harness.Run(() => _runner.Dispatch(registers, bound, 1, flattenedTable: table));
        }

        public uint ResultWord(uint offset) => ReadWord(_runner.ReadBack(_result, 0, ResultBytes), (int)offset);

        public uint PageWord(ulong guestAddress, ulong offset)
        {
            var page = guestAddress & ~(PageSize - 1);
            var inPage = guestAddress - page + offset;
            return ReadWord(_runner.ReadBack(_pages[page], 0, _pages[page].Size), (int)inPage);
        }

        public uint FaultWord(ulong guestAddress)
        {
            var pageIndex = guestAddress >> Gen5SpirvTranslator.DeviceAddressPageBits;
            return ReadWord(_runner.ReadBack(_fault, 0, _fault.Size), (int)(pageIndex / 32) * 4);
        }

        public uint[] FaultWords()
        {
            var bytes = _runner.ReadBack(_fault, 0, _fault.Size);
            var words = new uint[bytes.Length / 4];
            for (var index = 0; index < words.Length; index++)
            {
                words[index] = ReadWord(bytes, index * 4);
            }

            return words;
        }

        public void Finish(ITestOutputHelper output, string name)
        {
            _harness.AssertNoValidationMessages();
            _runner.Dispose();
            _harness.Dispose();
            output.WriteLine($"Verified {name} on {_harness.Vulkan.DeviceName}; validation={_harness.Vulkan.ValidationEnabled}.");
        }
    }
}
