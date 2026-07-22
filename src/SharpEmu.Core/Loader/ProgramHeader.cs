// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.Core.Loader;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct ProgramHeader
{

    public const int ExpectedSize = 56;

    private readonly uint _type;
    private readonly uint _flags;
    private readonly ulong _offset;
    private readonly ulong _virtualAddress;
    private readonly ulong _physicalAddress;
    private readonly ulong _fileSize;
    private readonly ulong _memorySize;
    private readonly ulong _alignment;

    static ProgramHeader()
    {
        
        int actualSize = Unsafe.SizeOf<ProgramHeader>();
        if (actualSize != ExpectedSize)
        {
            throw new InvalidOperationException(
                $"{nameof(ProgramHeader)} layout drift detected: expected " +
                $"{ExpectedSize} bytes (ELF64_Phdr), but got {actualSize}. " +
                "This struct is read directly from raw ELF bytes; its " +
                "field order and size must exactly match the ELF64 spec.");
        }
    }

    public uint Type => _type;

    public ProgramHeaderType HeaderType => (ProgramHeaderType)_type;

    public uint RawFlags => _flags;

    public ProgramHeaderFlags Flags => (ProgramHeaderFlags)_flags;

    public ulong Offset => _offset;

    public ulong VirtualAddress => _virtualAddress;

    public ulong PhysicalAddress => _physicalAddress;

    public ulong FileSize => _fileSize;

    public ulong MemorySize => _memorySize;

    public ulong Alignment => _alignment;
}

public enum ProgramHeaderType : uint
{
    Null = 0,
    Load = 1,
    Dynamic = 2,
    Interp = 3,
    Note = 4,
    ShLib = 5,
    Phdr = 6,
    Tls = 7,
    GnuEhFrame = 0x6474E550,
    SceRela = 0x60000000,
    SceProcParam = 0x61000001,
    SceDynLibData = 0x61000000,
    SceRelro = 0x61000010,
}

[Flags]
public enum ProgramHeaderFlags : uint
{
    None = 0,
    Execute = 0x1,
    Write = 0x2,
    Read = 0x4,
}
