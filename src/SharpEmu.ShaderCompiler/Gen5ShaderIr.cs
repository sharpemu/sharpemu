// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public enum Gen5ShaderEncoding
{
    Sop1,
    Sop2,
    Sopc,
    Sopp,
    Sopk,
    Smrd,
    Smem,
    Mubuf,
    Mtbuf,
    Vop1,
    Vop2,
    Vopc,
    Vop3,
    Vintrp,
    Ds,
    Flat,
    Vop3p,
    Mimg,
    Exp,
}

public enum Gen5OperandKind
{
    ScalarRegister,
    VectorRegister,
    EncodedConstant,
    LiteralConstant,
}

public enum Gen5PixelOutputKind
{
    Float,
    Uint,
    Sint,
}

/// <summary>
/// Selects the logical shader component for each physical color component.
/// Each component uses two bits in <see cref="Packed"/>.
/// </summary>
public readonly record struct Gen5ColorComponentMapping
{
    public const byte IdentityPacked = 0xE4;
    private readonly byte _encoded;

    public Gen5ColorComponentMapping(byte packed)
    {
        _encoded = (byte)(packed ^ IdentityPacked);
    }

    public byte Packed => (byte)(_encoded ^ IdentityPacked);

    public static Gen5ColorComponentMapping Identity { get; } = new(IdentityPacked);

    public uint Map(uint physicalComponent) =>
        physicalComponent < 4
            ? (uint)(Packed >> checked((int)(physicalComponent * 2))) & 0x3u
            : physicalComponent;

    public uint ApplyMask(uint logicalMask)
    {
        var mappedMask = 0u;
        for (var physicalComponent = 0u; physicalComponent < 4; physicalComponent++)
        {
            mappedMask |= ((logicalMask >> checked((int)Map(physicalComponent))) & 1u)
                << checked((int)physicalComponent);
        }

        return mappedMask;
    }

    public Gen5ColorComponentMapping Then(Gen5ColorComponentMapping next)
    {
        var packed = 0u;
        for (var physicalComponent = 0u; physicalComponent < 4; physicalComponent++)
        {
            packed |= next.Map(Map(physicalComponent))
                << checked((int)(physicalComponent * 2));
        }

        return new Gen5ColorComponentMapping(checked((byte)packed));
    }

    public bool IsIdentity => Packed == IdentityPacked;

    public static bool TryResolveRenderTarget(
        uint componentSwap,
        uint componentCount,
        out Gen5ColorComponentMapping mapping)
    {
        var packed = (componentSwap, componentCount) switch
        {
            (0, >= 1 and <= 4) => IdentityPacked,
            (1, 1) => 0xE1,
            (1, 2) => 0x6C,
            (1, 3) => 0xB4,
            (1, 4) => 0xC6,
            (2, 1) => 0xC6,
            (2, 2) => 0xE1,
            (2, 3) => 0xC6,
            (2, 4) => 0x1B,
            (3, 1) => 0x27,
            (3, 2) => 0x63,
            (3, 3) => 0x87,
            (3, 4) => 0x93,
            _ => -1,
        };
        mapping = packed >= 0
            ? new Gen5ColorComponentMapping(checked((byte)packed))
            : default;
        return packed >= 0;
    }
}

public readonly record struct Gen5PixelOutputBinding(
    uint GuestSlot,
    uint HostLocation,
    Gen5PixelOutputKind Kind,
    Gen5ColorComponentMapping ComponentMapping)
{
    public Gen5PixelOutputBinding(
        uint guestSlot,
        uint hostLocation,
        Gen5PixelOutputKind kind)
        : this(guestSlot, hostLocation, kind, Gen5ColorComponentMapping.Identity)
    {
    }
}

public readonly record struct Gen5ComputeSystemRegisters(
    uint? WorkGroupXRegister,
    uint? WorkGroupYRegister,
    uint? WorkGroupZRegister,
    uint? ThreadGroupSizeRegister);

public readonly record struct Gen5Operand(Gen5OperandKind Kind, uint Value)
{
    public static Gen5Operand Scalar(uint index) =>
        new(Gen5OperandKind.ScalarRegister, index);

    public static Gen5Operand Vector(uint index) =>
        new(Gen5OperandKind.VectorRegister, index);

    public static Gen5Operand Source(uint encoded, uint? literal = null)
    {
        if (encoded >= 256)
        {
            return Vector(encoded - 256);
        }

        if (encoded is 249 or 255 && literal.HasValue)
        {
            return new(Gen5OperandKind.LiteralConstant, literal.Value);
        }

        // 108..123 are the temporary SGPRs (TTMP0..TTMP15).  They use the
        // same scalar register file as ordinary SGPRs; treating them as
        // encoded constants loses values in shader continuations that spill
        // through a temporary register.
        if (encoded <= 105 || encoded is 106 or 107 or (>= 108 and <= 123) or 124 or 126 or 127)
        {
            return Scalar(encoded);
        }

        return new(Gen5OperandKind.EncodedConstant, encoded);
    }

    public override string ToString() => Kind switch
    {
        Gen5OperandKind.ScalarRegister => $"s{Value}",
        Gen5OperandKind.VectorRegister => $"v{Value}",
        Gen5OperandKind.LiteralConstant => $"0x{Value:X8}",
        _ => $"src[{Value}]",
    };
}

public abstract record Gen5InstructionControl;

public sealed record Gen5ImageControl(
    uint Dmask,
    uint VectorAddress,
    IReadOnlyList<uint> AddressRegisters,
    uint VectorData,
    uint ScalarResource,
    uint ScalarSampler,
    uint Dimension,
    bool IsArray,
    bool Glc,
    bool Slc,
    bool A16,
    bool D16) : Gen5InstructionControl
{
    public uint GetAddressRegister(int component) =>
        component < AddressRegisters.Count
            ? AddressRegisters[component]
            : VectorAddress + (uint)component;
}

