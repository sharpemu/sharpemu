// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial initializes AGC through versioned register-default structures.

    private const uint RegisterDefaultsVersion7 = 7;
    private const uint RegisterDefaultsVersion8 = 8;
    private const uint RegisterDefaultsVersion9 = 9;
    private const uint RegisterDefaultsVersion10 = 10;
    private const uint RegisterDefaultsVersion11 = 11;
    private const uint RegisterDefaultsVersion12 = 12;
    private const uint RegisterDefaultsVersion13 = 13;
    private const int RegisterDefaultsSize = 0x40;
    private const int RegisterDefaultBlockSize = 16 * 8;

    private static readonly object _registerDefaultsGate = new();
    private static readonly ConditionalWeakTable<object, Dictionary<uint, RegisterDefaultsAllocation>>
        _registerDefaultsAllocations = new();

    private static readonly RegisterDefaultGroup[] PrimaryRegisterDefaults =
        CreatePrimaryRegisterDefaults();

    private static readonly RegisterDefaultGroup[] InternalRegisterDefaults =
    [
        new(0, 0, 0x8FB4EDB5, [new(0x00E, 0)]),
        new(0, 1, 0xB994AD29, [new(0x2AF, 0)]),
        new(0, 2, 0xD427322F, [new(0x314, 0)]),
        new(0, 3, 0xF58FEA31, [new(0x1B5, 0)]),
        new(1, 0, 0x6AC156EF, [new(0x216, 0)]),
        new(1, 1, 0x6AC15610, [new(0x217, 0)]),
        new(1, 2, 0x6AC15009, [new(0x219, 0)]),
        new(1, 3, 0x6AC153BA, [new(0x21A, 0)]),
        new(1, 4, 0xBE7DCD73, [new(0x27D, 0)]),
        new(1, 5, 0x0C4B1438, [new(0x22A, 0)]),
        new(1, 6, 0xDB00D71A, [new(0x204, 0)]),
        new(1, 7, 0xDB00D249, [new(0x205, 0)]),
        new(1, 8, 0xDB00EC60, [new(0x206, 0)]),
        new(1, 9, 0x0C4D6FE4, [new(0x080, 0)]),
        new(1, 10, 0x0C4A80EF, [new(0x100, 0)]),
        new(1, 11, 0x0DD283E7, [new(0x006, 0)]),
        new(1, 12, 0xC620E68C, [new(0x081, 0)]),
        new(1, 13, 0xC67EFACF, [new(0x101, 0)]),
        new(1, 14, 0xD9E6D9F7, [new(0x001, 0)]),
        new(2, 0, 0x31F34B9F, [new(0x24F, 0)]),
        new(2, 1, 0xAC0F9E76, [new(0x80003FFF, 0)]),
        new(2, 2, 0x929FD95D, [new(0x250, 0)]),
    ];

    private sealed record CompactRegisterDefaults(
        uint[] Table0Registers,
        ushort[] Table0PointerOffsets,
        uint[] Table1Registers,
        ushort[] Table1PointerOffsets,
        uint[] Table2Registers,
        ushort[] Table2PointerOffsets,
        uint[] Table3Registers,
        ushort[] Table3PointerOffsets,
        uint[] Types);

    private readonly record struct RegisterDefaultGroup(
        uint Space,
        uint Index,
        uint Type,
        RegisterDefaultValue[] Registers);

    private sealed record RegisterDefaultsAllocation(ulong Primary, ulong Internal);

    // NID captured from shipped titles; 'sceAgcInit' is a working label that collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "23LRUSvYu1M",
        ExportName = "sceAgcInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int Init(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        var version = (uint)ctx[CpuRegister.Rsi];
        if (stateAddress == 0 || !IsSupportedRegisterDefaultsVersion(version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> state = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(state, version);
        BinaryPrimitives.WriteUInt32LittleEndian(state[sizeof(uint)..], 0);
        if (!ctx.Memory.TryWrite(stateAddress, state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.init state=0x{stateAddress:X16} version={version}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "2JtWUUiYBXs",
        ExportName = "sceAgcGetRegisterDefaults2",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: false);

    [SysAbiExport(
        Nid = "wRbq6ZjNop4",
        ExportName = "sceAgcGetRegisterDefaults2Internal",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2Internal(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: true);

    /// <summary>
    /// Reports that the GPU is not running in Trinity mode, matching the base
    /// console this backend emulates.
    /// </summary>
    [SysAbiExport(
        Nid = "BfBDZGbti7A",
        ExportName = "sceAgcGetIsTrinityMode",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetIsTrinityMode(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ReturnRegisterDefaults(CpuContext ctx, bool internalDefaults)
    {
        var version = (uint)ctx[CpuRegister.Rdi];
        if (!IsSupportedRegisterDefaultsVersion(version))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryGetRegisterDefaultsAllocation(ctx, version, out var allocation))
        {
            return ReturnPointer(ctx, 0);
        }

        var address = internalDefaults ? allocation.Internal : allocation.Primary;
        TraceAgc($"agc.get_register_defaults internal={internalDefaults} version={version} address=0x{address:X16}");
        return ReturnPointer(ctx, address);
    }

    private static bool IsSupportedRegisterDefaultsVersion(uint version)
    {
        return version is
            RegisterDefaultsVersion7 or
            RegisterDefaultsVersion8 or
            RegisterDefaultsVersion9 or
            RegisterDefaultsVersion10 or
            RegisterDefaultsVersion11 or
            RegisterDefaultsVersion12 or
            RegisterDefaultsVersion13;
    }

    private static bool TryGetRegisterDefaultsAllocation(
        CpuContext ctx,
        uint version,
        out RegisterDefaultsAllocation allocation)
    {
        lock (_registerDefaultsGate)
        {
            var memory = CanonicalMemory(ctx.Memory);
            if (!_registerDefaultsAllocations.TryGetValue(memory, out var allocations))
            {
                allocations = [];
                _registerDefaultsAllocations.Add(memory, allocations);
            }

            if (allocations.TryGetValue(version, out allocation!))
            {
                return true;
            }

            ulong primaryAddress = 0;
            ulong internalAddress = 0;
            var built = TrySelectCompactRegisterDefaults(version, out var publicDefaults, out var internalDefaults)
                ? TryBuildCompactRegisterDefaults(ctx, publicDefaults, out primaryAddress) &&
                  TryBuildCompactRegisterDefaults(ctx, internalDefaults, out internalAddress)
                : TryBuildRegisterDefaults(
                      ctx,
                      PrimaryRegisterDefaults,
                      cxTableLength: 78,
                      shTableLength: 29,
                      ucTableLength: 20,
                      out primaryAddress) &&
                  TryBuildRegisterDefaults(
                      ctx,
                      InternalRegisterDefaults,
                      cxTableLength: 4,
                      shTableLength: 15,
                      ucTableLength: 3,
                      out internalAddress);
            if (!built)
            {
                allocation = null!;
                return false;
            }

            allocation = new RegisterDefaultsAllocation(primaryAddress, internalAddress);
            allocations.Add(version, allocation);
            return true;
        }
    }

    private static bool TrySelectCompactRegisterDefaults(
        uint version,
        out CompactRegisterDefaults publicDefaults,
        out CompactRegisterDefaults internalDefaults)
    {
        switch (version)
        {
            case RegisterDefaultsVersion7:
                publicDefaults = PublicRegisterDefaultsVersion7;
                internalDefaults = InternalRegisterDefaultsVersion7;
                return true;
            case RegisterDefaultsVersion8:
                publicDefaults = PublicRegisterDefaultsVersion8;
                internalDefaults = InternalRegisterDefaultsVersion8;
                return true;
            case RegisterDefaultsVersion9:
                publicDefaults = PublicRegisterDefaultsVersion9;
                internalDefaults = InternalRegisterDefaultsVersion9;
                return true;
            case RegisterDefaultsVersion10:
            case RegisterDefaultsVersion12:
                // The AGC version table maps version 12 to the version 10 dataset.
                publicDefaults = PublicRegisterDefaultsVersion10;
                internalDefaults = InternalRegisterDefaultsVersion10;
                return true;
            case RegisterDefaultsVersion11:
                publicDefaults = PublicRegisterDefaultsVersion11;
                internalDefaults = InternalRegisterDefaultsVersion11;
                return true;
            case RegisterDefaultsVersion13 when !IsLegacyVersion13RegisterDefaultsRequested():
                // The exact version 13 data is not available. The available
                // open-source table uses version 11 for newer requests. Local
                // Astro Bot tests found no regression with this fallback.
                publicDefaults = PublicRegisterDefaultsVersion11;
                internalDefaults = InternalRegisterDefaultsVersion11;
                return true;
            default:
                publicDefaults = null!;
                internalDefaults = null!;
                return false;
        }
    }

    private static bool IsLegacyVersion13RegisterDefaultsRequested()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_AGC_VERSION13_DEFAULTS");
        return string.Equals(value, "legacy", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "generic", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "0", StringComparison.Ordinal);
    }

    private static bool TryBuildCompactRegisterDefaults(
        CpuContext ctx,
        CompactRegisterDefaults defaults,
        out ulong address)
    {
        address = 0;
        if (defaults.Types.Length % 3 != 0 ||
            !ArePointerOffsetsValid(defaults.Table0Registers, defaults.Table0PointerOffsets) ||
            !ArePointerOffsetsValid(defaults.Table1Registers, defaults.Table1PointerOffsets) ||
            !ArePointerOffsetsValid(defaults.Table2Registers, defaults.Table2PointerOffsets) ||
            !ArePointerOffsetsValid(defaults.Table3Registers, defaults.Table3PointerOffsets))
        {
            return false;
        }

        var table0Offset = AlignUp(RegisterDefaultsSize, sizeof(ulong));
        var table1Offset = table0Offset + (defaults.Table0PointerOffsets.Length * sizeof(ulong));
        var table2Offset = table1Offset + (defaults.Table1PointerOffsets.Length * sizeof(ulong));
        var table3Offset = table2Offset + (defaults.Table2PointerOffsets.Length * sizeof(ulong));
        var registers0Offset = AlignUp(
            table3Offset + (defaults.Table3PointerOffsets.Length * sizeof(ulong)),
            sizeof(ulong));
        var registers1Offset = registers0Offset + (defaults.Table0Registers.Length * sizeof(uint));
        var registers2Offset = registers1Offset + (defaults.Table1Registers.Length * sizeof(uint));
        var registers3Offset = registers2Offset + (defaults.Table2Registers.Length * sizeof(uint));
        var typesOffset = AlignUp(
            registers3Offset + (defaults.Table3Registers.Length * sizeof(uint)),
            sizeof(uint));
        var blobLength = typesOffset + (defaults.Types.Length * sizeof(uint));

        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, (ulong)blobLength, 0x1000, out address))
        {
            return false;
        }

        var blob = new byte[blobLength];
        WriteOptionalBlobPointer(blob, 0x00, address, table0Offset, defaults.Table0PointerOffsets.Length);
        WriteOptionalBlobPointer(blob, 0x08, address, table1Offset, defaults.Table1PointerOffsets.Length);
        WriteOptionalBlobPointer(blob, 0x10, address, table2Offset, defaults.Table2PointerOffsets.Length);
        WriteOptionalBlobPointer(blob, 0x18, address, table3Offset, defaults.Table3PointerOffsets.Length);
        WriteBlobUInt32(blob, 0x20, (uint)(defaults.Table0Registers.Length / 2));
        WriteBlobUInt32(blob, 0x24, (uint)(defaults.Table1Registers.Length / 2));
        WriteBlobUInt32(blob, 0x28, (uint)(defaults.Table2Registers.Length / 2));
        WriteBlobUInt32(blob, 0x2C, (uint)(defaults.Table3Registers.Length / 2));
        WriteBlobUInt64(blob, 0x30, address + (ulong)typesOffset);
        WriteBlobUInt32(blob, 0x38, (uint)(defaults.Types.Length / 3));

        WriteCompactPointerTable(blob, table0Offset, address + (ulong)registers0Offset, defaults.Table0PointerOffsets);
        WriteCompactPointerTable(blob, table1Offset, address + (ulong)registers1Offset, defaults.Table1PointerOffsets);
        WriteCompactPointerTable(blob, table2Offset, address + (ulong)registers2Offset, defaults.Table2PointerOffsets);
        WriteCompactPointerTable(blob, table3Offset, address + (ulong)registers3Offset, defaults.Table3PointerOffsets);
        WriteCompactRegisters(blob, registers0Offset, defaults.Table0Registers);
        WriteCompactRegisters(blob, registers1Offset, defaults.Table1Registers);
        WriteCompactRegisters(blob, registers2Offset, defaults.Table2Registers);
        WriteCompactRegisters(blob, registers3Offset, defaults.Table3Registers);
        for (var index = 0; index < defaults.Types.Length; index++)
        {
            WriteBlobUInt32(blob, typesOffset + (index * sizeof(uint)), defaults.Types[index]);
        }

        return ctx.Memory.TryWrite(address, blob);
    }

    private static bool ArePointerOffsetsValid(
        uint[] registers,
        ushort[] pointerOffsets)
    {
        if (registers.Length % 2 != 0)
        {
            return false;
        }

        if (pointerOffsets.Length == 0)
        {
            return registers.Length == 0;
        }

        return registers.Length != 0 && pointerOffsets.All(offset => offset < registers.Length / 2);
    }

    private static void WriteOptionalBlobPointer(
        Span<byte> blob,
        int fieldOffset,
        ulong address,
        int dataOffset,
        int elementCount) =>
        WriteBlobUInt64(blob, fieldOffset, elementCount == 0 ? 0 : address + (ulong)dataOffset);

    private static void WriteCompactPointerTable(
        Span<byte> blob,
        int tableOffset,
        ulong registersAddress,
        ushort[] pointerOffsets)
    {
        for (var index = 0; index < pointerOffsets.Length; index++)
        {
            WriteBlobUInt64(
                blob,
                tableOffset + (index * sizeof(ulong)),
                registersAddress + ((ulong)pointerOffsets[index] * 2 * sizeof(uint)));
        }
    }

    private static void WriteCompactRegisters(
        Span<byte> blob,
        int registersOffset,
        uint[] registers)
    {
        for (var index = 0; index < registers.Length; index++)
        {
            WriteBlobUInt32(blob, registersOffset + (index * sizeof(uint)), registers[index]);
        }
    }

    private static bool TryBuildRegisterDefaults(
        CpuContext ctx,
        RegisterDefaultGroup[] groups,
        int cxTableLength,
        int shTableLength,
        int ucTableLength,
        out ulong address)
    {
        var cxTableOffset = AlignUp(RegisterDefaultsSize, sizeof(ulong));
        var shTableOffset = cxTableOffset + (cxTableLength * sizeof(ulong));
        var ucTableOffset = shTableOffset + (shTableLength * sizeof(ulong));
        var typesOffset = AlignUp(ucTableOffset + (ucTableLength * sizeof(ulong)), sizeof(uint));
        var registerBlocksOffset = AlignUp(typesOffset + (groups.Length * 3 * sizeof(uint)), sizeof(ulong));
        var blobLength = registerBlocksOffset + (groups.Length * RegisterDefaultBlockSize);

        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, (ulong)blobLength, 0x1000, out address))
        {
            return false;
        }

        var blob = new byte[blobLength];
        WriteBlobUInt64(blob, 0x00, address + (ulong)cxTableOffset);
        WriteBlobUInt64(blob, 0x08, address + (ulong)shTableOffset);
        WriteBlobUInt64(blob, 0x10, address + (ulong)ucTableOffset);
        WriteBlobUInt64(blob, 0x30, address + (ulong)typesOffset);
        WriteBlobUInt32(blob, 0x38, (uint)groups.Length);

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            if (group.Registers.Length > 16)
            {
                return false;
            }

            var tableOffset = group.Space switch
            {
                0 => cxTableOffset,
                1 => shTableOffset,
                2 => ucTableOffset,
                _ => -1,
            };
            var tableLength = group.Space switch
            {
                0 => cxTableLength,
                1 => shTableLength,
                2 => ucTableLength,
                _ => 0,
            };
            if (tableOffset < 0 || group.Index >= tableLength)
            {
                return false;
            }

            var registerBlockOffset = registerBlocksOffset + (groupIndex * RegisterDefaultBlockSize);
            WriteBlobUInt64(
                blob,
                tableOffset + ((int)group.Index * sizeof(ulong)),
                address + (ulong)registerBlockOffset);

            var typeEntryOffset = typesOffset + (groupIndex * 3 * sizeof(uint));
            WriteBlobUInt32(blob, typeEntryOffset, group.Type);
            WriteBlobUInt32(blob, typeEntryOffset + sizeof(uint), (group.Index * 4) + group.Space);

            for (var registerIndex = 0; registerIndex < group.Registers.Length; registerIndex++)
            {
                var register = group.Registers[registerIndex];
                var registerOffset = registerBlockOffset + (registerIndex * 2 * sizeof(uint));
                WriteBlobUInt32(blob, registerOffset, register.Offset);
                WriteBlobUInt32(blob, registerOffset + sizeof(uint), register.Value);
            }
        }

        return ctx.Memory.TryWrite(address, blob);
    }

    private static int AlignUp(int value, int alignment) =>
        (value + alignment - 1) & -alignment;

    private static void WriteBlobUInt32(Span<byte> blob, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(blob[offset..], value);

    private static void WriteBlobUInt64(Span<byte> blob, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(blob[offset..], value);
}
