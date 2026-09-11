// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// The marker a preceding packet attaches to the next user scalar write.
public enum UserScalarKind : uint
{
    Unknown = 0,
    BufferResource = 1,
    Region = 2,
}

public sealed class UserScalarRegisters
{
    public const int Capacity = 32;

    public uint[] Values = new uint[Capacity];
    public UserScalarKind[] Kinds = new UserScalarKind[Capacity];
    public uint Count;

    public UserScalarRegisters Copy() => new()
    {
        Values = (uint[])Values.Clone(),
        Kinds = (UserScalarKind[])Kinds.Clone(),
        Count = Count,
    };

    public void Set(uint index, uint value, UserScalarKind kind)
    {
        Values[index] = value;
        Kinds[index] = kind;
        if (index + 1 > Count)
        {
            Count = index + 1;
        }
    }
}

public struct HullResource1
{
    public byte VectorRegisterCount;
    public byte Priority;
    public byte FloatMode;
    public bool Dx10Clamp;
    public bool DebugMode;
    public bool IeeeMode;
    public bool RequireForwardProgress;
    public bool ThreadgroupConfiguration;
    public byte LocalVectorComponentCount;
    public bool HalfPrecisionOverflow;

    public static HullResource1 Decode(uint value) => new()
    {
        VectorRegisterCount = (byte)RegisterField.Get(value, 0, 0x3F),
        Priority = (byte)RegisterField.Get(value, 10, 0x3),
        FloatMode = (byte)RegisterField.Get(value, 12, 0xFF),
        Dx10Clamp = RegisterField.Bit(value, 21),
        DebugMode = RegisterField.Bit(value, 22),
        IeeeMode = RegisterField.Bit(value, 23),
        RequireForwardProgress = RegisterField.Bit(value, 25),
        ThreadgroupConfiguration = RegisterField.Bit(value, 26),
        LocalVectorComponentCount = (byte)RegisterField.Get(value, 28, 0x3),
        HalfPrecisionOverflow = RegisterField.Bit(value, 30),
    };
}

public struct HullResource2
{
    public bool ScratchEnable;
    public byte UserScalarCount;
    public ushort LocalDataShareSize;
    public byte SharedVectorRegisters;

    public static HullResource2 Decode(uint value) => new()
    {
        ScratchEnable = RegisterField.Bit(value, 0),
        UserScalarCount = (byte)(RegisterField.Get(value, 1, 0x1F) + (RegisterField.Get(value, 27, 0x1) << 5)),
        LocalDataShareSize = (ushort)RegisterField.Get(value, 18, 0x1FF),
        SharedVectorRegisters = (byte)RegisterField.Get(value, 28, 0xF),
    };
}

public struct PixelResource1
{
    public byte VectorRegisterCount;
    public byte Priority;
    public byte FloatMode;
    public bool Dx10Clamp;
    public bool DebugMode;
    public bool IeeeMode;
    public bool ComputeUnitGroupDisable;
    public bool RequireForwardProgress;
    public bool HalfPrecisionOverflow;

    public static PixelResource1 Decode(uint value) => new()
    {
        VectorRegisterCount = (byte)RegisterField.Get(value, 0, 0x3F),
        Priority = (byte)RegisterField.Get(value, 10, 0x3),
        FloatMode = (byte)RegisterField.Get(value, 12, 0xFF),
        Dx10Clamp = RegisterField.Bit(value, 21),
        DebugMode = RegisterField.Bit(value, 22),
        IeeeMode = RegisterField.Bit(value, 23),
        ComputeUnitGroupDisable = RegisterField.Bit(value, 24),
        RequireForwardProgress = RegisterField.Bit(value, 26),
        HalfPrecisionOverflow = RegisterField.Bit(value, 29),
    };
}

public struct PixelResource2
{
    public bool ScratchEnable;
    public byte UserScalarCount;
    public bool WaveCountEnable;
    public byte ExtraLocalDataShareSize;
    public byte RasterOrderedShading;
    public byte SharedVectorRegisters;

    public static PixelResource2 Decode(uint value) => new()
    {
        ScratchEnable = RegisterField.Bit(value, 0),
        UserScalarCount = (byte)(RegisterField.Get(value, 1, 0x1F) + (RegisterField.Get(value, 27, 0x1) << 5)),
        WaveCountEnable = RegisterField.Bit(value, 7),
        ExtraLocalDataShareSize = (byte)RegisterField.Get(value, 8, 0xFF),
        RasterOrderedShading = (byte)RegisterField.Get(value, 25, 0x3),
        SharedVectorRegisters = (byte)RegisterField.Get(value, 28, 0xF),
    };
}

public struct GeometryResource1
{
    public byte VectorRegisterCount;
    public byte Priority;
    public byte FloatMode;
    public bool Dx10Clamp;
    public bool DebugMode;
    public bool IeeeMode;
    public bool ComputeUnitGroupEnable;
    public bool RequireForwardProgress;
    public bool ThreadgroupConfiguration;
    public byte GeometryVectorComponentCount;
    public bool HalfPrecisionOverflow;

