// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

public struct BlendRegisters
{
    public byte ColorSourceFactor = 1;
    public byte ColorFunction;
    public byte ColorDestinationFactor;
    public byte AlphaSourceFactor = 1;
    public byte AlphaFunction;
    public byte AlphaDestinationFactor;
    public bool SeparateAlpha = true;
    public bool Enable;

    public BlendRegisters()
    {
    }

    public static BlendRegisters Decode(uint value) => new()
    {
        ColorSourceFactor = (byte)RegisterField.Get(value, 0, 0x1F),
        ColorFunction = (byte)RegisterField.Get(value, 5, 0x7),
        ColorDestinationFactor = (byte)RegisterField.Get(value, 8, 0x1F),
        AlphaSourceFactor = (byte)RegisterField.Get(value, 16, 0x1F),
        AlphaFunction = (byte)RegisterField.Get(value, 21, 0x7),
        AlphaDestinationFactor = (byte)RegisterField.Get(value, 24, 0x1F),
        SeparateAlpha = RegisterField.Bit(value, 29),
        Enable = RegisterField.Bit(value, 30),
    };
}

public struct BlendColorRegisters
{
    public float Red;
    public float Green;
    public float Blue;
    public float Alpha;
}

public struct ClipControlRegisters
{
    public byte UserClipPlanes;
    public byte UserClipPlaneMode;
    public bool DirectXClipSpace;
    public bool VertexKillAny;
    public bool NearZClipDisable;
    public bool FarZClipDisable;
    public bool UserClipPlaneNegateY;
    public bool ClipDisable;
    public bool UserClipPlaneCullOnly;
    public bool CullOnClippingErrorDisable;
    public bool LinearAttributeClipEnable;
    public bool ForceViewportIndexFromVertexShader;

    public readonly bool IsZClipEnabled => !NearZClipDisable && !FarZClipDisable;

    public static ClipControlRegisters Decode(uint value) => new()
    {
        UserClipPlanes = (byte)RegisterField.Get(value, 0, 0x3F),
        UserClipPlaneMode = (byte)RegisterField.Get(value, 14, 0x3),
        DirectXClipSpace = RegisterField.Bit(value, 19),
        VertexKillAny = RegisterField.Bit(value, 21),
        NearZClipDisable = RegisterField.Bit(value, 26),
        FarZClipDisable = RegisterField.Bit(value, 27),
        UserClipPlaneNegateY = RegisterField.Bit(value, 13),
        ClipDisable = RegisterField.Bit(value, 16),
        UserClipPlaneCullOnly = RegisterField.Bit(value, 17),
        CullOnClippingErrorDisable = RegisterField.Bit(value, 20),
        LinearAttributeClipEnable = RegisterField.Bit(value, 24),
        ForceViewportIndexFromVertexShader = RegisterField.Bit(value, 25),
    };
}

public struct StencilControlRegisters
{
    public byte Fail;
    public byte Pass;
    public byte DepthFail;
    public byte FailBack;
    public byte PassBack;
    public byte DepthFailBack;

    public static StencilControlRegisters Decode(uint value) => new()
    {
        Fail = (byte)RegisterField.Get(value, 0, 0xF),
        Pass = (byte)RegisterField.Get(value, 4, 0xF),
        DepthFail = (byte)RegisterField.Get(value, 8, 0xF),
        FailBack = (byte)RegisterField.Get(value, 12, 0xF),
        PassBack = (byte)RegisterField.Get(value, 16, 0xF),
        DepthFailBack = (byte)RegisterField.Get(value, 20, 0xF),
    };
}

public struct StencilMaskRegisters
{
    public byte TestValue;
    public byte Mask;
    public byte WriteMask;
    public byte OperationValue;
    public byte TestValueBack;
    public byte MaskBack;
    public byte WriteMaskBack;
    public byte OperationValueBack;
}

public struct RasterModeRegisters
{
    public bool CullFront;
    public bool CullBack;
    public bool FrontFaceClockwise;
    public byte PolygonMode;
    public byte FrontPolygonType;
    public byte BackPolygonType;
    public bool PolygonOffsetFrontEnable;
    public bool PolygonOffsetBackEnable;
    public bool VertexWindowOffsetEnable;
    public bool ProvokingVertexLast;
    public bool PerspectiveCorrectionDisable;

    public static RasterModeRegisters Decode(uint value) => new()
    {
        CullFront = RegisterField.Bit(value, 0),
        CullBack = RegisterField.Bit(value, 1),
        FrontFaceClockwise = RegisterField.Bit(value, 2),
        PolygonMode = (byte)RegisterField.Get(value, 3, 0x3),
        FrontPolygonType = (byte)RegisterField.Get(value, 5, 0x7),
        BackPolygonType = (byte)RegisterField.Get(value, 8, 0x7),
        PolygonOffsetFrontEnable = RegisterField.Bit(value, 11),
        PolygonOffsetBackEnable = RegisterField.Bit(value, 12),
        VertexWindowOffsetEnable = RegisterField.Bit(value, 16),
        ProvokingVertexLast = RegisterField.Bit(value, 19),
        PerspectiveCorrectionDisable = RegisterField.Bit(value, 20),
    };
}

