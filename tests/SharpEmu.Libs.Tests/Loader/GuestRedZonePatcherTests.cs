// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Cpu.Emulation;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Libs.Tests.Loader;

[CollectionDefinition(GuestRedZonePatcherCollection.Name, DisableParallelization = true)]
public sealed class GuestRedZonePatcherCollection
{
    public const string Name = "GuestRedZonePatcher";
}

[Collection(GuestRedZonePatcherCollection.Name)]
public sealed class GuestRedZonePatcherTests
{
    [Theory]
    [InlineData("554889E5415741564155415453488B45C0C3", true)]
    [InlineData("554889E54883EC40488B45C0C3", false)]
    [InlineData("554889E54883EC20488B45C0C3", true)]
    [InlineData("554889E54889C4488B45F8C3", false)]
    [InlineData("554889E574044883EC20488B45F8C3", false)]
    [InlineData("554889E54883EC0875FA488B45F8C3", false)]
    [InlineData("554889E5FFE0488B45F8C3", false)]
    [InlineData("554889E5488D45F8C3", false)]
    [InlineData("554889E5904883EC40488B45F8C3", false)]
    [InlineData("554889E55353535353488945D0C3", true)]
    [InlineData("554889E55353535353488945D8C3", false)]
    [InlineData("554889E5535353535348898550FFFFFFC3", false)]
    [InlineData("488945D0C3", false)]
    [InlineData("554889E5535353535348894424D0C3", false)]
    [InlineData("554889E5535353535348898558FFFFFFC3", true)]
    [InlineData("554889E5535353535348898557FFFFFFC3", false)]
    public void FrameAnalysisRequiresAProvenRedZoneAccess(string bytes, bool expected)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(bytes)));
        var instructions = new List<Instruction>();
        var length = (ulong)(bytes.Length / 2);
        while (decoder.IP < length) instructions.Add(decoder.Decode());
        Assert.Equal(expected, GuestStackFrameAnalysis.UsesFrameRedZone(instructions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupingPreservesIncomingBranchFromAnotherFunction(bool separateSegment)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var memory = new PhysicalVirtualMemory();
        var imageBase = memory.AllocateAt(0, 0x10000);
        byte[] function = [0x48, 0x89, 0x44, 0x24, 0xF8,
            0x8B, 0x81, 0, 1, 0, 0, 0xB8, 0x2A, 0, 0, 0, 0xC3];
        Assert.True(memory.TryWrite(imageBase, function));
        var jump = new byte[5];
        Assert.True(GuestRedZonePatcher.TryWriteRelativeJump(jump, imageBase + 0x40, imageBase + 11));
        Assert.True(memory.TryWrite(imageBase + 0x40, jump));
        var header = new byte[48];
        header[0] = 1;
        header[2] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), imageBase);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), imageBase + 0x40);
        Assert.True(memory.TryWrite(imageBase + 0x8000, header));
        var headers = new List<ProgramHeader>
        {
            CreateProgramHeader(ProgramHeaderType.Load, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute,
                0, separateSegment ? (ulong)function.Length : 0x45),
            CreateProgramHeader(ProgramHeaderType.GnuEhFrame, ProgramHeaderFlags.Read, 0x8000, 48),
        };
        if (separateSegment)
            headers.Add(CreateProgramHeader(ProgramHeaderType.Load, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, 0x40, 5));
        var result = GuestRedZonePatcher.Patch(memory, memory, headers, imageBase, 0x10000);
        Assert.Equal(0, result.FailedSites);
        Span<byte> target = stackalloc byte[1];
        Assert.True(memory.TryRead(imageBase + 11, target));
        Assert.Equal(0xB8, target[0]);
    }

    [Theory]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x80 }, true)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0xF8 }, true)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x08 }, false)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x84, 0x24, 0x7F, 0xFF, 0xFF, 0xFF }, false)]
    public void RecognizesOnlyTheSystemVRedZone(byte[] instructionBytes, bool expected)
    {
        var instruction = Decode(instructionBytes);

        Assert.Equal(expected, GuestRedZonePatcher.UsesRedZone(instruction));
    }

    [Fact]
    public void RelocatesOnlyOrdinaryNonStackMemoryInstructions()
    {
        var ordinaryLoad = Decode([0x8B, 0x01]);
        var addressCalculation = Decode([0x48, 0x8D, 0x01]);
        var stackLoad = Decode([0x48, 0x8B, 0x44, 0x24, 0xF8]);

        Assert.True(GuestRedZonePatcher.IsFaultableGuestMemoryInstruction(ordinaryLoad));
        Assert.False(GuestRedZonePatcher.IsFaultableGuestMemoryInstruction(addressCalculation));
        Assert.False(GuestRedZonePatcher.IsFaultableGuestMemoryInstruction(stackLoad));
    }

    [Fact]
    public void EncodesAReachableRelativeJump()
    {
        Span<byte> destination = stackalloc byte[5];

        Assert.True(GuestRedZonePatcher.TryWriteRelativeJump(
            destination,
            instructionAddress: 0x1000,
            targetAddress: 0x1800));
        Assert.Equal(0xE9, destination[0]);
        Assert.Equal(0x7FB, BinaryPrimitives.ReadInt32LittleEndian(destination[1..]));
    }

    [Fact]
    public void RejectsAnUnreachableRelativeJump()
    {
        Span<byte> destination = stackalloc byte[5];

        Assert.False(GuestRedZonePatcher.TryWriteRelativeJump(
            destination,
            instructionAddress: 0x1000,
            targetAddress: 0x1_0000_1000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void PatchesWindowsAndMacOsLeafFunctionWhileLeavingLinuxUnchanged(bool framePointer)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var memory = new PhysicalVirtualMemory();
        const ulong imageSize = 0x10000;
        // Put the image at the start of a large free hole so the trampoline range beside it
        // is free too: the runtime's heaps around an arbitrary address can leave none in reach.
        const ulong holeSize = 0x4000000;
        var hole = (ulong)HostMemory.Alloc(null, (nuint)holeSize, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
        Assert.NotEqual(0UL, hole);
        Assert.True(HostMemory.Free((void*)hole, 0, HostMemory.MEM_RELEASE));
        var imageBase = memory.AllocateAt(hole, imageSize);
        const ulong expected = 0x1122_3344_5566_7788;
        // Save a value in the red zone before the memory read.
        // Read the saved value after the memory read and return it.
        byte[] function =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0x48, 0x89, 0x44, 0x24, 0xF8,
            0x8B, OperatingSystem.IsWindows() ? (byte)0x81 : (byte)0x87,
            0x00, 0x01, 0x00, 0x00,
            0x48, 0x8B, 0x44, 0x24, 0xF8,
            0xC3,
        ];
        if (framePointer)
        {
            function = [0x55, 0x48, 0x89, 0xE5,
                0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
                0x48, 0x89, 0x45, 0xF8,
                0x8B, OperatingSystem.IsWindows() ? (byte)0x81 : (byte)0x87, 0, 1, 0, 0,
                0x48, 0x8B, 0x45, 0xF8, 0x5D, 0xC3];
        }
        Assert.True(memory.TryWrite(imageBase, function));

        // Define one function with absolute 64-bit pointers in the exception frame header.
        var exceptionFrameHeader = new byte[32];
        exceptionFrameHeader[0] = 1;
        exceptionFrameHeader[2] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(exceptionFrameHeader.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(exceptionFrameHeader.AsSpan(16), imageBase);
        Assert.True(memory.TryWrite(imageBase + 0x1000, exceptionFrameHeader));
        ProgramHeader[] headers =
        [
            CreateProgramHeader(ProgramHeaderType.Load, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute,
                0, (ulong)function.Length),
            CreateProgramHeader(ProgramHeaderType.GnuEhFrame, ProgramHeaderFlags.Read, 0x1000, (ulong)exceptionFrameHeader.Length),
        ];

        var result = GuestRedZonePatcher.Patch(memory, memory, headers, imageBase, imageSize);
        var patched = new byte[function.Length];
        Assert.True(memory.TryRead(imageBase, patched));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(1, result.RedZoneFunctions);
            Assert.Equal(1, result.PatchedSites);
            Assert.Equal(0, result.FailedSites);
            if (framePointer)
            {
                Assert.Equal(0xE9, patched[14]);
            }
            else
            {
                Assert.Equal(0xE9, patched[15]);
                var trampolineAddress = (ulong)((long)imageBase + 20 +
                    BinaryPrimitives.ReadInt32LittleEndian(patched.AsSpan(16)));
                var trampoline = new byte[24];
                Assert.True(memory.TryRead(trampolineAddress, trampoline));
                Assert.Equal(new byte[] { 0x48, 0x8D, 0x64, 0x24, 0x80 }, trampoline[..5]);
                Assert.Equal(function[15..21], trampoline[5..11]);
                Assert.Equal(new byte[] { 0x48, 0x8D, 0xA4, 0x24, 0x80, 0, 0, 0 }, trampoline[11..19]);
                Assert.Equal(0xE9, trampoline[19]);
                Assert.Equal(imageBase + 21, (ulong)((long)trampolineAddress + 24 +
                    BinaryPrimitives.ReadInt32LittleEndian(trampoline.AsSpan(20))));
            }
        }
        else
        {
            Assert.Equal(0, result.PatchedSites);
            Assert.Equal(function, patched);
        }

        // Run the relocated code and compare the saved red zone value.
        // The code must restore the stack pointer before it returns.
        Assert.Equal(expected, ((delegate* unmanaged<ulong, ulong>)imageBase)(imageBase));
    }

    // Demon's Souls FUN_800ad8fe0: a four-byte compare followed by JZ had no room
    // for the jump, so a fault there let Windows overwrite the saved red-zone pointer.
    [Theory]
    [InlineData(1u, 0x1122_3344_5566_7788UL)]
    [InlineData(0u, 0UL)]
    public unsafe void PatchesShortAccessFollowedByConditionalBranch(uint value, ulong expected)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var argument = OperatingSystem.IsWindows() ? (byte)0x79 : (byte)0x7F;
        byte[] function =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, // mov rax, value
            0x48, 0x89, 0x44, 0x24, 0xF8,                               // mov [rsp-8], rax
            0x83, argument, 0x40, 0x00,                                 // cmp dword [arg+0x40], 0
            0x74, 0x06,                                                 // jz zero
            0x48, 0x8B, 0x44, 0x24, 0xF8,                               // mov rax, [rsp-8]
            0xC3,                                                       // ret
            0x31, 0xC0,                                                 // zero: xor eax, eax
            0xC3,                                                       // ret
        ];

        using var image = PatchedImage.Create(function, [(0x40, value)]);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(1, image.Result.PatchedSites);
            Assert.Equal(0xE9, image.ReadByte(15));
        }

        Assert.Equal(expected, ((delegate* unmanaged<ulong, ulong>)image.Base)(image.Base));
    }

    // Demon's Souls FUN_800ad8fe0: a two-byte AND whose successor is a branch
    // target is patched by starting the span at the instruction before it.
    [Fact]
    public unsafe void PatchesShortAccessBeforeBranchTargetFromThePreviousInstruction()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var argument = OperatingSystem.IsWindows() ? (byte)0x91 : (byte)0x97;
        byte[] function =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, // mov rax, value
            0x48, 0x89, 0x44, 0x24, 0xF8,                               // mov [rsp-8], rax
            0x48, 0x8D, argument, 0x00, 0x20, 0x00, 0x00,               // lea rdx, [arg+0x2000]
            0xB8, 0xFE, 0xFF, 0xFF, 0xFF,                               // mov eax, 0xFFFFFFFE
            0x21, 0x02,                                                 // and [rdx], eax
            0x48, 0x8B, 0x44, 0x24, 0xF8,                               // target: mov rax, [rsp-8]
            0xC3,                                                       // ret
            0xEB, 0xF8,                                                 // jmp target
        ];

        using var image = PatchedImage.Create(function, [(0x2000, 7u)]);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(1, image.Result.PatchedSites);
            Assert.Equal(0xE9, image.ReadByte(22));
        }

        Assert.Equal(0x1122_3344_5566_7788UL, ((delegate* unmanaged<ulong, ulong>)image.Base)(image.Base));
        Assert.Equal(6u, image.ReadUInt32(0x2000));
    }

    [Theory]
    [InlineData(8, 4)]
    [InlineData(0, 63)]
    public unsafe void ExtractRewriteRunsRelocatedWithoutAFault(int length, int index)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Sse4aExtractRewrite.IsRequired) return;
        var loadDestination = OperatingSystem.IsWindows() ? (byte)0x91 : (byte)0x97;
        var loadControl = OperatingSystem.IsWindows() ? (byte)0xA9 : (byte)0xAF;
        byte[] function =
        [
            0xF3, 0x0F, 0x6F, loadDestination, 0x00, 0x20, 0x00, 0x00,
            0xF3, 0x0F, 0x6F, loadControl, 0x10, 0x20, 0x00, 0x00,
            0x66, 0x0F, 0x79, 0xD5,                         // extrq xmm2,xmm5
            0x66, 0x48, 0x0F, 0x7E, 0xD0,                   // movq rax,xmm2
            0xF3, 0x0F, 0x7F, loadDestination, 0x20, 0x20, 0x00, 0x00,
            0xC3,
        ];
        const ulong low = 0xA00E_8965_2C7A_6E65;
        const ulong high = 0xEB14_B4C0_0000_0000;
        var control = (uint)length | ((uint)index << 8);
        using var image = PatchedImage.Create(function,
            [(0x2000, unchecked((uint)low)), (0x2004, (uint)(low >> 32)), (0x2008, unchecked((uint)high)), (0x200C, (uint)(high >> 32)),
             (0x2010, control), (0x2028, 0xFFFF_FFFFu)]);
        if (Sse4aExtractRewrite.IsRequired)
        {
            Assert.Equal(1, image.Result.ExtractRewrites);
            Assert.Equal(1, image.Result.ExtractCandidates);
            Assert.Equal(0, image.Result.ShaInstructionCount);
            Assert.Equal(0xE9, image.ReadByte(16));
        }

        var expected = Sse4aBitFieldEmulator.ExtractBitField(low, length, index);
        Assert.Equal(expected, ((delegate* unmanaged<ulong, ulong>)image.Base)(image.Base));
        Assert.Equal(0u, image.ReadUInt32(0x2028));
    }

    [Fact]
    public void ExtractCountersIncludeEveryInstructionInAGroupedSpan()
    {
        if (!Sse4aExtractRewrite.IsRequired) return;
        byte[] function = [0x66, 0x0F, 0x79, 0xD5, 0x66, 0x0F, 0x79, 0xD5, 0xC3];
        using var image = PatchedImage.Create(function, []);
        Assert.Equal(2, image.Result.ExtractCandidates);
        Assert.Equal(2, image.Result.ExtractRewrites);
        Assert.Equal(0, image.Result.ShaInstructionCount);
        Assert.Equal(1, image.Result.PatchedSites);
    }

    private sealed unsafe class PatchedImage : IDisposable
    {
        private const ulong ImageSize = 0x10000;
        private readonly PhysicalVirtualMemory _memory;

        private PatchedImage(PhysicalVirtualMemory memory, ulong imageBase, GuestRedZonePatcher.PatchResult result)
        {
            _memory = memory;
            Base = imageBase;
            Result = result;
        }

        public ulong Base { get; }

        public GuestRedZonePatcher.PatchResult Result { get; }

        public static PatchedImage Create(byte[] function, (ulong Offset, uint Value)[] data)
        {
            var memory = new PhysicalVirtualMemory();
            const ulong holeSize = 0x4000000;
            var hole = (ulong)HostMemory.Alloc(null, (nuint)holeSize, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            Assert.NotEqual(0UL, hole);
            Assert.True(HostMemory.Free((void*)hole, 0, HostMemory.MEM_RELEASE));
            var imageBase = memory.AllocateAt(hole, ImageSize);
            Assert.True(memory.TryWrite(imageBase, function));
            Span<byte> word = stackalloc byte[sizeof(uint)];
            foreach (var (offset, value) in data)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, value);
                Assert.True(memory.TryWrite(imageBase + offset, word));
            }

            var exceptionFrameHeader = new byte[32];
            exceptionFrameHeader[0] = 1;
            exceptionFrameHeader[2] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(exceptionFrameHeader.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(exceptionFrameHeader.AsSpan(16), imageBase);
            Assert.True(memory.TryWrite(imageBase + 0x1000, exceptionFrameHeader));
            ProgramHeader[] headers =
            [
                CreateProgramHeader(ProgramHeaderType.Load, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute,
                    0, (ulong)function.Length),
                CreateProgramHeader(ProgramHeaderType.GnuEhFrame, ProgramHeaderFlags.Read, 0x1000, (ulong)exceptionFrameHeader.Length),
            ];

            var result = GuestRedZonePatcher.Patch(memory, memory, headers, imageBase, ImageSize);
            return new PatchedImage(memory, imageBase, result);
        }

        public byte ReadByte(ulong offset)
        {
            Span<byte> value = stackalloc byte[1];
            Assert.True(_memory.TryRead(Base + offset, value));
            return value[0];
        }

        public uint ReadUInt32(ulong offset)
        {
            Span<byte> value = stackalloc byte[sizeof(uint)];
            Assert.True(_memory.TryRead(Base + offset, value));
            return BinaryPrimitives.ReadUInt32LittleEndian(value);
        }

        public void Dispose() => _memory.Dispose();
    }

    [Theory]
    [InlineData(1u, 0x1122_3344_5566_7788UL)]
    [InlineData(0u, 0UL)]
    public unsafe void GroupedAccessesRestoreTheStackAndFlagsBeforeAConditionalBranch(uint value, ulong expected)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var argument = OperatingSystem.IsWindows() ? (byte)0x81 : (byte)0x87;
        var compareArgument = OperatingSystem.IsWindows() ? (byte)0x79 : (byte)0x7F;
        byte[] function =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0x48, 0x89, 0x44, 0x24, 0xF8,
            0x8B, argument, 0x00, 0x01, 0x00, 0x00,
            0x83, compareArgument, 0x40, 0x00,
            0x74, 0x06,
            0x48, 0x8B, 0x44, 0x24, 0xF8,
            0xC3,
            0x31, 0xC0,
            0xC3,
        ];
        using var image = PatchedImage.Create(function, [(0x40, value)]);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(1, image.Result.PatchedSites);
            Assert.Equal(0, image.Result.FailedSites);
            Assert.Equal(0xE9, image.ReadByte(15));
            Assert.Equal(0x90, image.ReadByte(21));
            Assert.Equal(0x74, image.ReadByte(25));
        }
        Assert.Equal(expected, ((delegate* unmanaged<ulong, ulong>)image.Base)(image.Base));
    }

    [Theory]
    [InlineData(new byte[] { 0x90 }, 1)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0xF8 }, 2)]
    [InlineData(new byte[] { 0x9C, 0x9D }, 2)]
    [InlineData(new byte[] { 0xEB, 0x00 }, 2)]
    public void GroupingStopsAtStackAndControlFlowBoundaries(byte[] separator, int expectedSites)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var memory = new PhysicalVirtualMemory();
        var imageBase = memory.AllocateAt(0, 0x10000);
        byte[] function = [0x48, 0x89, 0x44, 0x24, 0xF8,
            0x8B, 0x81, 0x00, 0x01, 0x00, 0x00, .. separator,
            0x8B, 0x81, 0x00, 0x01, 0x00, 0x00, 0xC3];

        var result = PatchFunction(memory, imageBase, function);

        Assert.Equal(expectedSites, result.PatchedSites);
        Assert.Equal(0, result.FailedSites);
    }

    [Fact]
    public void GroupingPreservesBranchTargetsAndDisablesExpansionForIndirectJumps()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        Span<byte> target = stackalloc byte[1];
        foreach (var indirectJump in new[] { false, true })
        {
            using var memory = new PhysicalVirtualMemory();
            var imageBase = memory.AllocateAt(0, 0x10000);
            byte[] function = [0x48, 0x89, 0x44, 0x24, 0xF8,
                0x74, 0x06,
                0x8B, 0x81, 0x00, 0x01, 0x00, 0x00,
                0x8B, 0x81, 0x00, 0x01, 0x00, 0x00,
                0x8B, 0x81, 0x00, 0x01, 0x00, 0x00,
                .. (indirectJump ? new byte[] { 0xFF, 0xE0 } : new byte[] { 0xC3 })];

            var result = PatchFunction(memory, imageBase, function);

            Assert.Equal(indirectJump ? 3 : 2, result.PatchedSites);
            Assert.Equal(0, result.FailedSites);
            Assert.True(memory.TryRead(imageBase + 13, target));
            Assert.Equal(0xE9, target[0]);
        }
    }

    [Fact]
    public unsafe void GroupedRelocationPreservesRipRelativeDataFlagsAndRedZone()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var memory = new PhysicalVirtualMemory();
        var imageBase = memory.AllocateAt(0, 0x10000);
        byte[] function = [
            0x48, 0xC7, 0x44, 0x24, 0xF8, 0x2A, 0, 0, 0,
            0x8B, 0x05, 0xF1, 0x01, 0, 0,
            0x83, 0xC0, 0x01,
            0x0F, 0x94, 0xC2,
            0x88, 0x15, 0xED, 0x01, 0, 0,
            0x48, 0x8B, 0x44, 0x24, 0xF8, 0xC3];
        Assert.True(memory.TryWrite(imageBase + 0x200, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));

        var result = PatchFunction(memory, imageBase, function);

        Assert.Equal(1, result.PatchedSites);
        Assert.Equal(0, result.FailedSites);
        Assert.Equal(42UL, ((delegate* unmanaged<ulong>)imageBase)());
        Span<byte> zeroFlag = stackalloc byte[1];
        Assert.True(memory.TryRead(imageBase + 0x208, zeroFlag));
        Assert.Equal(1, zeroFlag[0]);
    }

    [Fact]
    public void LargeGroupsHaveEnoughTrampolineSpace()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var memory = new PhysicalVirtualMemory();
        var imageBase = memory.AllocateAt(0, 0x10000);
        var function = new List<byte> { 0x48, 0x89, 0x44, 0x24, 0xF8 };
        for (var index = 0; index < 1000; index++)
            function.AddRange(new byte[] { 0x8B, 0x81, 0, 1, 0, 0 });
        function.Add(0xC3);

        var result = PatchFunction(memory, imageBase, function.ToArray());

        Assert.Equal(48, result.PatchedSites);
        Assert.Equal(0, result.FailedSites);
        Assert.True(result.TrampolineBytes > 6000);
    }

    [Fact]
    public void BackwardSpanDoesNotOverwriteAnEarlierPatch()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64) return;

        using var memory = new PhysicalVirtualMemory();
        var imageBase = memory.AllocateAt(0, 0x10000);
        byte[] function = [0x48, 0x89, 0x44, 0x24, 0xF8,
            0x8B, 0x81, 0, 1, 0, 0,
            0x8B, 0x01, 0xFF, 0xE0];

        var result = PatchFunction(memory, imageBase, function);

        Assert.Equal(1, result.PatchedSites);
        Assert.Equal(1, result.UnrelocatableSites);
        Assert.Equal(0, result.FailedSites);
        Span<byte> retained = stackalloc byte[2];
        Assert.True(memory.TryRead(imageBase + 11, retained));
        Assert.Equal(new byte[] { 0x8B, 0x01 }, retained.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void GroupedFaultResumesWithoutRepeatingWrites(bool framePointer)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var memory = new PhysicalVirtualMemory();
        var imageBase = memory.AllocateAt(0, 0x10000);
        var guardedAddress = imageBase + 0x4000;
        byte[] function = [
            0x48, 0xC7, 0x44, 0x24, 0xF8, 0x2A, 0, 0, 0,
            0x48, 0x8B, 0x02,
            0x49, 0xFF, 0x00,
            0x48, 0x8B, 0x01,
            0x48, 0x89, 0x02,
            0x48, 0x8B, 0x44, 0x24, 0xF8, 0xC3];
        if (framePointer)
            function = [0x55, 0x48, 0x89, 0xE5,
                0x48, 0xC7, 0x45, 0xF8, 0x2A, 0, 0, 0,
                0x48, 0x8B, 0x02, 0x49, 0xFF, 0x00,
                0x48, 0x8B, 0x01, 0x48, 0x89, 0x02,
                0x48, 0x8B, 0x45, 0xF8, 0x5D, 0xC3];
        var result = PatchFunction(memory, imageBase, function);
        Assert.Equal(1, result.PatchedSites);
        Assert.Equal(0, result.FailedSites);
        *(ulong*)guardedAddress = 0x123456789ABCDEF0;

        var kernelModule = NativeLibrary.Load("kernel32.dll");
        var protect = (delegate* unmanaged<ulong, nuint, uint, uint*, int>)NativeLibrary.GetExport(kernelModule, "VirtualProtect");
        var addHandler = (delegate* unmanaged<uint, ulong, nint>)NativeLibrary.GetExport(kernelModule, "AddVectoredExceptionHandler");
        var removeHandler = (delegate* unmanaged<nint, uint>)NativeLibrary.GetExport(kernelModule, "RemoveVectoredExceptionHandler");
        var assembler = new Assembler(64);
        var reject = assembler.CreateLabel();
        assembler.mov(rax, __qword_ptr[rcx]);
        assembler.cmp(__dword_ptr[rax], unchecked((int)0xC0000005));
        assembler.jne(reject);
        assembler.mov(rdx, guardedAddress);
        assembler.cmp(__qword_ptr[rax + 40], rdx);
        assembler.jne(reject);
        assembler.mov(rcx, rdx);
        assembler.sub(rsp, 40);
        assembler.mov(edx, 4096);
        assembler.mov(r8d, 4);
        assembler.lea(r9, __[rsp + 32]);
        assembler.mov(rax, (ulong)protect);
        assembler.call(rax);
        assembler.neg(eax);
        assembler.add(rsp, 40);
        assembler.ret();
        assembler.Label(ref reject);
        assembler.xor(eax, eax);
        assembler.ret();
        using var handlerBytes = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(handlerBytes), imageBase + 0x6000);
        Assert.True(memory.TryWrite(imageBase + 0x6000, handlerBytes.ToArray()));
        uint previousProtection;
        nint handler = 0;
        try
        {
            handler = addHandler(1, imageBase + 0x6000);
            Assert.NotEqual(0, handler);
            Assert.NotEqual(0, protect(guardedAddress, 4096, 1, &previousProtection));
            ulong writeCount = 0, output = 0;
            var sentinel = ((delegate* unmanaged<ulong, ulong*, ulong*, ulong>)imageBase)(guardedAddress, &output, &writeCount);
            Assert.Equal(42UL, sentinel);
            Assert.Equal(1UL, writeCount);
            Assert.Equal(0x123456789ABCDEF0UL, output);
        }
        finally
        {
            protect(guardedAddress, 4096, 4, &previousProtection);
            if (handler != 0)
                removeHandler(handler);
            NativeLibrary.Free(kernelModule);
        }
    }

    private static GuestRedZonePatcher.PatchResult PatchFunction(
        PhysicalVirtualMemory memory, ulong imageBase, byte[] function)
    {
        Assert.True(memory.TryWrite(imageBase, function));
        var exceptionFrameHeader = new byte[32];
        exceptionFrameHeader[0] = 1;
        exceptionFrameHeader[2] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(exceptionFrameHeader.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(exceptionFrameHeader.AsSpan(16), imageBase);
        Assert.True(memory.TryWrite(imageBase + 0x8000, exceptionFrameHeader));
        ProgramHeader[] headers = [
            CreateProgramHeader(ProgramHeaderType.Load, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute,
                0, (ulong)function.Length),
            CreateProgramHeader(ProgramHeaderType.GnuEhFrame, ProgramHeaderFlags.Read, 0x8000, 32)];
        return GuestRedZonePatcher.Patch(memory, memory, headers, imageBase, 0x10000);
    }

    private static ProgramHeader CreateProgramHeader(
        ProgramHeaderType type, ProgramHeaderFlags flags, ulong address, ulong size)
    {
        Span<byte> bytes = stackalloc byte[56];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)flags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], size);
        return MemoryMarshal.Read<ProgramHeader>(bytes);
    }

    private static Instruction Decode(byte[] instructionBytes)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(instructionBytes));
        decoder.Decode(out var instruction);
        Assert.NotEqual(Code.INVALID, instruction.Code);
        return instruction;
    }
}
