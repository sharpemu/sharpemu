// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Rendering;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The fixed pipeline state packed into 166 bytes; two draws with equal bytes share a pipeline.
public sealed class PipelineStaticParameters : IEquatable<PipelineStaticParameters>
{
    public const int ByteSize = 166;
    public const int ColorAttachmentCount = 8;

    private const int NegativeOneToOneOffset = 0;
    private const int DepthClipEnableOffset = 1;
    private const int TopologyOffset = 2;
    private const int PrimitiveRestartOffset = 6;
    private const int SamplesOffset = 7;
    private const int SampleShadingOffset = 11;
    private const int WithDepthOffset = 12;
    private const int DepthBoundsTestOffset = 13;
    private const int DepthMinBoundsOffset = 14;
    private const int DepthMaxBoundsOffset = 18;
    private const int StencilTestOffset = 22;
    private const int StencilFrontOffset = 23;
    private const int StencilBackOffset = 39;
    private const int ColorCountOffset = 55;
    private const int ColorMaskOffset = 59;
    private const int CullFrontOffset = 91;
    private const int CullBackOffset = 92;
    private const int FaceOffset = 93;
    private const int ColorSourceBlendOffset = 94;
    private const int ColorBlendFunctionOffset = 102;
    private const int ColorDestinationBlendOffset = 110;
    private const int AlphaSourceBlendOffset = 118;
    private const int AlphaBlendFunctionOffset = 126;
    private const int AlphaDestinationBlendOffset = 134;
    private const int SeparateAlphaBlendOffset = 142;
    private const int BlendEnableOffset = 150;
    private const int BlendBypassOffset = 158;

    private readonly byte[] _bytes = new byte[ByteSize];

    public PipelineStaticParameters()
    {
        DepthClipEnable = true;
        Samples = 1;
        ColorCount = 1;
        StencilFront = StencilOperations.Default;
        StencilBack = StencilOperations.Default;
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static PipelineStaticParameters FromBytes(ReadOnlySpan<byte> bytes)
    {
        var parameters = new PipelineStaticParameters();
        bytes[..ByteSize].CopyTo(parameters._bytes);
        return parameters;
    }

    private bool GetBool(int offset) => _bytes[offset] != 0;

    private void SetBool(int offset, bool value) => _bytes[offset] = value ? (byte)1 : (byte)0;

    private uint GetUInt(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset));

    private void SetUInt(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(offset), value);

    private float GetFloat(int offset) => BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(offset));

    private void SetFloat(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(offset), value);

    private StencilOperations GetStencil(int offset) => new(
        (StencilOp)GetUInt(offset),
        (StencilOp)GetUInt(offset + 4),
        (StencilOp)GetUInt(offset + 8),
        (CompareOp)GetUInt(offset + 12));

    private void SetStencil(int offset, in StencilOperations operations)
    {
        SetUInt(offset, (uint)operations.FailOperation);
        SetUInt(offset + 4, (uint)operations.PassOperation);
        SetUInt(offset + 8, (uint)operations.DepthFailOperation);
        SetUInt(offset + 12, (uint)operations.Compare);
    }

    public bool NegativeOneToOne { get => GetBool(NegativeOneToOneOffset); set => SetBool(NegativeOneToOneOffset, value); }
    public bool DepthClipEnable { get => GetBool(DepthClipEnableOffset); set => SetBool(DepthClipEnableOffset, value); }
    public PrimitiveTopology Topology { get => (PrimitiveTopology)GetUInt(TopologyOffset); set => SetUInt(TopologyOffset, (uint)value); }
    public bool PrimitiveRestartEnable { get => GetBool(PrimitiveRestartOffset); set => SetBool(PrimitiveRestartOffset, value); }
    public uint Samples { get => GetUInt(SamplesOffset); set => SetUInt(SamplesOffset, value); }
    public bool SampleShadingEnable { get => GetBool(SampleShadingOffset); set => SetBool(SampleShadingOffset, value); }
    public bool WithDepth { get => GetBool(WithDepthOffset); set => SetBool(WithDepthOffset, value); }
    public bool DepthBoundsTestEnable { get => GetBool(DepthBoundsTestOffset); set => SetBool(DepthBoundsTestOffset, value); }
    public float DepthMinBounds { get => GetFloat(DepthMinBoundsOffset); set => SetFloat(DepthMinBoundsOffset, value); }
    public float DepthMaxBounds { get => GetFloat(DepthMaxBoundsOffset); set => SetFloat(DepthMaxBoundsOffset, value); }
    public bool StencilTestEnable { get => GetBool(StencilTestOffset); set => SetBool(StencilTestOffset, value); }
    public StencilOperations StencilFront { get => GetStencil(StencilFrontOffset); set => SetStencil(StencilFrontOffset, in value); }
    public StencilOperations StencilBack { get => GetStencil(StencilBackOffset); set => SetStencil(StencilBackOffset, in value); }
    public uint ColorCount { get => GetUInt(ColorCountOffset); set => SetUInt(ColorCountOffset, value); }
    public bool CullFront { get => GetBool(CullFrontOffset); set => SetBool(CullFrontOffset, value); }
    public bool CullBack { get => GetBool(CullBackOffset); set => SetBool(CullBackOffset, value); }
    public bool FrontFaceClockwise { get => GetBool(FaceOffset); set => SetBool(FaceOffset, value); }

    public uint GetColorMask(int index) => GetUInt(ColorMaskOffset + index * 4);
    public void SetColorMask(int index, uint value) => SetUInt(ColorMaskOffset + index * 4, value);
    public byte GetColorSourceBlend(int index) => _bytes[ColorSourceBlendOffset + index];
    public void SetColorSourceBlend(int index, byte value) => _bytes[ColorSourceBlendOffset + index] = value;
    public byte GetColorBlendFunction(int index) => _bytes[ColorBlendFunctionOffset + index];
    public void SetColorBlendFunction(int index, byte value) => _bytes[ColorBlendFunctionOffset + index] = value;
    public byte GetColorDestinationBlend(int index) => _bytes[ColorDestinationBlendOffset + index];
    public void SetColorDestinationBlend(int index, byte value) => _bytes[ColorDestinationBlendOffset + index] = value;
    public byte GetAlphaSourceBlend(int index) => _bytes[AlphaSourceBlendOffset + index];
    public void SetAlphaSourceBlend(int index, byte value) => _bytes[AlphaSourceBlendOffset + index] = value;
    public byte GetAlphaBlendFunction(int index) => _bytes[AlphaBlendFunctionOffset + index];
    public void SetAlphaBlendFunction(int index, byte value) => _bytes[AlphaBlendFunctionOffset + index] = value;
    public byte GetAlphaDestinationBlend(int index) => _bytes[AlphaDestinationBlendOffset + index];
    public void SetAlphaDestinationBlend(int index, byte value) => _bytes[AlphaDestinationBlendOffset + index] = value;
    public bool GetSeparateAlphaBlend(int index) => GetBool(SeparateAlphaBlendOffset + index);
    public void SetSeparateAlphaBlend(int index, bool value) => SetBool(SeparateAlphaBlendOffset + index, value);
    public bool GetBlendEnable(int index) => GetBool(BlendEnableOffset + index);
    public void SetBlendEnable(int index, bool value) => SetBool(BlendEnableOffset + index, value);
    public bool GetBlendBypass(int index) => GetBool(BlendBypassOffset + index);
    public void SetBlendBypass(int index, bool value) => SetBool(BlendBypassOffset + index, value);

    public bool Equals(PipelineStaticParameters? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as PipelineStaticParameters);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }
}

