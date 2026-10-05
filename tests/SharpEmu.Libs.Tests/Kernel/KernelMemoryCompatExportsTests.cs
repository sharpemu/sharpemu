// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Core.Memory;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Memory.GpuMemory;
using System.Globalization;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(KernelMemoryCompatStateCollection.Name, DisableParallelization = true)]
public sealed class KernelMemoryCompatStateCollection
{
    public const string Name = "KernelMemoryCompatState";
}

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMemoryCompatExportsTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;
    private const ulong AllocationOutAddress = GuestMemoryBase + 0x100;
    private const ulong SpanStartOutAddress = GuestMemoryBase + 0x108;
    private const ulong SpanSizeOutAddress = GuestMemoryBase + 0x110;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UppercaseStringConversion_ConsumesWideArgumentBetweenNarrowStrings(bool useVaList)
    {
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(GuestMemoryBase + 0x100, "assets");
        Assert.True(memory.TryWrite(GuestMemoryBase + 0x200, Encoding.Unicode.GetBytes("armor\0")));
        memory.WriteCString(GuestMemoryBase + 0x300, "body");
        const string format = "%s/%S_%s.bin";
        string rendered;
        if (useVaList)
        {
            var arguments = GuestMemoryBase + 0x400;
            var savedRegisters = GuestMemoryBase + 0x500;
            Assert.True(context.TryWriteUInt64(arguments, 48UL << 32));
            Assert.True(context.TryWriteUInt64(arguments + 8, GuestMemoryBase + 0x600));
            Assert.True(context.TryWriteUInt64(arguments + 16, savedRegisters));
            Assert.True(context.TryWriteUInt64(savedRegisters, GuestMemoryBase + 0x100));
            Assert.True(context.TryWriteUInt64(savedRegisters + 8, GuestMemoryBase + 0x200));
            Assert.True(context.TryWriteUInt64(savedRegisters + 16, GuestMemoryBase + 0x300));
            Assert.True(KernelMemoryCompatExports.TryFormatStringFromVaList(context, format, arguments, out rendered));
        }
        else
        {
            context[CpuRegister.Rcx] = GuestMemoryBase + 0x100;
            context[CpuRegister.R8] = GuestMemoryBase + 0x200;
            context[CpuRegister.R9] = GuestMemoryBase + 0x300;
            rendered = KernelMemoryCompatExports.FormatStringFromVarArgs(context, format, 3);
        }
        Assert.Equal("assets/armor_body.bin", rendered);
    }

    [Theory]
    [InlineData("%8.3S", "     arm")]
    [InlineData("%-8.3S", "arm     ")]
    public void UppercaseStringConversion_UsesExistingWideStringFormatting(string format, string expected)
    {
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(GuestMemoryBase + 0x100, Encoding.Unicode.GetBytes("armor\0")));
        context[CpuRegister.Rcx] = GuestMemoryBase + 0x100;
        Assert.Equal(expected, KernelMemoryCompatExports.FormatStringFromVarArgs(context, format, 3));
    }

    [Theory]
    [InlineData("a/b/c.xml", '/', 1, 3)]
    [InlineData("a/b/c.xml", '.', 5, 5)]
    [InlineData("a/b/c.xml", 'z', -1, -1)]
    [InlineData("a/b/c.xml", '\0', 9, 9)]
    [InlineData("", 'a', -1, -1)]
    public void StrchrStrrchr_MatchNativeLibc(string text, char needle, int firstIndex, int lastIndex)
    {
        const ulong address = GuestMemoryBase + 0x100;
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x1000);
        memory.WriteCString(address, text);

        Assert.Equal(ExpectedMatch(address, firstIndex), ScanString(memory, address, needle, last: false));
        Assert.Equal(ExpectedMatch(address, lastIndex), ScanString(memory, address, needle, last: true));
    }

    [Fact]
    public void StrchrStrrchr_ScanAcrossPagesAndPastChunkSize()
    {
        // Starts just before a page boundary and runs well past one 4 KiB chunk.
        const ulong address = GuestMemoryBase + 0xFF0;
        var text = "<" + new string('x', 6000) + ">" + new string('y', 100) + ">";
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x3000);
        memory.WriteCString(address, text);

        Assert.Equal(address + 6001, ScanString(memory, address, '>', last: false));
        Assert.Equal(address + (ulong)text.Length - 1, ScanString(memory, address, '>', last: true));
        Assert.Equal(address + (ulong)text.Length, ScanString(memory, address, '\0', last: false));
    }

    [Fact]
    public void StrchrStrrchr_StringEndingAtEndOfMappedMemory()
    {
        // The first chunk would read past the mapping, so the byte-wise fallback
        // must still find the terminator that lies inside it.
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x1000);
        const ulong address = GuestMemoryBase + 0x1000 - 6;
        memory.WriteCString(address, "a/b/c");

        Assert.Equal(address + 1, ScanString(memory, address, '/', last: false));
        Assert.Equal(address + 3, ScanString(memory, address, '/', last: true));
    }

    [Fact]
    public void StrchrStrrchr_UnterminatedStringAtEndOfMemoryFaults()
    {
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x1000);
        const ulong address = GuestMemoryBase + 0x1000 - 4;
        Assert.True(memory.TryWrite(address, "abcd"u8));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = 'z';

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, KernelMemoryCompatExports.Strchr(context));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, KernelMemoryCompatExports.Strrchr(context));
    }

    [Fact]
    public void StrchrStrrchr_ScanMappedMemoryInPlace()
    {
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateGuestMemory(0x4000, 0x1000, out var baseAddress));

        // Crosses a page boundary and runs past one 4 KiB page.
        var address = baseAddress + 0xFF0;
        var text = "<" + new string('x', 6000) + ">" + new string('y', 100) + ">";
        Assert.True(memory.TryWrite(address, Encoding.ASCII.GetBytes(text + "\0")));

        Assert.True(memory.TryScanCString(address, (byte)'>', findLast: false, 1_048_576, out var first));
        Assert.Equal(address + 6001, first);
        Assert.True(memory.TryScanCString(address, (byte)'>', findLast: true, 1_048_576, out var last));
        Assert.Equal(address + (ulong)text.Length - 1, last);
        Assert.True(memory.TryScanCString(address, 0, findLast: true, 1_048_576, out var terminator));
        Assert.Equal(address + (ulong)text.Length, terminator);
        Assert.True(memory.TryScanCString(address, (byte)'z', findLast: false, 1_048_576, out var missing));
        Assert.Equal(0UL, missing);

        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = '>';
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Strrchr(context));
        Assert.Equal(address + (ulong)text.Length - 1, context[CpuRegister.Rax]);
    }

    [Fact]
    public void StrchrStrrchr_UnmappedMemoryIsNotScannedInPlace()
    {
        using var memory = new PhysicalVirtualMemory();
        Assert.False(memory.TryScanCString(0x7_0000_0000, (byte)'a', findLast: false, 1_048_576, out _));
    }

    private static ulong ExpectedMatch(ulong address, int index) =>
        index < 0 ? 0 : address + (ulong)index;

    private static ulong ScanString(FakeCpuMemory memory, ulong address, char needle, bool last)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = needle;
        var result = last
            ? KernelMemoryCompatExports.Strrchr(context)
            : KernelMemoryCompatExports.Strchr(context);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        return context[CpuRegister.Rax];
    }

    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixOpen_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/il2cpp.usym");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0; // O_RDONLY

        var result = KernelMemoryCompatExports.PosixOpen(context);

        // A libc open() failure must be -1, not the raw 0x8002xxxx sentinel the
        // guest would otherwise store as a valid fd and later dereference.
        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void KernelMkdir_GuestRootReturnsAlreadyExists()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/");
        context[CpuRegister.Rdi] = pathAddress;

        var result = KernelMemoryCompatExports.KernelMkdir(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS, result);
    }

    [Fact]
    public void PosixFstat_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // the not-found sentinel misused as an fd
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixFstat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixClose_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd

        var result = KernelMemoryCompatExports.PosixClose(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixRead_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x40;

        var result = KernelMemoryCompatExports.PosixRead(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixWrite_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(bufferAddress, "payload");
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x7;

        var result = KernelMemoryCompatExports.PosixWrite(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_FragmentedRangeReturnsLargestAlignedSpan()
    {
        const ulong firstAllocationStart = 0x0020_0000;
        const ulong firstAllocationLength = 0x0020_0000;
        const ulong secondAllocationStart = 0x00C0_0000;
        const ulong secondAllocationLength = 0x0040_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, firstAllocationStart, firstAllocationLength);
            AllocateDirectMemory(context, secondAllocationStart, secondAllocationLength);

            QueryAvailableDirectMemory(context, 0, 0x0100_0000, 0x4000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0040_0000UL, spanStart);
            Assert.Equal(0x0080_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, firstAllocationStart, firstAllocationLength);
            ReleaseDirectMemory(context, secondAllocationStart, secondAllocationLength);
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_AppliesAlignmentBeforeComparingSpans()
    {
        const ulong allocationStart = 0x0070_0000;
        const ulong allocationLength = 0x0010_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, allocationStart, allocationLength);

            QueryAvailableDirectMemory(context, 0x0010_0000, 0x00C0_0000, 0x0040_0000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0080_0000UL, spanStart);
            Assert.Equal(0x0040_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, allocationStart, allocationLength);
        }
    }

    private static void AllocateDirectMemory(CpuContext context, ulong start, ulong length, ulong outputAddress = AllocationOutAddress)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = start + length;
        context[CpuRegister.Rdx] = length;
        context[CpuRegister.Rcx] = 0x4000;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = outputAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        Assert.True(context.TryReadUInt64(outputAddress, out var allocatedAddress));
        Assert.Equal(start, allocatedAddress);
    }

    private static void QueryAvailableDirectMemory(
        CpuContext context,
        ulong searchStart,
        ulong searchEnd,
        ulong alignment)
    {
        context[CpuRegister.Rdi] = searchStart;
        context[CpuRegister.Rsi] = searchEnd;
        context[CpuRegister.Rdx] = alignment;
        context[CpuRegister.Rcx] = SpanStartOutAddress;
        context[CpuRegister.R8] = SpanSizeOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAvailableDirectMemorySize(context));
    }

    private static void ReleaseDirectMemory(CpuContext context, ulong start, ulong length)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = length;

        Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(context));
    }

    [Fact]
    public void MapNamedFlexibleMemory_NullInOutPointerReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x03; // CPU read|write
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal(unchecked((int)0x80020016), result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong inOutAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(inOutAddress, BitConverter.GetBytes(0UL));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal(unchecked((int)0x80020016), result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_UnreadableInOutPointerReturnsMemoryFault()
    {
        // The in-out pointer points outside the FakeCpuMemory backing store, so
        // the first TryReadUInt64 must fail before any reservation is attempted.
        const ulong memoryBase = 0x1_0000_0000;
        const ulong unreachableInOut = memoryBase + 0x10_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unreachableInOut;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal(unchecked((int)0x8002000E), result);
    }

    [Fact]
    public void VirtualQuery_PreservesReservationPastFixedCommitAtSameBase()
    {
        const ulong reservedLength = 0x1_0000;
        const ulong committedLength = 0x4000;
        using var test = new BackedKernelMemory();
        var context = test.Context;
        var infoAddress = test.Output + 0x100;
        var memoryBase = test.Reserve(reservedLength);
        test.Allocate(0, committedLength);
        Assert.Equal(memoryBase, test.Map(0, committedLength, memoryBase));

        context[CpuRegister.Rdi] = memoryBase + 0x8000;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = infoAddress;
        context[CpuRegister.Rcx] = 0x48;

        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(context));
        Assert.True(context.TryReadUInt64(infoAddress, out var regionStart));
        Assert.True(context.TryReadUInt64(infoAddress + 8, out var regionEnd));
        Assert.Equal(memoryBase + committedLength, regionStart);
        Assert.Equal(memoryBase + reservedLength, regionEnd);
    }

    [Fact]
    public void Mprotect_ZeroAddressReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal(unchecked((int)0x80020016), result);
    }

    [Fact]
    public void Mprotect_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = memoryBase;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal(unchecked((int)0x80020016), result);
    }

    [Fact]
    public void Mprotect_UnmappedRangeReturnsNotFound()
    {
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal(unchecked((int)0x80020002), result);
    }

    [Fact]
    public void Munmap_ZeroAddressReturnsAccessDenied()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal(unchecked((int)0x8002000D), result);
    }

    [Fact]
    public void Munmap_OverflowRangeReturnsInvalidArgument()
    {
        // address + length would overflow; KernelMunmap guards this explicitly
        // before touching any region accounting.
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue - 0x10;
        context[CpuRegister.Rsi] = 0x20;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal(unchecked((int)0x80020016), result);
    }

    [Fact]
    public void Munmap_UnmappedRangeReturnsAccessDenied()
    {
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal(unchecked((int)0x8002000D), result);
    }

    [Fact]
    public void MapDirectMemory_RegistersAndMunmapUnregistersTheGpuSpan()
    {
        const ulong directStart = 0x0300_0000;
        const ulong length = 0x0001_0000;
        const ulong requestedAddress = 0x2_0000_0000;
        using var memory = new PhysicalVirtualMemory(viewHost: HostViewMemory.Create(), backingBytes: 128UL * 1024 * 1024);
        var outputAddress = memory.AllocateAt(0, 0x4000, false);
        var context = new CpuContext(memory, Generation.Gen5);
        var gpuMemory = new GuestGpuMemory(new RecordingAddressSpace());
        GuestGpuMemoryHook.Attach(gpuMemory);
        var mappedAddress = 0UL;

        try
        {
            AllocateDirectMemory(context, directStart, length, outputAddress);

            Assert.True(context.TryWriteUInt64(outputAddress, requestedAddress));
            context[CpuRegister.Rdi] = outputAddress;
            context[CpuRegister.Rsi] = length;
            context[CpuRegister.Rdx] = 0x33; // CPU read|write, GPU read|write
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = directStart;
            context[CpuRegister.R9] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));
            Assert.True(context.TryReadUInt64(outputAddress, out mappedAddress));
            Assert.True(gpuMemory.Covers(mappedAddress, length));

            context[CpuRegister.Rdi] = mappedAddress;
            context[CpuRegister.Rsi] = length;
            Assert.Equal(0, KernelMemoryCompatExports.KernelMunmap(context));
            Assert.False(gpuMemory.Covers(mappedAddress, length));
            mappedAddress = 0;
        }
        finally
        {
            if (mappedAddress != 0)
            {
                context[CpuRegister.Rdi] = mappedAddress;
                context[CpuRegister.Rsi] = length;
                _ = KernelMemoryCompatExports.KernelMunmap(context);
            }

            ReleaseDirectMemory(context, directStart, length);
            GuestGpuMemoryHook.Attach(null);
            gpuMemory.Dispose();
            KernelMemoryCompatExports.ResetBackingMappings(memory);
        }
    }

    [Fact]
    public void ReleaseDirectMemory_FailedReleaseKeepsTheMappedGpuSpan()
    {
        const ulong directStart = 0x0400_0000;
        const ulong length = 0x0001_0000;
        const ulong unallocatedStart = 0x0500_0000;
        const ulong requestedAddress = 0x2_1000_0000;
        using var memory = new PhysicalVirtualMemory(viewHost: HostViewMemory.Create(), backingBytes: 128UL * 1024 * 1024);
        var outputAddress = memory.AllocateAt(0, 0x4000, false);
        var context = new CpuContext(memory, Generation.Gen5);
        var gpuMemory = new GuestGpuMemory(new RecordingAddressSpace());
        GuestGpuMemoryHook.Attach(gpuMemory);
        var mappedAddress = 0UL;

        try
        {
            AllocateDirectMemory(context, directStart, length, outputAddress);

            Assert.True(context.TryWriteUInt64(outputAddress, requestedAddress));
            context[CpuRegister.Rdi] = outputAddress;
            context[CpuRegister.Rsi] = length;
            context[CpuRegister.Rdx] = 0x33; // CPU read|write, GPU read|write
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = directStart;
            context[CpuRegister.R9] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));
            Assert.True(context.TryReadUInt64(outputAddress, out mappedAddress));
            Assert.True(gpuMemory.Covers(mappedAddress, length));

            context[CpuRegister.Rdi] = unallocatedStart;
            context[CpuRegister.Rsi] = length;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, KernelMemoryCompatExports.KernelCheckedReleaseDirectMemory(context));
            Assert.True(gpuMemory.Covers(mappedAddress, length));

            // Overlaps the second half of the allocation and runs past its end.
            context[CpuRegister.Rdi] = directStart + length / 2;
            context[CpuRegister.Rsi] = length;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, KernelMemoryCompatExports.KernelCheckedReleaseDirectMemory(context));
            Assert.True(gpuMemory.Covers(mappedAddress, length));

            context[CpuRegister.Rdi] = directStart + length / 2;
            context[CpuRegister.Rsi] = length;
            Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(context));
            Assert.True(gpuMemory.Covers(mappedAddress, length));

            ReleaseDirectMemory(context, directStart, length);
            Assert.False(gpuMemory.Covers(mappedAddress, length));
        }
        finally
        {
            if (mappedAddress != 0)
            {
                context[CpuRegister.Rdi] = mappedAddress;
                context[CpuRegister.Rsi] = length;
                _ = KernelMemoryCompatExports.KernelMunmap(context);
            }

            GuestGpuMemoryHook.Attach(null);
            gpuMemory.Dispose();
            KernelMemoryCompatExports.ResetBackingMappings(memory);
        }
    }
}
