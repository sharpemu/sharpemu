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

public enum Gen5ShaderResourceKind
{
    ReadOnlyTexture,
    ReadWriteTexture,
    Sampler,
    ConstantBuffer,
}

public enum Gen5PixelOutputKind
{
    Float,
    Uint,
    Sint,
}

public readonly record struct Gen5PixelOutputBinding(
    uint GuestSlot,
    uint HostLocation,
    Gen5PixelOutputKind Kind);

public readonly record struct Gen5ShaderResourceMapping(
    Gen5ShaderResourceKind Kind,
    uint Slot,
    uint OffsetDwords,
    bool SizeFlag);

public sealed record Gen5ShaderMetadata(
    uint ExtendedUserDataSizeDwords,
    uint ShaderResourceTableSizeDwords,
    IReadOnlyDictionary<uint, uint> DirectResources,
    IReadOnlyList<Gen5ShaderResourceMapping> Resources);

public readonly record struct Gen5ComputeSystemRegisters(
    uint? WorkGroupXRegister,
    uint? WorkGroupYRegister,
    uint? WorkGroupZRegister,
    uint? ThreadGroupSizeRegister)
{
    public bool TryGetExpression(uint scalarRegister, out string expression)
    {
        if (WorkGroupXRegister == scalarRegister)
        {
            expression = "gl_WorkGroupID.x";
            return true;
        }

        if (WorkGroupYRegister == scalarRegister)
        {
            expression = "gl_WorkGroupID.y";
            return true;
        }

        if (WorkGroupZRegister == scalarRegister)
        {
            expression = "gl_WorkGroupID.z";
            return true;
        }

        if (ThreadGroupSizeRegister == scalarRegister)
        {
            expression = "(gl_WorkGroupSize.x * gl_WorkGroupSize.y * gl_WorkGroupSize.z)";
            return true;
        }

        expression = string.Empty;
        return false;
    }

    public void ClearStaticValues(Span<uint> scalarRegisters)
    {
        ClearStaticValue(scalarRegisters, WorkGroupXRegister);
        ClearStaticValue(scalarRegisters, WorkGroupYRegister);
        ClearStaticValue(scalarRegisters, WorkGroupZRegister);
        ClearStaticValue(scalarRegisters, ThreadGroupSizeRegister);
    }

    private static void ClearStaticValue(Span<uint> scalarRegisters, uint? scalarRegister)
    {
        if (scalarRegister is { } register && register < scalarRegisters.Length)
        {
            scalarRegisters[(int)register] = 0;
        }
    }
}

public sealed record Gen5ShaderState(
    Gen5ShaderProgram Program,
    IReadOnlyList<uint> UserData,
    Gen5ShaderMetadata? Metadata,
    Gen5ComputeSystemRegisters? ComputeSystemRegisters = null,
    uint UserDataScalarRegisterBase = 0);

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

        if (encoded <= 105 || encoded is 106 or 107 or 124 or 126 or 127)
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

public sealed record Gen5GlobalMemoryControl(
    uint DwordCount,
    uint VectorAddress,
    uint VectorData,
    uint ScalarAddress,
    int OffsetBytes,
    bool Glc,
    bool Slc) : Gen5InstructionControl;

public sealed record Gen5BufferMemoryControl(
    uint DwordCount,
    uint VectorAddress,
    uint VectorData,
    uint ScalarResource,
    int OffsetBytes,
    bool IndexEnabled,
    bool OffsetEnabled,
    bool Glc,
    bool Slc) : Gen5InstructionControl;

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
    bool Gds) : Gen5InstructionControl;

public sealed record Gen5ImageBinding(
    uint Pc,
    string Opcode,
    Gen5ImageControl Control,
    IReadOnlyList<uint> ResourceDescriptor,
    IReadOnlyList<uint> SamplerDescriptor,
    uint? MipLevel);

// Data arrays may be rented from ArrayPool (oversized): always slice with
// DataLength, never Data.Length. Ownership transfers to the presenter, which
// returns pooled arrays after uploading them into host-visible buffers.
public sealed record Gen5GlobalMemoryBinding(
    uint ScalarAddress,
    ulong BaseAddress,
    IReadOnlyList<uint> InstructionPcs,
    byte[] Data,
    int DataLength,
    bool DataPooled)
{
    public bool Writable { get; set; }

    // Writable describes shader access and is also used to decide whether a
    // compute dispatch has observable work. A statically reachable resource
    // can nevertheless be unbound for the current scalar path; the evaluator
    // supplies zero-filled storage for Vulkan in that case. Such synthetic
    // storage must remain shader-writable, but must never be copied to the
    // descriptor's unmapped guest address.
    public bool WriteBackToGuest { get; set; } = true;
}

public sealed record Gen5VertexInputBinding(
    uint Pc,
    uint Location,
    uint ComponentCount,
    uint DataFormat,
    uint NumberFormat,
    ulong BaseAddress,
    uint Stride,
    uint OffsetBytes,
    byte[] Data,
    int DataLength,
    bool DataPooled);

public sealed record Gen5ShaderEvaluation(
    IReadOnlyList<uint> InitialScalarRegisters,
    IReadOnlyList<uint> ScalarRegisters,
    IReadOnlyList<Gen5ImageBinding> ImageBindings,
    IReadOnlyList<Gen5GlobalMemoryBinding> GlobalMemoryBindings,
    Gen5ComputeSystemRegisters? ComputeSystemRegisters = null,
    IReadOnlySet<uint>? RuntimeScalarRegisters = null,
    IReadOnlyList<Gen5VertexInputBinding>? VertexInputs = null);

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
    private const int ScalarRegisterCount = 256;
    private IReadOnlySet<uint>? _runtimeScalarRegisters;

    public uint PixelColorExportMasks => _pixelColorExportMasks;

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

    public IEnumerable<Gen5ImageControl> ImageResources =>
        Instructions
            .Select(instruction => instruction.Control)
            .OfType<Gen5ImageControl>();

    /// <summary>
    /// The set of scalar registers the program reads or writes as runtime
    /// values. It depends only on the (cached) decoded program, so it is
    /// computed once and shared read-only across every draw that uses this
    /// shader — the evaluator previously rebuilt this HashSet by scanning
    /// every instruction on every draw, one of the largest per-draw
    /// allocation and CPU sources.
    /// </summary>
    public IReadOnlySet<uint> RuntimeScalarRegisters =>
        _runtimeScalarRegisters ??= ComputeRuntimeScalarRegisters();

    private IReadOnlySet<uint> ComputeRuntimeScalarRegisters()
    {
        var registers = new HashSet<uint>();
        foreach (var instruction in Instructions)
        {
            foreach (var operand in instruction.Sources)
            {
                if (operand.Kind == Gen5OperandKind.ScalarRegister &&
                    operand.Value < ScalarRegisterCount)
                {
                    registers.Add(operand.Value);
                }
            }

            foreach (var operand in instruction.Destinations)
            {
                if (operand.Kind == Gen5OperandKind.ScalarRegister &&
                    operand.Value < ScalarRegisterCount)
                {
                    registers.Add(operand.Value);
                }
            }

            if (instruction.Control is Gen5ScalarMemoryControl
                {
                    DynamicOffsetRegister: { } offsetRegister,
                } &&
                offsetRegister < ScalarRegisterCount)
            {
                registers.Add(offsetRegister);
            }
        }

        return registers;
    }
}