    public static GeometryResource1 Decode(uint value) => new()
    {
        VectorRegisterCount = (byte)RegisterField.Get(value, 0, 0x3F),
        Priority = (byte)RegisterField.Get(value, 10, 0x3),
        FloatMode = (byte)RegisterField.Get(value, 12, 0xFF),
        Dx10Clamp = RegisterField.Bit(value, 21),
        DebugMode = RegisterField.Bit(value, 22),
        IeeeMode = RegisterField.Bit(value, 23),
        ComputeUnitGroupEnable = RegisterField.Bit(value, 24),
        RequireForwardProgress = RegisterField.Bit(value, 26),
        ThreadgroupConfiguration = RegisterField.Bit(value, 27),
        GeometryVectorComponentCount = (byte)RegisterField.Get(value, 29, 0x3),
        HalfPrecisionOverflow = RegisterField.Bit(value, 31),
    };
}

public struct GeometryResource2
{
    public bool ScratchEnable;
    public byte UserScalarCount;
    public byte ExportVectorComponentCount;
    public bool OffChipLocalDataShare;
    public byte LocalDataShareSize;
    public byte SharedVectorRegisters;

    public static GeometryResource2 Decode(uint value) => new()
    {
        ScratchEnable = RegisterField.Bit(value, 0),
        UserScalarCount = (byte)(RegisterField.Get(value, 1, 0x1F) + (RegisterField.Get(value, 27, 0x1) << 5)),
        ExportVectorComponentCount = (byte)RegisterField.Get(value, 16, 0x3),
        OffChipLocalDataShare = RegisterField.Bit(value, 18),
        LocalDataShareSize = (byte)RegisterField.Get(value, 19, 0xFF),
        SharedVectorRegisters = (byte)RegisterField.Get(value, 28, 0xF),
    };
}

public sealed class VertexStageRegisters
{
    public ulong ExportAddress;
    public ulong LocalAddress;
    public ulong HullAddress;
    public HullResource1 HullResource1;
    public HullResource2 HullResource2;
    public ulong GeometryAddress;
    public GeometryResource1 GeometryResource1;
    public GeometryResource2 GeometryResource2;
    public UserScalarRegisters HullUserScalars = new();
    public UserScalarRegisters GeometryUserScalars = new();
    // The legacy vertex block and the export user scalars are stored, not decoded.
    public ulong LegacyVertexAddress;
    public uint LegacyVertexResource1;
    public uint LegacyVertexResource2;
    public UserScalarRegisters LegacyVertexUserScalars = new();
    public UserScalarRegisters ExportUserScalars = new();

    public VertexStageRegisters Copy()
    {
        var copy = (VertexStageRegisters)MemberwiseClone();
        copy.HullUserScalars = HullUserScalars.Copy();
        copy.GeometryUserScalars = GeometryUserScalars.Copy();
        copy.LegacyVertexUserScalars = LegacyVertexUserScalars.Copy();
        copy.ExportUserScalars = ExportUserScalars.Copy();
        return copy;
    }
}

public sealed class PixelStageRegisters
{
    public ulong Address;
    public PixelResource1 Resource1;
    public PixelResource2 Resource2;
    public UserScalarRegisters UserScalars = new();

    public PixelStageRegisters Copy()
    {
        var copy = (PixelStageRegisters)MemberwiseClone();
        copy.UserScalars = UserScalars.Copy();
        return copy;
    }
}

public sealed class ComputeStageRegisters
{
    public ulong Address;
    public uint ProgramChecksum;
    public uint ThreadsX;
    public uint ThreadsY;
    public uint ThreadsZ;
    public byte VectorRegisterCount;
    public byte Priority;
    public byte FloatMode;
    public bool Dx10Clamp;
    public bool DebugMode;
    public bool IeeeMode;
    public bool RequireForwardProgress;
    public bool HalfPrecisionOverflow;
    public bool ThreadgroupConfiguration;
    public byte WaveSize = 64;
    public bool ScratchEnable;
    public byte UserScalarCount;
    public bool ThreadGroupIdXEnable;
    public bool ThreadGroupIdYEnable;
    public bool ThreadGroupIdZEnable;
    public bool ThreadGroupSizeEnable;
    public byte ThreadIdComponentCount;
    public ushort LocalDataShareSize;
    public byte SharedVectorRegisters;
    public UserScalarRegisters UserScalars = new();

    public ComputeStageRegisters Copy()
    {
        var copy = (ComputeStageRegisters)MemberwiseClone();
        copy.UserScalars = UserScalars.Copy();
        return copy;
    }
}

// The shader bank: the program registers of every stage and their user scalars.
public sealed class ShaderProgramRegisters
{
    public VertexStageRegisters Vertex = new();
    public PixelStageRegisters Pixel = new();
    public ComputeStageRegisters Compute = new();

    public ShaderProgramRegisters Copy() => new()
    {
        Vertex = Vertex.Copy(),
        Pixel = Pixel.Copy(),
        Compute = Compute.Copy(),
    };
}