// The attachment formats a graphics pipeline renders into.
public sealed class PipelineRenderingState : IEquatable<PipelineRenderingState>
{
    public Format[] ColorFormats { get; } = new Format[PipelineStaticParameters.ColorAttachmentCount];
    public Format DepthFormat { get; set; } = Format.Undefined;
    public Format StencilFormat { get; set; } = Format.Undefined;
    public uint ColorCount { get; set; }

    public bool Equals(PipelineRenderingState? other) =>
        other is not null && ColorCount == other.ColorCount && DepthFormat == other.DepthFormat && StencilFormat == other.StencilFormat &&
        ColorFormats.AsSpan().SequenceEqual(other.ColorFormats);

    public override bool Equals(object? obj) => Equals(obj as PipelineRenderingState);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ColorCount);
        for (var index = 0; index < ColorCount; index++)
        {
            hash.Add(ColorFormats[index]);
        }

        hash.Add(DepthFormat);
        hash.Add(StencilFormat);
        return hash.ToHashCode();
    }
}

public readonly record struct PipelineVertexBinding(uint Stride, bool Instance);

public readonly record struct PipelineVertexAttribute(uint Offset, byte Binding);

// The vertex bindings and attributes a graphics pipeline fetches through.
public sealed class PipelineVertexInputState : IEquatable<PipelineVertexInputState>
{
    public PipelineVertexBinding[] Bindings { get; } = new PipelineVertexBinding[VertexInputInfo.MaxBuffers];
    public PipelineVertexAttribute[] Attributes { get; } = new PipelineVertexAttribute[VertexInputInfo.MaxBuffers];
    public byte BindingCount { get; set; }
    public byte AttributeCount { get; set; }

    public bool Equals(PipelineVertexInputState? other) =>
        other is not null && BindingCount == other.BindingCount && AttributeCount == other.AttributeCount &&
        Bindings.AsSpan().SequenceEqual(other.Bindings) && Attributes.AsSpan().SequenceEqual(other.Attributes);

    public override bool Equals(object? obj) => Equals(obj as PipelineVertexInputState);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(BindingCount);
        for (var index = 0; index < BindingCount; index++)
        {
            hash.Add(Bindings[index]);
        }

        hash.Add(AttributeCount);
        for (var index = 0; index < AttributeCount; index++)
        {
            hash.Add(Attributes[index]);
        }

        return hash.ToHashCode();
    }
}

public sealed class GraphicsPipelineKey : IEquatable<GraphicsPipelineKey>
{
    public required PipelineRenderingState Rendering { get; init; }
    public ulong VertexProgramId { get; init; }
    public ulong PixelProgramId { get; init; }
    public required PipelineVertexInputState VertexInput { get; init; }
    public required PipelineStaticParameters StaticParameters { get; init; }

    public bool Equals(GraphicsPipelineKey? other) =>
        other is not null && Rendering.Equals(other.Rendering) && VertexProgramId == other.VertexProgramId && PixelProgramId == other.PixelProgramId &&
        VertexInput.Equals(other.VertexInput) && StaticParameters.Equals(other.StaticParameters);

    public override bool Equals(object? obj) => Equals(obj as GraphicsPipelineKey);

    public override int GetHashCode() => HashCode.Combine(Rendering, VertexProgramId, PixelProgramId, VertexInput, StaticParameters);
}

public readonly record struct ComputePipelineKey(ulong ComputeProgramId);