public struct PolygonOffsetRegisters
{
    public sbyte NegativeDepthBits = -23;
    public bool DepthIsFloat = true;
    public float Clamp;
    public float FrontScale;
    public float FrontOffset;
    public float BackScale;
    public float BackOffset;

    public PolygonOffsetRegisters()
    {
    }
}

public struct EnhancedQualityAntialiasingRegisters
{
    public byte MaxAnchorSamples;
    public byte PixelShaderIterationSamples;
    public byte MaskExportSamples;
    public byte AlphaToMaskSamples;
    public bool HighQualityIntersections;
    public bool IncoherentReads;
    public bool InterpolateComponentZ;
    public bool StaticAnchorAssociations;

    public static EnhancedQualityAntialiasingRegisters Decode(uint value) => new()
    {
        MaxAnchorSamples = (byte)RegisterField.Get(value, 0, 0x7),
        PixelShaderIterationSamples = (byte)RegisterField.Get(value, 4, 0x7),
        MaskExportSamples = (byte)RegisterField.Get(value, 8, 0x7),
        AlphaToMaskSamples = (byte)RegisterField.Get(value, 12, 0x7),
        HighQualityIntersections = RegisterField.Bit(value, 16),
        IncoherentReads = RegisterField.Bit(value, 17),
        InterpolateComponentZ = RegisterField.Bit(value, 18),
        StaticAnchorAssociations = RegisterField.Bit(value, 20),
    };
}

public struct ColorControlRegisters
{
    public byte Mode = 1;
    public byte LogicOperation = 0xCC;

    public ColorControlRegisters()
    {
    }

    public static ColorControlRegisters Decode(uint value) => new()
    {
        Mode = (byte)RegisterField.Get(value, 4, 0x7),
        LogicOperation = (byte)RegisterField.Get(value, 16, 0xFF),
    };
}

public struct ScanModeRegisters
{
    public bool MultisampleEnable;
    public bool ViewportScissorEnable = true;
    public bool LineStippleEnable;

    public ScanModeRegisters()
    {
    }

    public static ScanModeRegisters Decode(uint value) => new()
    {
        MultisampleEnable = RegisterField.Bit(value, 0),
        ViewportScissorEnable = RegisterField.Bit(value, 1),
        LineStippleEnable = RegisterField.Bit(value, 2),
    };
}

public struct AntialiasingConfigRegisters
{
    public byte SampleCountLog2;
    public bool MaskCentroidDetermination;
    public byte MaxSampleDistance;
    public byte ExposedSamplesLog2;

    public static AntialiasingConfigRegisters Decode(uint value) => new()
    {
        SampleCountLog2 = (byte)RegisterField.Get(value, 0, 0x7),
        MaskCentroidDetermination = RegisterField.Bit(value, 4),
        MaxSampleDistance = (byte)RegisterField.Get(value, 13, 0xF),
        ExposedSamplesLog2 = (byte)RegisterField.Get(value, 20, 0x7),
    };
}

public struct DepthShaderControlRegisters
{
    public uint RemainingBits;
    public byte ConservativeDepthExport;
    public byte DepthExportOrder;
    public bool KillEnable;
    public bool DepthExportEnable;
    public bool MaskExportEnable;
    public bool DualExportEnable;
    public bool ExecuteOnNoop;
    public bool AlphaToMaskDisable;

    public static DepthShaderControlRegisters Decode(uint value) => new()
    {
        RemainingBits = value & 0xFFFF_908Eu,
        ConservativeDepthExport = (byte)RegisterField.Get(value, 13, 0x3),
        DepthExportOrder = (byte)RegisterField.Get(value, 4, 0x3),
        KillEnable = RegisterField.Bit(value, 6),
        DepthExportEnable = RegisterField.Bit(value, 0),
        MaskExportEnable = RegisterField.Bit(value, 8),
        DualExportEnable = RegisterField.Bit(value, 9),
        ExecuteOnNoop = RegisterField.Bit(value, 10),
        AlphaToMaskDisable = RegisterField.Bit(value, 11),
    };
}

public struct ViewportRegisters
{
    public float MinDepth;
    public float MaxDepth;
    public float XScale;
    public float XOffset;
    public float YScale;
    public float YOffset;
    public float ZScale;
    public float ZOffset;
    public int ScissorLeft;
    public int ScissorTop;
    public int ScissorRight;
    public int ScissorBottom;
    public bool ScissorWindowOffsetEnable;
}

public sealed class SampleLocationRegisters
{
    public const int LocationCount = 16;

    public ulong CentroidPriority;
    public uint[] Locations = new uint[LocationCount];

    public SampleLocationRegisters Copy()
    {
        var copy = (SampleLocationRegisters)MemberwiseClone();
        copy.Locations = (uint[])Locations.Clone();
        return copy;
    }
}

