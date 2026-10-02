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

    // An invocation reports the missing pages of one fault-bitmap word when it ends. A page
    // in another word is reported by a later dispatch, once the first word's pages are mapped.
    [Fact]
    public void MissingPagesInTwoBitmapWords_AreReportedOneWordPerDispatch()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        // v1 = load(GuestBase), then v2 = load(GuestBase + 32 pages): the next bitmap word.
        var program = Program(
            Vop2(0, "VAddU32", OffsetRegister, Operand(0), Gen5Operand.Vector(0)),
            GlobalMemory(4, "GlobalLoadDword", AddressLow, OffsetRegister, 1, 1),
            Vop2(12, "VAddU32", 6, Operand(32), Gen5Operand.Vector(0)),
            Vop2(16, "VLshlrevB32", OffsetRegister, Operand(Gen5SpirvTranslator.DeviceAddressPageBits), Gen5Operand.Vector(6)),
            GlobalMemory(20, "GlobalLoadDword", AddressLow, OffsetRegister, 2, 1),
            EndProgram(28));
        var far = GuestBase + 32 * PageSize;
        var run = new Run(vulkan, program);
        run.Dispatch(GuestBase);
        Assert.Equal(1u << (int)((GuestBase >> Gen5SpirvTranslator.DeviceAddressPageBits) & 31), run.FaultWord(GuestBase));
        Assert.Equal(0u, run.FaultWord(far));

        run.MapPage(GuestBase, Pattern((int)PageSize, seed: 51));
        run.Dispatch(GuestBase);
        Assert.Equal(1u << (int)((far >> Gen5SpirvTranslator.DeviceAddressPageBits) & 31), run.FaultWord(far));
        run.Finish(output, nameof(MissingPagesInTwoBitmapWords_AreReportedOneWordPerDispatch));
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

    // A formatted load through a V# the program picks at run time reads its element
    // window through the page table: across a page boundary, from an unmapped page, and
    // cut short by num_records.
    [Fact]
    public void RuntimeFormattedLoad_ReadsItsElementThroughThePageTable()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        const uint Uint32x4 = 75;
        var element = GuestBase + PageSize - 8;

        var both = FormatLoadRun(vulkan);
        var first = both.MapPage(GuestBase, Pattern((int)PageSize, seed: 11));
        var second = both.MapPage(GuestBase + PageSize, Pattern(64, seed: 12));
        both.MapPage(DescriptorPage, BufferDescriptorBytes(GuestBase, (uint)(PageSize + 64), Uint32x4));
        both.Dispatch(DescriptorPage, extraRegisters: new Dictionary<int, uint> { [13] = (uint)(element - GuestBase) });
        Assert.Equal(ReadWord(first, (int)PageSize - 8), both.ResultWord(0));
        Assert.Equal(ReadWord(first, (int)PageSize - 4), both.ResultWord(4));
        Assert.Equal(ReadWord(second, 0), both.ResultWord(8));
        Assert.Equal(ReadWord(second, 4), both.ResultWord(12));

        var missingSecond = FormatLoadRun(vulkan);
        var only = missingSecond.MapPage(GuestBase, Pattern((int)PageSize, seed: 13));
        missingSecond.MapPage(DescriptorPage, BufferDescriptorBytes(GuestBase, (uint)(PageSize + 64), Uint32x4));
        missingSecond.Dispatch(DescriptorPage, extraRegisters: new Dictionary<int, uint> { [13] = (uint)(element - GuestBase) });
        Assert.Equal(ReadWord(only, (int)PageSize - 8), missingSecond.ResultWord(0));
        Assert.Equal(ReadWord(only, (int)PageSize - 4), missingSecond.ResultWord(4));
        Assert.Equal(0u, missingSecond.ResultWord(8));
        Assert.Equal(0u, missingSecond.ResultWord(12));
        Assert.NotEqual(0u, missingSecond.FaultWord(GuestBase + PageSize));

        // num_records ends inside the element: the whole element reads as zero, and the
        // unmapped page past the end is never walked, so it records no fault.
        var cut = FormatLoadRun(vulkan);
        cut.MapPage(GuestBase, Pattern((int)PageSize, seed: 14));
        cut.MapPage(DescriptorPage, BufferDescriptorBytes(GuestBase, (uint)(PageSize - 4), Uint32x4));
        cut.Dispatch(DescriptorPage, extraRegisters: new Dictionary<int, uint> { [13] = (uint)(element - GuestBase) });
        for (uint component = 0; component < 4; component++)
        {
            Assert.Equal(0u, cut.ResultWord(component * 4));
        }

        Assert.Equal(0u, cut.FaultWord(GuestBase + PageSize));

        // 8_8_8_8 UNORM inside one page: each byte converts to byte / 255 (within one ulp;
        // the conversion multiplies by 1/255).
        const uint Unorm8x4 = 56;
        var unorm = FormatLoadRun(vulkan);
        var bytes = unorm.MapPage(GuestBase, Pattern((int)PageSize, seed: 15));
        unorm.MapPage(DescriptorPage, BufferDescriptorBytes(GuestBase, (uint)(PageSize), Unorm8x4));
        unorm.Dispatch(DescriptorPage, extraRegisters: new Dictionary<int, uint> { [13] = 36 });
        for (var component = 0; component < 4; component++)
        {
            var expected = BitConverter.SingleToUInt32Bits(bytes[36 + component] / 255f);
            Assert.InRange(unorm.ResultWord((uint)component * 4), expected - 1, expected + 1);
        }

        foreach (var run in new[] { both, missingSecond, cut, unorm })
        {
            run.Finish(output, nameof(RuntimeFormattedLoad_ReadsItsElementThroughThePageTable));
        }
    }

    // ---- programs ----

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    public void GpuLoadedDescriptor_MultipleWordsRespectEachPage(bool formatted, bool mapFirst, bool mapSecond)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var run = new Run(vulkan, GpuDescriptorLoadProgram(formatted));
        Assert.Contains(run.Request.Memory.Entries, memory => memory.DeviceDescriptor);
        var first = Pattern((int)PageSize, seed: 31);
        var second = Pattern((int)PageSize, seed: 32);
        if (mapFirst) run.MapPage(GuestBase, first);
        if (mapSecond) run.MapPage(GuestBase + PageSize, second);
        var descriptorPage = GuestBase + 4 * PageSize;
        run.MapPage(descriptorPage, BufferDescriptorBytes(GuestBase, (uint)PageSize + 16));
        run.Dispatch(descriptorPage, extraRegisters: new Dictionary<int, uint> { [13] = (uint)PageSize - 8 });
        Assert.Equal(mapFirst ? ReadWord(first, (int)PageSize - 8) : 0u, run.ResultWord(0));
        Assert.Equal(mapFirst ? ReadWord(first, (int)PageSize - 4) : 0u, run.ResultWord(4));
        Assert.Equal(mapSecond ? ReadWord(second, 0) : 0u, run.ResultWord(8));
        Assert.Equal(mapSecond ? ReadWord(second, 4) : 0u, run.ResultWord(12));
        var firstBit = 1u << (int)((GuestBase >> Gen5SpirvTranslator.DeviceAddressPageBits) & 31);
        var secondBit = firstBit << 1;
        Assert.Equal((mapFirst ? 0u : firstBit) | (mapSecond ? 0u : secondBit), run.FaultWord(GuestBase));
        run.Finish(output, nameof(GpuLoadedDescriptor_MultipleWordsRespectEachPage));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GpuLoadedDescriptor_BoundsAndOffsetWrapDoNotWalkSkippedWords(bool formatted, bool wrapOffset)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var run = new Run(vulkan, GpuDescriptorLoadProgram(formatted));
        Assert.Contains(run.Request.Memory.Entries, memory => memory.DeviceDescriptor);
        var bytes = run.MapPage(GuestBase, Pattern((int)PageSize, seed: 33));
        var descriptorPage = GuestBase + 4 * PageSize;
        var size = wrapOffset ? 4u : (uint)PageSize - 4;
        run.MapPage(descriptorPage, BufferDescriptorBytes(GuestBase, size));
        var offset = wrapOffset ? 0xFFFF_FFFCu : (uint)PageSize - 8;
        run.Dispatch(descriptorPage, extraRegisters: new Dictionary<int, uint> { [13] = offset });
        for (uint component = 0; component < 4; component++)
        {
            var expected = formatted ? 0u : wrapOffset
                ? component == 1 ? ReadWord(bytes, 0) : 0u
                : component == 0 ? ReadWord(bytes, (int)PageSize - 8) : 0u;
            Assert.Equal(expected, run.ResultWord(component * 4));
        }
        Assert.All(run.FaultWords(), word => Assert.Equal(0u, word));
        run.Finish(output, nameof(GpuLoadedDescriptor_BoundsAndOffsetWrapDoNotWalkSkippedWords));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GpuLoadedDescriptor_GuestAddressWrapResolvesTheNewPage(bool formatted)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var run = new Run(vulkan, GpuDescriptorLoadProgram(formatted));
        var bytes = run.MapPage(0, Pattern((int)PageSize, seed: 34));
        var descriptorPage = GuestBase + 4 * PageSize;
        run.MapPage(descriptorPage, BufferDescriptorBytes(0x0000_FFFF_FFFF_FFFCu, 16));
        run.Dispatch(descriptorPage, extraRegisters: new Dictionary<int, uint> { [13] = 0 });
        Assert.Equal(0u, run.ResultWord(0));
        for (uint component = 1; component < 4; component++)
            Assert.Equal(ReadWord(bytes, (int)(component - 1) * 4), run.ResultWord(component * 4));
        Assert.All(run.FaultWords(), word => Assert.Equal(0u, word));
        run.Finish(output, nameof(GpuLoadedDescriptor_GuestAddressWrapResolvesTheNewPage));
    }

    public static IEnumerable<object[]> PartialFormats()
    {
        for (uint format = 1; format < 128; format++)
        {
            if (!Gfx10UnifiedFormat.TryDecode(format, out _, out _)) continue;
            for (uint count = 1; count < 4; count++)
                yield return [format, count];
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GpuLoadedDescriptor_OffsetAndGuestWrapKeepTheMiddlePage(bool formatted)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var run = new Run(vulkan, GpuDescriptorLoadProgram(formatted, formatted ? 1u : 4u));
        var data = run.MapPage(0, Pattern(64, seed: 35));
        var descriptorPage = GuestBase + 4 * PageSize;
        var descriptor = BufferDescriptorBytes(0x0000_FFFF_0000_0008ul, uint.MaxValue);
        if (formatted) WriteWord(descriptor, 12, 5u | (75u << 12)); // X reads the Y component.
        run.MapPage(descriptorPage, descriptor);
        run.Dispatch(descriptorPage, extraRegisters: new Dictionary<int, uint> { [13] = 0xFFFF_FFF4u });
        // The first guest page is at the end of the 48-bit space, the middle
        // page wraps to zero, and the last page follows the uint32 offset wrap.
        Assert.Equal(formatted ? ReadWord(data, 0) : 0u, run.ResultWord(0));
        if (!formatted)
        {
            Assert.Equal(ReadWord(data, 0), run.ResultWord(4));
            Assert.Equal(0u, run.ResultWord(8)); // Last dword exceeds num_records.
            Assert.Equal(0u, run.ResultWord(12));
        }
        Assert.All(run.FaultWords(), word => Assert.Equal(0u, word));
        run.Finish(output, nameof(GpuLoadedDescriptor_OffsetAndGuestWrapKeepTheMiddlePage));
    }

    [Theory]
    [MemberData(nameof(PartialFormats))]
    public void GpuLoadedDescriptor_PartialFormatMatchesFullLoad(uint format, uint count)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var data = Pattern(64, seed: format);
        for (uint selector = 0; selector < 8; selector++)
        {
            uint swizzle = 0;
            for (uint component = 0; component < count; component++)
                swizzle |= ((selector + component) & 7) << (int)(component * 3);
            foreach (uint offset in new uint[] { 1, 60 })
            {
                uint[] Execute(uint width)
                {
                    var run = new Run(vulkan, GpuDescriptorLoadProgram(true, width));
                    Assert.Contains(run.Request.Memory.Entries, memory => memory.Pc == 36 && memory.DeviceDescriptor);
                    run.MapPage(GuestBase, data);
                    var descriptorPage = GuestBase + 4 * PageSize;
                    var descriptor = BufferDescriptorBytes(GuestBase, 64);
                    WriteWord(descriptor, 12, swizzle | (format << 12));
                    run.MapPage(descriptorPage, descriptor);
                    run.Dispatch(descriptorPage, extraRegisters: new Dictionary<int, uint> { [13] = offset });
                    var result = Enumerable.Range(0, (int)count).Select(index => run.ResultWord((uint)index * 4)).ToArray();
                    Assert.All(run.FaultWords(), word => Assert.Equal(0u, word));
                    run.Finish(output, nameof(GpuLoadedDescriptor_PartialFormatMatchesFullLoad));
                    return result;
                }
                Assert.Equal(Execute(4), Execute(count));
            }
        }
    }

    [Theory]
    [InlineData(75u, 1u)]
    [InlineData(75u, 2u)]
    [InlineData(75u, 3u)]
    [InlineData(75u, 4u)]
    [InlineData(76u, 4u)]
    [InlineData(77u, 1u)]
    [InlineData(77u, 2u)]
    [InlineData(77u, 3u)]
    [InlineData(77u, 4u)]
    public void GpuLoadedD16_PacksComponentsWithoutOverwritingNeighbourRegisters(uint format, uint count)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var load = BufferAccess(52, count switch
        {
            1 => "BufferLoadFormatD16X", 2 => "BufferLoadFormatD16Xy",
            3 => "BufferLoadFormatD16Xyz", _ => "BufferLoadFormatD16Xyzw",
        }, 16, dwords: (count + 1) / 2, vectorData: 4);
        load = load with { Control = ((Gen5BufferMemoryControl)load.Control!) with
        { PackedD16 = true, FormatComponentCount = count } };
        var program = Program([
            .. GpuDescriptorLoadProgram(true).Instructions.Take(8),
            MoveVector(36, 6, 0xAABBCCDD), MoveVector(44, 7, 0x11223344),
            load, BufferAccess(60, "BufferStoreDwordx4", ResultRegister, dwords: 4, vectorData: 4), EndProgram(68),
        ]);
        var run = new Run(vulkan, program);
        var data = new byte[64];
        uint[] components = format == 77
            ? [0x3F800000u, 0xBF000000u, 0x40400000u, 0x40800000u]
            : [0x00011234u, 0xFFFF8000u, 0x0002ABCDu, 0xFFFF7FFFu];
        for (var i = 0; i < 4; i++) WriteWord(data, i * 4, components[i]);
        run.MapPage(GuestBase, data);
        var descriptorPage = GuestBase + 4 * PageSize;
        run.MapPage(descriptorPage, BufferDescriptorBytes(GuestBase, 64, format));
        run.Dispatch(descriptorPage);
        for (uint component = 0; component < count; component++)
        {
            var expected = format == 77
                ? BitConverter.HalfToUInt16Bits((Half)BitConverter.UInt32BitsToSingle(components[component]))
                : components[component] & 0xFFFF;
            var actual = (run.ResultWord(component / 2 * 4) >> (int)((component & 1) * 16)) & 0xFFFF;
            Assert.Equal(expected, actual);
        }
        Assert.Equal(0xAABBCCDDu, run.ResultWord(8));
        Assert.Equal(0x11223344u, run.ResultWord(12));
        run.Finish(output, nameof(GpuLoadedD16_PacksComponentsWithoutOverwritingNeighbourRegisters));
    }

    [Fact]
    public void RuntimeFormatWithoutCandidates_ReadsTheActualGuestDescriptor()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var run = new Run(vulkan, GpuDescriptorLoadProgram(true), configure: request =>
        {
            Assert.True(request.Memory.TryGetIndex(36, 0, out var index));
            Assert.False(request.BufferCandidateTableByMemoryIndex.ContainsKey(index));
            request.Memory[index].DeviceDescriptor = false;
            request.Memory[index].BufferDescriptor = new GuestBufferDescriptor { Provenance = BufferDescriptorProvenance.Runtime };
        });
        var data = new byte[64];
        for (var component = 0; component < 4; component++)
            WriteWord(data, component * 4, BitConverter.SingleToUInt32Bits(component + 1f));
        run.MapPage(GuestBase, data);
        var descriptorPage = GuestBase + 4 * PageSize;
        run.MapPage(descriptorPage, BufferDescriptorBytes(GuestBase, 64, 77));
        run.Dispatch(descriptorPage);
        for (var component = 0; component < 4; component++)
            Assert.Equal(ReadWord(data, component * 4), run.ResultWord((uint)component * 4));
        run.Finish(output, nameof(RuntimeFormatWithoutCandidates_ReadsTheActualGuestDescriptor));
    }

    [Theory]
    [InlineData(75u)]
    [InlineData(76u)]
    [InlineData(77u)]
    public void GpuLoadedD16Store_UnpacksBothRegistersIntoFourComponents(uint format)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var store = BufferAccess(52, "BufferStoreFormatD16Xyzw", 16, dwords: 2, vectorData: 4);
        store = store with { Control = ((Gen5BufferMemoryControl)store.Control!) with
        { PackedD16 = true, FormatComponentCount = 4 } };
        var program = Program([.. GpuDescriptorStoreProgram().Instructions.Take(11), store, EndProgram(60)]);
        var run = new Run(vulkan, program);
        run.MapPage(GuestBase, new byte[64]);
        var descriptorPage = GuestBase + 4 * PageSize;
        run.MapPage(descriptorPage, BufferDescriptorBytes(GuestBase, 64, format));
        ushort[] components = format == 77 ? [0x3C00, 0xB800, 0x4200, 0x4400] : [0x1234, 0x8000, 0xABCD, 0x7FFF];
        run.Dispatch(descriptorPage, extraRegisters: new Dictionary<int, uint>
        {
            [24] = components[0] | ((uint)components[1] << 16),
            [25] = components[2] | ((uint)components[3] << 16),
            [26] = 0xDEADBEEF, [27] = 0xBAD0CAFE,
        });
        for (uint component = 0; component < 4; component++)
        {
            var expected = format switch
            {
                77 => BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf(components[component])),
                76 => unchecked((uint)(int)(short)components[component]),
                _ => components[component],
            };
            Assert.Equal(expected, run.PageWord(GuestBase, component * 4));
        }
        run.Finish(output, nameof(GpuLoadedD16Store_UnpacksBothRegistersIntoFourComponents));
    }

    private static Gen5ShaderProgram GpuDescriptorLoadProgram(bool formatted, uint count = 4) => Program(
        MoveVectorFromScalar(0, 8, AddressLow),
        Vop2(4, "VAddU32", 8, Gen5Operand.Vector(8), Gen5Operand.Vector(0)),
        ReadFirstLane(8, 8, 8), MoveScalarRegister(12, 9, AddressHigh),
        MoveScalar(16, 10, 16), MoveScalar(20, 11, 0),
        ScalarBufferLoad(24, 8, destination: 16, count: 4),
        MoveVectorFromScalar(32, OffsetRegister, 13),
        BufferAccess(36, formatted ? count switch
            { 1 => "BufferLoadFormatX", 2 => "BufferLoadFormatXy", 3 => "BufferLoadFormatXyz", _ => "BufferLoadFormatXyzw" }
            : "BufferLoadDwordx4", 16,
            dwords: count, vectorData: 4, offsetEnabled: true, vectorAddress: OffsetRegister),
        BufferAccess(44, "BufferStoreDwordx4", ResultRegister, dwords: 4, vectorData: 4),
        EndProgram(52));

    private static byte[] BufferDescriptorBytes(ulong address, uint size, uint format = 75)
    {
        var bytes = new byte[16];
        WriteWord(bytes, 0, (uint)address);
        WriteWord(bytes, 4, (uint)(address >> 32) & 0xFFFF);
        WriteWord(bytes, 8, size);
        WriteWord(bytes, 12, 4u | (5u << 3) | (6u << 6) | (7u << 9) | (format << 12));
        return bytes;
    }

    // A typed store through a V# the GPU reads from memory: the runtime format selects the
    // encoding, and the element is written through the page table without touching the
    // bytes around it, across a page boundary and at an unaligned offset. An element cut by
    // num_records, or a format that encodes nothing, writes nothing.
    [Theory]
    [InlineData(56u, 2u, 4u, true)]   // 8_8_8_8 UNORM, 2 bytes before the page end
    [InlineData(75u, 8u, 16u, true)]  // 32_32_32_32 UINT, 8 bytes before the page end
    [InlineData(75u, 8u, 12u, false)] // num_records ends inside the element
    [InlineData(0u, 8u, 16u, false)]  // invalid format
    public void GpuLoadedDescriptor_FormattedStoreWritesItsElementThroughThePageTable(uint format, uint fromEnd, uint recordBytesPastOffset, bool lands)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var run = new Run(vulkan, GpuDescriptorStoreProgram());
        Assert.Contains(run.Request.Memory.Entries, memory => memory.DeviceDescriptor);
        var first = run.MapPage(GuestBase, Pattern((int)PageSize, seed: 41));
        var second = run.MapPage(GuestBase + PageSize, Pattern((int)PageSize, seed: 42));
        var descriptorPage = GuestBase + 4 * PageSize;
        var offset = (uint)PageSize - fromEnd;
        run.MapPage(descriptorPage, BufferDescriptorBytes(GuestBase, offset + recordBytesPastOffset, format));
        uint[] values = format == 56
            ? [BitConverter.SingleToUInt32Bits(1f), BitConverter.SingleToUInt32Bits(0.5f), 0u, BitConverter.SingleToUInt32Bits(1f)]
            : [0x1111_1111, 0x2222_2222, 0x3333_3333, 0x4444_4444];
        run.Dispatch(descriptorPage, extraRegisters: new Dictionary<int, uint>
        {
            [13] = offset, [24] = values[0], [25] = values[1], [26] = values[2], [27] = values[3],
        });

        // The plane around the element, as bytes: the end of the first page and the start of the second.
        var expected = new byte[32];
        first.AsSpan((int)PageSize - 16, 16).CopyTo(expected);
        second.AsSpan(0, 16).CopyTo(expected.AsSpan(16));
        if (lands)
        {
            byte[] element = format == 56
                ? [0xFF, 0x80, 0x00, 0xFF]
                : [.. values.SelectMany(BitConverter.GetBytes)];
            element.CopyTo(expected.AsSpan(16 - (int)fromEnd));
        }

        var actual = new byte[32];
        for (var word = 0; word < 4; word++)
        {
            BitConverter.TryWriteBytes(actual.AsSpan(word * 4), run.PageWord(GuestBase, PageSize - 16 + (ulong)word * 4));
            BitConverter.TryWriteBytes(actual.AsSpan(16 + word * 4), run.PageWord(GuestBase + PageSize, (ulong)word * 4));
        }

        Assert.Equal(expected, actual);
        run.Finish(output, nameof(GpuLoadedDescriptor_FormattedStoreWritesItsElementThroughThePageTable));
    }

    // s[16:19] = the V# at s[8:9] + v0, read by the GPU; v4..v7 = s24..s27;
    // typed_store(s[16:19], offset s13) of v4..v7.
    private static Gen5ShaderProgram GpuDescriptorStoreProgram() => Program(
        MoveVectorFromScalar(0, 8, AddressLow),
        Vop2(4, "VAddU32", 8, Gen5Operand.Vector(8), Gen5Operand.Vector(0)),
        ReadFirstLane(8, 8, 8), MoveScalarRegister(12, 9, AddressHigh),
        MoveScalar(16, 10, 16), MoveScalar(20, 11, 0),
        ScalarBufferLoad(24, 8, destination: 16, count: 4),
        MoveVectorFromScalar(32, OffsetRegister, 13),
        MoveVectorFromScalar(36, 4, 24), MoveVectorFromScalar(40, 5, 25),
        MoveVectorFromScalar(44, 6, 26), MoveVectorFromScalar(48, 7, 27),
        BufferAccess(52, "BufferStoreFormatXyzw", 16, dwords: 4, vectorData: 4, offsetEnabled: true, vectorAddress: OffsetRegister),
        EndProgram(60));

    // The V# sits on its own page, which the GPU reads to find the formatted buffer.
    private const ulong DescriptorPage = GuestBase + 4 * PageSize;

    // A formatted load reads through the page table only when its V# is unknown until the
    // shader runs; a descriptor read by the GPU is one, so the program loads it that way.
    private Run FormatLoadRun(HeadlessVulkan vulkan)
    {
        var run = new Run(vulkan, GpuDescriptorLoadProgram());
        Assert.Contains(run.Request.Memory.Entries, memory => memory.DeviceDescriptor);
        return run;
    }

    // s[16:19] = the V# at s[8:9] + v0, read by the GPU; v4..v7 = format_load(s[16:19], s13);
    // result[0..3] = v4..v7.
    private static Gen5ShaderProgram GpuDescriptorLoadProgram() => Program(
        MoveVectorFromScalar(0, 8, AddressLow),
        Vop2(4, "VAddU32", 8, Gen5Operand.Vector(8), Gen5Operand.Vector(0)),
        ReadFirstLane(8, 8, 8), MoveScalarRegister(12, 9, AddressHigh),
        MoveScalar(16, 10, 16), MoveScalar(20, 11, 0),
        ScalarBufferLoad(24, 8, destination: 16, count: 4),
        MoveVectorFromScalar(32, OffsetRegister, 13),
        BufferAccess(36, "BufferLoadFormatXyzw", 16, 0, 4, vectorData: 4, offsetEnabled: true, vectorAddress: OffsetRegister),
        BufferAccess(44, "BufferStoreDwordx4", ResultRegister, 0, 4, vectorData: 4),
        EndProgram(52));

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

        public Run(HeadlessVulkan vulkan, Gen5ShaderProgram program, uint threadCount = 1, ulong tableEntries = TableEntries,
            Action<ShaderCompileRequest>? configure = null)
        {
            var (plan, resources, layout) = Prepare(program);
            Plan = plan;
            Request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = threadCount, ThreadCountX = threadCount };
            configure?.Invoke(Request);
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

        public void Dispatch(ulong guestAddress, (ulong Base, ulong Size)? writtenRange = null, IReadOnlyDictionary<int, uint>? extraRegisters = null)
        {
            _pageTable = _runner.CreatePageTable(_tableEntries, _pages.Select(page => (page.Key, page.Value, 0ul)));
            var registers = new uint[256];
            foreach (var (register, value) in extraRegisters ?? new Dictionary<int, uint>())
            {
                registers[register] = value;
            }

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