public sealed record Gen5RayIntersectControl(
    uint VectorAddress,
    IReadOnlyList<uint> AddressRegisters,
    uint VectorData,
    uint ScalarResource,
    bool A16) : Gen5InstructionControl
{
    public const uint ResultDwords = 4;

    public uint GetAddressRegister(int component) =>
        component < AddressRegisters.Count
            ? AddressRegisters[component]
            : VectorAddress + (uint)component;
}

public sealed record Gen5GlobalMemoryControl(
    uint DwordCount,
    uint VectorAddress,
    uint SourceVectorRegister,
    uint DestinationVectorRegister,
    uint ScalarAddress,
    int OffsetBytes,
    bool Glc,
    bool Slc,
    bool UsesFlatAddress = false,
    uint? DynamicOffsetRegister = null,
    bool SourceIsScalar = false) : Gen5InstructionControl;

// A typed access carries the unified format from the instruction; a formatted
// untyped access reads the descriptor format when it executes.
public sealed record Gen5BufferMemoryControl(
    uint DwordCount,
    uint VectorAddress,
    uint VectorData,
    uint ScalarResource,
    int OffsetBytes,
    bool IndexEnabled,
    bool OffsetEnabled,
    bool Glc,
    bool Slc,
    bool Typed = false,
    uint TypedFormat = 0) : Gen5InstructionControl;

public sealed record Gen5ExportControl(
    uint Target,
    uint EnableMask,
    bool Compressed,
    bool Done,
    bool ValidMask) : Gen5InstructionControl;

public sealed record Gen5InterpolationControl(
    uint Attribute,
    uint Channel) : Gen5InstructionControl;

public sealed record Gen5Vop3Control(
    uint AbsoluteMask,
    uint NegateMask,
    uint OutputModifier,
    bool Clamp,
    uint OperandSelect,
    uint? ScalarDestination) : Gen5InstructionControl;

public sealed record Gen5SdwaControl(
    uint DestinationSelect,
    uint DestinationUnused,
    uint Source0Select,
    uint Source1Select,
    bool Source0SignExtend,
    bool Source1SignExtend,
    uint AbsoluteMask,
    uint NegateMask,
    uint OutputModifier,
    bool Clamp,
    uint? ScalarDestination) : Gen5InstructionControl;

// Packed (VOP3P) source and destination modifiers. Each mask holds one bit per
// source operand. OpSel/OpSelHi pick which 16-bit half of a source feeds the low
// and high result lanes respectively; NegLo/NegHi negate the value routed to each
// lane. Clamp saturates each output half to [0, 1].
public sealed record Gen5Vop3pControl(
    uint OpSelMask,
    uint OpSelHiMask,
    uint NegLoMask,
    uint NegHiMask,
    bool Clamp) : Gen5InstructionControl;

public sealed record Gen5DppControl(
    uint Control,
    bool FetchInactive,
    bool BoundControl,
    uint AbsoluteMask,
    uint NegateMask,
    uint BankMask,
    uint RowMask) : Gen5InstructionControl;

public sealed record Gen5Dpp8Control(
    uint LaneSelectors,
    bool FetchInactive) : Gen5InstructionControl;

public sealed record Gen5ScalarMemoryControl(
    uint DestinationCount,
    int ImmediateOffsetBytes,
    uint? DynamicOffsetRegister) : Gen5InstructionControl;

public sealed record Gen5DataShareControl(
    uint Offset0,
    uint Offset1,
    bool Gds) : Gen5InstructionControl
{
    // Single-address DS instructions encode one 16-bit byte offset across
    // OFFSET0 and OFFSET1. The paired read2/write2 forms instead interpret
    // them as two independent scaled 8-bit offsets.
    public uint SingleOffsetBytes => Offset0 | (Offset1 << 8);
}

public sealed record Gen5ShaderInstruction(
    uint Pc,
    Gen5ShaderEncoding Encoding,
    string Opcode,
    IReadOnlyList<uint> Words,
    IReadOnlyList<Gen5Operand> Sources,
    IReadOnlyList<Gen5Operand> Destinations,
    Gen5InstructionControl? Control);

public sealed record Gen5ShaderProgram(
    ulong Address,
    IReadOnlyList<Gen5ShaderInstruction> Instructions)
{
    private const uint PixelColorTargetCount = 8;
    private const int PixelColorMaskBits = 4;
    private readonly uint _pixelColorExportMasks = ComputePixelColorExportMasks(Instructions);
    private readonly uint _parameterExportMask = ComputeParameterExportMask(Instructions);

    public uint PixelColorExportMasks => _pixelColorExportMasks;

    public uint ParameterExportMask => _parameterExportMask;

    private static uint ComputePixelColorExportMasks(
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        var masks = 0u;
        foreach (var instruction in instructions)
        {
            if (instruction.Control is Gen5ExportControl export &&
                export.Target < PixelColorTargetCount)
            {
                masks |= (export.EnableMask & 0xFu) <<
                    (int)(export.Target * PixelColorMaskBits);
            }
        }

        return masks;
    }

    private static uint ComputeParameterExportMask(
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        var mask = 0u;
        foreach (var instruction in instructions)
        {
            if (instruction.Control is Gen5ExportControl export &&
                export.Target is >= 32 and < 64 &&
                export.EnableMask != 0)
            {
                mask |= 1u << (int)(export.Target - 32);
            }
        }

        return mask;
    }

    public IEnumerable<Gen5ImageControl> ImageResources =>
        Instructions
            .Select(instruction => instruction.Control)
            .OfType<Gen5ImageControl>();
}