public sealed class ScreenViewportRegisters
{
    public const int ViewportCount = 16;
    public const int ClipRectangleCount = 4;

    public ViewportRegisters[] Viewports = new ViewportRegisters[ViewportCount];
    public uint TransformControl = 1087;
    public int ScreenScissorLeft;
    public int ScreenScissorTop;
    public int ScreenScissorRight;
    public int ScreenScissorBottom;
    public int WindowScissorLeft;
    public int WindowScissorTop;
    public int WindowScissorRight;
    public int WindowScissorBottom;
    public bool WindowScissorWindowOffsetEnable;
    public int GenericScissorLeft;
    public int GenericScissorTop;
    public int GenericScissorRight;
    public int GenericScissorBottom;
    public bool GenericScissorWindowOffsetEnable;
    public int WindowOffsetX;
    public int WindowOffsetY;
    public uint HardwareOffsetX;
    public uint HardwareOffsetY;
    public float GuardBandHorizontalClip;
    public float GuardBandVerticalClip;
    public float GuardBandHorizontalDiscard;
    public float GuardBandVerticalDiscard;
    public ushort ClipRectangleRule = 0xFFFF;
    public int[] ClipRectangleLeft = new int[ClipRectangleCount];
    public int[] ClipRectangleTop = new int[ClipRectangleCount];
    public int[] ClipRectangleRight = new int[ClipRectangleCount];
    public int[] ClipRectangleBottom = new int[ClipRectangleCount];
    public bool[] ClipRectangleWindowOffsetEnable = new bool[ClipRectangleCount];

    public ScreenViewportRegisters Copy()
    {
        var copy = (ScreenViewportRegisters)MemberwiseClone();
        copy.Viewports = (ViewportRegisters[])Viewports.Clone();
        copy.ClipRectangleLeft = (int[])ClipRectangleLeft.Clone();
        copy.ClipRectangleTop = (int[])ClipRectangleTop.Clone();
        copy.ClipRectangleRight = (int[])ClipRectangleRight.Clone();
        copy.ClipRectangleBottom = (int[])ClipRectangleBottom.Clone();
        copy.ClipRectangleWindowOffsetEnable = (bool[])ClipRectangleWindowOffsetEnable.Clone();
        return copy;
    }
}

// The context bank as the register writers decode it; the draw path reads it without lookups.
public sealed class ContextRegisters
{
    public const int ColorTargetCount = 8;

    public float LineWidth = 1f;
    public uint PrimitiveResetIndex = 0xFFFF_FFFFu;
    public BlendRegisters[] BlendControls = NewBlendControls();
    public BlendColorRegisters BlendColor;
    public ColorTargetWords[] ColorTargets = new ColorTargetWords[ColorTargetCount];
    public uint[] ColorClearWord1 = new uint[ColorTargetCount];
    public uint RenderTargetMask;
    public ScreenViewportRegisters ScreenViewport = new();
    public ClipControlRegisters Clip;
    public ColorControlRegisters ColorControl = new();
    public ScanModeRegisters ScanMode = new();
    public SampleLocationRegisters SampleLocations = new();
    public AntialiasingConfigRegisters AntialiasingConfig;
    public uint ShaderStages;
    public DepthTargetWords DepthTarget;
    // The reserved depth register is stored because the composite depth binding writes it.
    public uint ReservedDepthRegister;
    // The two reserved registers behind the depth block, written by the composite depth binding.
    public uint ReservedDepthRegister1;
    public uint ReservedDepthRegister3;
    public float DepthClearValue;
    public float DepthBoundsMin;
    public float DepthBoundsMax = 1f;
    public byte StencilClearValue;
    public StencilControlRegisters StencilControl;
    public StencilMaskRegisters StencilMask;
    public RasterModeRegisters RasterMode;
    public PolygonOffsetRegisters PolygonOffset = new();
    public EnhancedQualityAntialiasingRegisters EnhancedQualityAntialiasing;
    public ShaderInterfaceRegisters ShaderInterface = new();

    public uint RenderTargetMaskForSlot(uint slot) => (RenderTargetMask >> (int)(slot * 4)) & 0xFu;

    public ContextRegisters Copy()
    {
        var copy = (ContextRegisters)MemberwiseClone();
        copy.BlendControls = (BlendRegisters[])BlendControls.Clone();
        copy.ColorTargets = (ColorTargetWords[])ColorTargets.Clone();
        copy.ColorClearWord1 = (uint[])ColorClearWord1.Clone();
        copy.ScreenViewport = ScreenViewport.Copy();
        copy.SampleLocations = SampleLocations.Copy();
        copy.ShaderInterface = ShaderInterface.Copy();
        return copy;
    }

    private static BlendRegisters[] NewBlendControls()
    {
        var controls = new BlendRegisters[ColorTargetCount];
        Array.Fill(controls, new BlendRegisters());
        return controls;
    }
}
