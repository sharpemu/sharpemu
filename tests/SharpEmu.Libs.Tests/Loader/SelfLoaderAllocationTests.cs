// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class SelfLoaderAllocationTests
{
    private const ulong Ps5MainImageBase = 0x0000000800000000UL;

    [Fact]
    public void Load_ExactBaseUnavailable_FallsBackToAlternativeAddress()
    {
        var imageData = BuildMinimalElf();
        using var host = new BlockingHostMemory(Ps5MainImageBase);
        using var memory = new PhysicalVirtualMemory(host);
        var loader = new SelfLoader();

        var image = loader.Load(imageData, memory);

        Assert.NotEqual(0UL, image.ImageBase);
        Assert.NotEqual(Ps5MainImageBase, image.ImageBase);
    }

    /// <summary>
    /// Builds a minimal x86-64 ELF with a single PT_LOAD segment and ABI version 2,
    /// which causes the loader to request the PS5 main image base.
    /// </summary>
    private static byte[] BuildMinimalElf()
    {
        const int elfHeaderSize = 64;
        const int programHeaderSize = 56;
        const int segmentSize = 16;
        const int fileSize = elfHeaderSize + programHeaderSize + segmentSize;

        var buffer = new byte[fileSize];
        var span = buffer.AsSpan();

        // e_ident
        span[0] = 0x7F;
        span[1] = (byte)'E';
        span[2] = (byte)'L';
        span[3] = (byte)'F';
        span[4] = 2; // ELFCLASS64
        span[5] = 1; // ELFDATA2LSB
        span[6] = 1; // EV_CURRENT
        span[7] = 0; // ELFOSABI_NONE
        span[8] = 2; // ABI version 2 -> treated as next-gen/PS5
        // Remaining ident bytes are zero.

        // e_type = ET_DYN (PIE)
        WriteUInt16LittleEndian(span, 16, 3);
        // e_machine = EM_X86_64
        WriteUInt16LittleEndian(span, 18, 62);
        // e_version
        WriteUInt32LittleEndian(span, 20, 1);
        // e_entry
        WriteUInt64LittleEndian(span, 24, 0);
        // e_phoff
        WriteUInt64LittleEndian(span, 32, elfHeaderSize);
        // e_shoff
        WriteUInt64LittleEndian(span, 40, 0);
        // e_flags
        WriteUInt32LittleEndian(span, 48, 0);
        // e_ehsize
        WriteUInt16LittleEndian(span, 52, elfHeaderSize);
        // e_phentsize
        WriteUInt16LittleEndian(span, 54, programHeaderSize);
        // e_phnum
        WriteUInt16LittleEndian(span, 56, 1);
        // e_shentsize
        WriteUInt16LittleEndian(span, 58, 0);
        // e_shnum
        WriteUInt16LittleEndian(span, 60, 0);
        // e_shstrndx
        WriteUInt16LittleEndian(span, 62, 0);

        var phOffset = elfHeaderSize;
        // p_type = PT_LOAD
        WriteUInt32LittleEndian(span, phOffset + 0, 1);
        // p_flags = PF_R | PF_X
        WriteUInt32LittleEndian(span, phOffset + 4, 0x5);
        // p_offset
        WriteUInt64LittleEndian(span, phOffset + 8, (ulong)(elfHeaderSize + programHeaderSize));
        // p_vaddr
        WriteUInt64LittleEndian(span, phOffset + 16, 0);
        // p_paddr
        WriteUInt64LittleEndian(span, phOffset + 24, 0);
        // p_filesz
        WriteUInt64LittleEndian(span, phOffset + 32, segmentSize);
        // p_memsz
        WriteUInt64LittleEndian(span, phOffset + 40, segmentSize);
        // p_align
        WriteUInt64LittleEndian(span, phOffset + 48, 0x1000);

        // Segment data: a few NOPs followed by RET.
        var segmentData = new byte[] { 0x90, 0x90, 0x90, 0x90, 0xC3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        segmentData.CopyTo(span.Slice(elfHeaderSize + programHeaderSize, segmentSize));

        return buffer;
    }

    private static void WriteUInt16LittleEndian(Span<byte> buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteUInt32LittleEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt64LittleEndian(Span<byte> buffer, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            buffer[offset + i] = (byte)((value >> (i * 8)) & 0xFF);
        }
    }

    /// <summary>
    /// Host memory implementation that blocks a specific exact address and
    /// returns a real, page-aligned allocation when asked to allocate at any
    /// address. This allows the loader to actually copy segment data without
    /// touching unmapped memory.
    /// </summary>
    private sealed class BlockingHostMemory : IHostMemory, IDisposable
    {
        private const ulong PageSize = 0x1000;
        private readonly ulong _blockedAddress;
        private readonly Dictionary<ulong, AllocationInfo> _allocations = new();
        private ulong? _fallbackAddress;

        public BlockingHostMemory(ulong blockedAddress)
        {
            _blockedAddress = blockedAddress;
        }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            if (desiredAddress == _blockedAddress)
            {
                return 0;
            }

            if (desiredAddress == 0)
            {
                if (_fallbackAddress is null)
                {
                    var allocation = AllocateReal(size);
                    _fallbackAddress = allocation.Address;
                    _allocations[_fallbackAddress.Value] = allocation;
                }

                return _fallbackAddress.Value;
            }

            if (_allocations.TryGetValue(desiredAddress, out var existing))
            {
                return existing.Address;
            }

            var info = AllocateReal(size);
            _allocations[info.Address] = info;
            return info.Address;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            Allocate(desiredAddress, size, protection);

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address)
        {
            if (_allocations.Remove(address, out var info))
            {
                Marshal.FreeHGlobal(info.OriginalPointer);
                if (_fallbackAddress == address)
                {
                    _fallbackAddress = null;
                }

                return true;
            }

            return true;
        }

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
            foreach (var info in _allocations.Values)
            {
                Marshal.FreeHGlobal(info.OriginalPointer);
            }

            _allocations.Clear();
            _fallbackAddress = null;
        }

        private static AllocationInfo AllocateReal(ulong size)
        {
            var alignedSize = (size + PageSize - 1) & ~(PageSize - 1);
            var allocation = Marshal.AllocHGlobal(checked((nint)(alignedSize + PageSize)));
            if (allocation == IntPtr.Zero)
            {
                throw new OutOfMemoryException("Test host memory could not allocate real memory.");
            }

            var address = ((ulong)allocation + PageSize - 1) & ~(PageSize - 1);
            return new AllocationInfo(address, allocation);
        }

        private readonly record struct AllocationInfo(ulong Address, IntPtr OriginalPointer);
    }
}
