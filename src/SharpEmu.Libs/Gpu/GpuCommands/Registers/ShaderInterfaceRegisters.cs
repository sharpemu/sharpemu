// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// The shader interface registers of the context bank: vertex export, interpolation and stage layout.
public sealed class ShaderInterfaceRegisters
{
    public const int InterpolatorCount = 32;
    public const int TargetOutputCount = 8;

    public uint VertexOutputConfiguration;
    public uint PositionExportFormat;
    public uint VertexOutputControl;
    public uint[] PixelInterpolatorSettings = new uint[InterpolatorCount];
    // Bit n is set once interpolator n was written; an unwritten slot maps attribute n to parameter n.
    public uint PixelInterpolatorWritten;
    public uint IndexExportFormat;
    public uint PrimitiveShaderSubgroupControl;
    public uint GeometryInstanceCount;
    public uint GeometryOnChipControl;
    public uint MaxTessellationLevel;
    public uint MinTessellationLevel;
    public uint MaxOutputPerSubgroup;
    public uint ExportRingItemSize;
    public uint GeometryMaxVerticesOut;
    public uint GeometryOutputPrimitiveType;
    public uint PrimitiveIdEnable;
    public uint VertexReuseOff;
    public uint TessellationDistribution;
    public uint LocalHullConfiguration;
    public uint TessellationFactorParameter;
    public uint DepthExportFormat;
    public byte[] TargetOutputModes = new byte[TargetOutputCount];
    public uint PixelInputEnable;
    public uint PixelInputAddress;
    public uint PixelInputControl;
    public uint BarycentricControl;
    public uint ColorShaderMask;
    public uint ScanShaderControl;
    public DepthShaderControlRegisters DepthShaderControl;

    public uint ExportCount => 1u + ((VertexOutputConfiguration >> 1) & 0x1Fu);

    public uint ExportVerticesPerSubgroup => GeometryOnChipControl & 0x7FFu;

    public uint GeometryPrimitivesPerSubgroup => (GeometryOnChipControl >> 11) & 0x7FFu;

    public uint GeometryInstancedPrimitivesInSubgroup => (GeometryOnChipControl >> 22) & 0x3FFu;

    public ShaderInterfaceRegisters Copy()
    {
        var copy = (ShaderInterfaceRegisters)MemberwiseClone();
        copy.PixelInterpolatorSettings = (uint[])PixelInterpolatorSettings.Clone();
        copy.TargetOutputModes = (byte[])TargetOutputModes.Clone();
        return copy;
    }
}
