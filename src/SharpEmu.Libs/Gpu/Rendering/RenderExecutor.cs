// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Gpu.Rendering;

public enum GuestPrimitiveType : uint
{
    None = 0,
    PointList = 1,
    LineList = 2,
    LineStrip = 3,
    TriangleList = 4,
    TriangleFan = 5,
    TriangleStrip = 6,
    RectangleList = 7,
    RectangleListLegacy = 17,
    QuadListLegacy = 19,
}

public enum GuestIndexType : uint
{
    Index16 = 0,
    Index32 = 1,
    Index8 = 2,
}

// Resolves draw and dispatch state from the register banks and records it through the host.
public sealed partial class RenderExecutor
{
    private const uint PrimitiveShaderStageMask = 0x02002000;
    private const uint MaxOutputPerSubgroupLimit = 0x40;
    private static int _geometryWarningShown;

    private readonly IRenderHost _host;
    private readonly IShaderPipelineProvider _pipelines;
    private readonly bool _strictDrawResources;
    private readonly HashSet<(ulong ShaderHash, ImageType ImageType, ImageViewType ViewType)> _reportedDrawImageTypeMismatches = [];

    public RenderExecutor(IRenderHost host, IShaderPipelineProvider pipelines)
        : this(host, pipelines, Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE") == "1")
    {
    }

    internal RenderExecutor(IRenderHost host, IShaderPipelineProvider pipelines, bool strictDrawResources)
    {
        _host = host;
        _pipelines = pipelines;
        _strictDrawResources = strictDrawResources;
    }

    private readonly record struct DrawCall(string Name, RecordedOperation Operation, uint Count, uint InstanceCount, uint FirstInstance);

    private readonly record struct DrawEmission(bool Indexed, int VertexOffset, uint FirstVertex, uint FirstInstance);

    private readonly record struct IndexSource(bool Enabled, ulong Address, byte[]? HostData, ulong Size, IndexType Type);

    [System.Runtime.CompilerServices.InlineArray(RenderingState.ColorAttachmentCapacity)]
    private struct ColorTargetStates
    {
        private ColorTargetState _element0;
    }

    private struct DrawState
    {
        public ColorTargetStates Colors;
        public uint ColorCount;
        public DepthAttachmentState Depth;
        public bool PixelActive;
        public RenderingState Rendering;
        public GraphicsPrograms Programs;

        public static DrawState Create() => new() { PixelActive = true, Programs = null! };
    }

    private static ReadOnlySpan<ColorTargetState> BoundColors(ref DrawState state) =>
        ((ReadOnlySpan<ColorTargetState>)state.Colors)[..(int)state.ColorCount];

    public void DrawIndexed(ulong submitId, RegisterBanks banks, in DrawIndexedArguments arguments)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawExecutor);
        if (!_host.IsRecording)
        {
            throw _host.Fatal("An indexed draw has no recording command buffer.");
        }

        if (arguments.OffsetSource == DrawOffsetSource.Packet && arguments.FirstInstance != 0)
        {
            throw _host.Fatal($"A packet indexed draw carries a first instance: firstInstance={arguments.FirstInstance}.");
        }

        _host.RunPendingOperations();
        var userConfig = banks.UserConfig;
        var shader = banks.Shader;
        _host.SetDebugInformation(RecordedOperation.DrawIndex, submitId, arguments.IndexCount, 0, 1, arguments.InstanceCount, arguments.IndexAddress);
        if (arguments.IndexCount == 0 || arguments.InstanceCount == 0)
        {
            return;
        }

        if (ConsumesColorMetadataOperation(banks.Context))
        {
            _host.ResetBindings();
            return;
        }

        if (!HasValidVertexShader(shader) || IsUnsupportedGeometryStage(banks))
        {
            return;
        }

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write(
                $"DrawIndexed submit={submitId} indexTypeAndSize=0x{arguments.IndexTypeAndSize:X8} count=0x{arguments.IndexCount:X8} " +
                $"indexAddress=0x{arguments.IndexAddress:X16} instances=0x{arguments.InstanceCount:X8} baseVertex=0x{(uint)arguments.BaseVertex:X8} firstInstance=0x{arguments.FirstInstance:X8}");
        }

        ValidateDrawRegisters(banks);
        if (!ResolveTopology(userConfig, autoDraw: false, out var topology))
        {
            return;
        }

        var primitiveRestart = ResolvePrimitiveRestart(banks, topology, arguments.IndexTypeAndSize);
        IndexType indexType;
        ulong indexSize;
        var expandIndex8 = false;
        switch ((GuestIndexType)arguments.IndexTypeAndSize)
        {
            case GuestIndexType.Index16:
                indexType = IndexType.Uint16;
                indexSize = 2ul * arguments.IndexCount;
                break;
            case GuestIndexType.Index32:
                indexType = IndexType.Uint32;
                indexSize = 4ul * arguments.IndexCount;
                break;
            case GuestIndexType.Index8:
                indexType = IndexType.Uint16;
                indexSize = arguments.IndexCount;
                expandIndex8 = true;
                break;
            default:
                throw _host.Fatal($"The index type and size is unknown: indexTypeAndSize={arguments.IndexTypeAndSize}.");
        }

        var draw = new DrawCall("DrawIndexed", RecordedOperation.DrawIndex, arguments.IndexCount, arguments.InstanceCount, arguments.FirstInstance);
        byte[]? expandedIndices = null;
        if (expandIndex8)
        {
            expandedIndices = ExpandIndex8(arguments.IndexAddress, arguments.IndexCount, primitiveRestart, banks.Context.PrimitiveResetIndex);
        }
        else if (primitiveRestart)
        {
            var indexMask = indexType == IndexType.Uint16 ? 0xFFFFu : uint.MaxValue;
            var restartIndex = banks.Context.PrimitiveResetIndex & indexMask;
            if (restartIndex != indexMask)
            {
                expandedIndices = ConvertRestartIndices(arguments.IndexAddress, arguments.IndexCount, indexType, restartIndex);
                indexType = IndexType.Uint32;
            }
        }

        var indexSource = new IndexSource(
            true,
            arguments.IndexAddress,
            expandedIndices,
            expandedIndices is null ? indexSize : (ulong)expandedIndices.Length,
            indexType);
        var state = DrawState.Create();
        if (!TryResolveDrawTargets(banks, in draw, ref state))
        {
            _host.ResetBindings();
            return;
        }

        ResolveShaderPrograms(banks, ref state);
        if (!ApplyProgramAdaptations(banks, in draw, ref state, new TargetlessDrawArguments(submitId, true, arguments, default)))
        {
            _host.ResetBindings();
            return;
        }

        TraceDrawState(submitId, banks, in draw, in state);
        var indirect = arguments.OffsetSource == DrawOffsetSource.IndirectArguments;
        var vertexOffset = indirect
            ? arguments.BaseVertex
            : ResolveVertexOffset(userConfig.IndexOffset, state.Programs.VertexInput) + arguments.BaseVertex;
        var emission = new DrawEmission(
            true,
            vertexOffset,
            0,
            indirect ? arguments.FirstInstance : ResolveInstanceOffset(state.Programs.VertexInput));
        RecordDraw(submitId, banks, in draw, ref state, topology, in emission, in indexSource, primitiveRestart, setBindDebug: true, setAutoDebug: false);
        _host.ResetBindings();
    }

    public void DrawAuto(ulong submitId, RegisterBanks banks, in DrawAutoArguments arguments)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawExecutor);
        if (!_host.IsRecording)
        {
            throw _host.Fatal("An automatic draw has no recording command buffer.");
        }

        if (arguments.OffsetSource == DrawOffsetSource.Packet && arguments.FirstInstance != 0)
        {
            throw _host.Fatal($"A packet automatic draw carries a first instance: firstInstance={arguments.FirstInstance}.");
        }

        _host.RunPendingOperations();
        var userConfig = banks.UserConfig;
        var shader = banks.Shader;
        _host.SetDebugInformation(RecordedOperation.DrawIndexAuto, submitId, arguments.VertexCount, 0, arguments.FirstVertex, arguments.InstanceCount, arguments.FirstInstance);
        if (arguments.VertexCount == 0 || arguments.InstanceCount == 0)
        {
            return;
        }

        if (ConsumesColorMetadataOperation(banks.Context))
        {
            _host.ResetBindings();
            return;
        }

        if (!HasValidVertexShader(shader) || IsUnsupportedGeometryStage(banks))
        {
            return;
        }

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write(
                $"DrawAuto submit={submitId} count=0x{arguments.VertexCount:X8} instances=0x{arguments.InstanceCount:X8} " +
                $"firstVertex=0x{arguments.FirstVertex:X8} firstInstance=0x{arguments.FirstInstance:X8}");
        }

        ValidateDrawRegisters(banks);
        var draw = new DrawCall("DrawAuto", RecordedOperation.DrawIndexAuto, arguments.VertexCount, arguments.InstanceCount, arguments.FirstInstance);
        var state = DrawState.Create();
        if (!TryResolveDrawTargets(banks, in draw, ref state))
        {
            _host.ResetBindings();
            return;
        }

        if (!ResolveTopology(userConfig, autoDraw: true, out var topology))
        {
            _host.ResetBindings();
            return;
        }

        ResolveShaderPrograms(banks, ref state);
        var vertexInput = state.Programs.VertexInput;
        var pixelInput = state.Programs.PixelInput;
        var rectangleList = topology == PrimitiveTopology.PatchList;
        if (rectangleList && vertexInput.Buffers.Length == 0 && vertexInput.Stage.Program?.ParameterExportMask == 0 && pixelInput.InputCount != 0)
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write(
                    $"DrawAuto skipped a rectangle list with no vertex parameter exports and pixel inputs: pixelInputs={pixelInput.InputCount} " +
                    $"pixel=0x{shader.Pixel.Address:X16} export=0x{shader.Vertex.ExportAddress:X16} geometry=0x{shader.Vertex.GeometryAddress:X16}");
            }

            _host.ResetBindings();
            return;
        }

        if (!ApplyProgramAdaptations(banks, in draw, ref state, new TargetlessDrawArguments(submitId, false, default, arguments)))
        {
            _host.ResetBindings();
            return;
        }

        TraceDrawState(submitId, banks, in draw, in state);
        var indirect = arguments.OffsetSource == DrawOffsetSource.IndirectArguments;
        var vertexOffset = indirect
            ? (int)arguments.FirstVertex
            : ResolveVertexOffset(userConfig.IndexOffset, vertexInput) + (int)arguments.FirstVertex;
        var emission = new DrawEmission(
            false,
            0,
            (uint)vertexOffset,
            indirect ? arguments.FirstInstance : ResolveInstanceOffset(vertexInput));
        RecordDraw(submitId, banks, in draw, ref state, topology, in emission, default, primitiveRestart: false, setBindDebug: false, setAutoDebug: true);
        _host.ResetBindings();
    }

    private byte[] ConvertRestartIndices(ulong indexAddress, uint indexCount, IndexType indexType, uint restartIndex)
    {
        var sourceStride = indexType == IndexType.Uint16 ? sizeof(ushort) : sizeof(uint);
        var source = new byte[checked((int)indexCount * sourceStride)];
        if (indexAddress == 0 || !_host.TryReadGuest(indexAddress, source))
        {
            throw _host.Fatal($"The restart index data is unreadable: address=0x{indexAddress:X16} count={indexCount}.");
        }

        // Widen 16-bit indices so their maximum value remains an ordinary vertex.
        var converted = new byte[checked((int)indexCount * sizeof(uint))];
        for (var index = 0; index < (int)indexCount; index++)
        {
            var value = sourceStride == sizeof(ushort)
                ? BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(index * sourceStride))
                : BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(index * sourceStride));
            if (value == uint.MaxValue)
            {
                throw _host.Fatal("A custom restart index conflicts with a 32-bit vertex index of 0xFFFFFFFF.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(converted.AsSpan(index * sizeof(uint)),
                value == restartIndex ? uint.MaxValue : value);
        }

        return converted;
    }

    private byte[] ExpandIndex8(ulong indexAddress, uint indexCount, bool primitiveRestart, uint restartIndex)
    {
        if (indexAddress == 0)
        {
            throw _host.Fatal($"An 8-bit index draw has no index address: count={indexCount}.");
        }

        var source = new byte[indexCount];
        if (!_host.TryReadGuest(indexAddress, source))
        {
            throw _host.Fatal($"The 8-bit index data is unreadable: address=0x{indexAddress:X16} count={indexCount}.");
        }

        var expanded = new byte[indexCount * 2];
        for (var i = 0; i < indexCount; i++)
        {
            var value = source[i];
            ushort index = primitiveRestart && value == (restartIndex & 0xFF) ? (ushort)0xFFFF : value;
            expanded[i * 2] = (byte)index;
            expanded[(i * 2) + 1] = (byte)(index >> 8);
        }

        return expanded;
    }

    // Vertex offset: the index offset register, or the embedded fetch scalar when that register is zero.
    public static int ResolveVertexOffset(uint indexOffset, VertexInputInfo vertexInput)
    {
        if (indexOffset != 0 || !vertexInput.FetchEmbedded)
        {
            return (int)indexOffset;
        }

        var program = vertexInput.Stage.Program ?? throw SubmissionScheduler.Fatal("The embedded fetch vertex input has no program.");
        return (int)ReadUserDataScalar(program, vertexInput.Stage.Resources, program.VertexOffsetScalarRegister);
    }

    public static uint ResolveInstanceOffset(VertexInputInfo vertexInput)
    {
        if (!vertexInput.FetchEmbedded)
        {
            return 0;
        }

        var program = vertexInput.Stage.Program ?? throw SubmissionScheduler.Fatal("The embedded fetch vertex input has no program.");
        return ReadUserDataScalar(program, vertexInput.Stage.Resources, program.InstanceOffsetScalarRegister);
    }

    private static uint ReadUserDataScalar(ShaderProgramInfo program, ResourceSnapshot resources, int scalarRegister)
    {
        if (scalarRegister >= (int)program.UserDataBase)
        {
            var index = (uint)scalarRegister - program.UserDataBase;
            if (index < resources.UserData.Length)
            {
                return resources.UserData[index];
            }
        }

        return 0;
    }

    private static bool HasValidVertexShader(ShaderProgramRegisters shader) => shader.Vertex.ExportAddress != 0;

    private static bool IsKnownGeometryOutputPrimitiveType(uint value) => value <= 4;

    // Only the plain vertex path and the primitive-shader vertex path with default geometry state run.
    private static bool IsUnsupportedGeometryStage(RegisterBanks banks)
    {
        var context = banks.Context;
        var shaderInterface = context.ShaderInterface;
        var vertex = banks.Shader.Vertex;
        var stages = context.ShaderStages;
        var primitiveShaderVertexPath =
            stages == PrimitiveShaderStageMask && vertex.ExportAddress != 0 &&
            shaderInterface.GeometryMaxVerticesOut == 0 && IsKnownGeometryOutputPrimitiveType(shaderInterface.GeometryOutputPrimitiveType);
        var unsupportedStageMask = stages != 0 && stages != PrimitiveShaderStageMask;
        var unsupportedGeometryStage = vertex.ExportAddress != 0 && vertex.GeometryAddress != 0 && !primitiveShaderVertexPath;
        var geometryRegisters =
            (shaderInterface.PrimitiveShaderSubgroupControl != 0 && shaderInterface.PrimitiveShaderSubgroupControl != 1) ||
            shaderInterface.GeometryMaxVerticesOut != 0 ||
            !IsKnownGeometryOutputPrimitiveType(shaderInterface.GeometryOutputPrimitiveType) ||
            shaderInterface.MaxOutputPerSubgroup > MaxOutputPerSubgroupLimit;
        if (!unsupportedStageMask && !unsupportedGeometryStage && !geometryRegisters)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _geometryWarningShown, 1) == 0)
        {
            Console.Error.WriteLine("Warning: the title uses unsupported graphics pipelines; some draw calls were skipped.");
        }

        if (RenderTrace.Enabled && RenderTrace.GeometryStageSkip())
        {
            var geometryControl = banks.UserConfig.GeometryEngineControl;
            RenderTrace.Write(
                $"Skipping an unsupported geometry stage draw: stages=0x{stages:X8} primitiveGroup=0x{geometryControl.PrimitiveGroupSize:X4} " +
                $"vertexGroup=0x{geometryControl.VertexGroupSize:X4} subgroupControl=0x{shaderInterface.PrimitiveShaderSubgroupControl:X8} " +
                $"maxOutput=0x{shaderInterface.MaxOutputPerSubgroup:X8} maxVerticesOut=0x{shaderInterface.GeometryMaxVerticesOut:X8} " +
                $"outputPrimitive=0x{shaderInterface.GeometryOutputPrimitiveType:X8} export=0x{vertex.ExportAddress:X16} geometry=0x{vertex.GeometryAddress:X16}");
        }

        return true;
    }

    private static bool PixelShaderHasDepthOrCoverageSideEffects(ShaderInterfaceRegisters shaderInterface)
    {
        var control = shaderInterface.DepthShaderControl;
        return shaderInterface.DepthExportFormat != 0 || control.KillEnable || control.DepthExportEnable ||
               control.MaskExportEnable || control.DualExportEnable || control.ExecuteOnNoop;
    }

    private static bool HasActivePixelShader(RegisterBanks banks)
    {
        var context = banks.Context;
        var shaderInterface = context.ShaderInterface;
        var hasColorOutput = (context.RenderTargetMask & shaderInterface.ColorShaderMask) != 0;
        return banks.Shader.Pixel.Address != 0 && (hasColorOutput || PixelShaderHasDepthOrCoverageSideEffects(shaderInterface));
    }

    private const byte ColorModeEliminateFastClear = 2;
    private const byte ColorModeResolve = 3;
    private const byte ColorModeFmaskDecompress = 5;
    private const byte ColorModeDccDecompress = 6;

    // These modes run color metadata operations; the shader output is not a normal draw.
    public static bool ConsumesColorMetadataOperation(ContextRegisters context)
    {
        var mode = context.ColorControl.Mode;
        return mode is ColorModeEliminateFastClear or ColorModeFmaskDecompress or ColorModeDccDecompress;
    }

    public bool ResolveTopology(UserConfigRegisters userConfig, bool autoDraw, out PrimitiveTopology topology)
    {
        topology = PrimitiveTopology.PointList;
        switch ((GuestPrimitiveType)userConfig.PrimitiveType)
        {
            case GuestPrimitiveType.None:
                return false;
            case GuestPrimitiveType.PointList:
                topology = PrimitiveTopology.PointList;
                break;
            case GuestPrimitiveType.LineList:
                topology = PrimitiveTopology.LineList;
                break;
            case GuestPrimitiveType.LineStrip:
                topology = PrimitiveTopology.LineStrip;
                break;
            case GuestPrimitiveType.TriangleList:
                topology = PrimitiveTopology.TriangleList;
                break;
            case GuestPrimitiveType.TriangleFan:
                topology = PrimitiveTopology.TriangleFan;
                break;
            case GuestPrimitiveType.TriangleStrip:
                topology = PrimitiveTopology.TriangleStrip;
                break;
            case GuestPrimitiveType.RectangleList:
                topology = PrimitiveTopology.PatchList;
                break;
            case GuestPrimitiveType.RectangleListLegacy:
                if (!autoDraw)
                {
                    throw _host.Fatal($"The primitive type is unknown for an indexed draw: primitiveType={userConfig.PrimitiveType}.");
                }

                topology = PrimitiveTopology.TriangleStrip;
                break;
            case GuestPrimitiveType.QuadListLegacy:
                topology = PrimitiveTopology.TriangleFan;
                break;
            default:
                throw _host.Fatal($"The primitive type is unknown: primitiveType={userConfig.PrimitiveType}.");
        }

        return true;
    }

    public bool ResolvePrimitiveRestart(RegisterBanks banks, PrimitiveTopology topology, uint indexTypeAndSize)
    {
        var userConfig = banks.UserConfig;
        var control = userConfig.PrimitiveResetControl;
        if ((control & ~0x3u) != 0)
        {
            throw _host.Fatal($"The primitive reset control has unsupported bits: control=0x{control:X8}.");
        }

        if ((control & 0x1) == 0)
        {
            return false;
        }

        switch ((GuestPrimitiveType)userConfig.PrimitiveType)
        {
            case GuestPrimitiveType.LineStrip:
            case GuestPrimitiveType.TriangleFan:
            case GuestPrimitiveType.TriangleStrip:
                break;
            default:
                return false;
        }

        if (topology is not (PrimitiveTopology.LineStrip or PrimitiveTopology.TriangleStrip or PrimitiveTopology.TriangleFan))
        {
            return false;
        }

        var indexMask = (GuestIndexType)indexTypeAndSize switch
        {
            GuestIndexType.Index8 => 0xFFu,
            GuestIndexType.Index16 => 0xFFFFu,
            GuestIndexType.Index32 => 0xFFFF_FFFFu,
            _ => throw _host.Fatal($"The index type and size is unknown: indexTypeAndSize={indexTypeAndSize}."),
        };
        var resetIndex = banks.Context.PrimitiveResetIndex;
        if ((control & 0x2) != 0 && (resetIndex & ~indexMask) != 0)
        {
            return false;
        }

        return true;
    }

    private void ResolveShaderPrograms(RegisterBanks banks, ref DrawState state)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawProgramResolution);
        var context = banks.Context;
        Span<ColorComponentMapArray> mappingStorage = stackalloc ColorComponentMapArray[1];
        Span<ColorComponentMap> targetExportMapping = mappingStorage[0];
        targetExportMapping.Fill(ColorComponentMap.Identity);
        foreach (ref readonly var color in BoundColors(ref state))
        {
            targetExportMapping[(int)color.Slot] = color.Resolution.ExportMapping;
        }

        state.Programs = _pipelines.GetGraphicsPrograms(
            banks.Shader.Vertex,
            banks.Shader.Pixel,
            context.ShaderInterface,
            context,
            targetExportMapping,
            state.PixelActive);
    }

    [System.Runtime.CompilerServices.InlineArray(RenderingState.ColorAttachmentCapacity)]
    private struct ColorComponentMapArray
    {
        private ColorComponentMap _element0;
    }

    private static void TraceDrawState(ulong submitId, RegisterBanks banks, in DrawCall draw, in DrawState state)
    {
        if (!RenderTrace.Enabled)
        {
            return;
        }

        var shader = banks.Shader;
        var colorAddress = state.ColorCount != 0 ? state.Colors[0].Resolution.BaseAddress : 0;
        var colorWidth = state.ColorCount != 0 ? state.Colors[0].Resolution.Extent.Width : 0;
        var colorHeight = state.ColorCount != 0 ? state.Colors[0].Resolution.Extent.Height : 0;
        var depth = state.Depth.HasTarget ? state.Depth.Target : default;
        RenderTrace.Write(
            $"DrawState seq={RenderTrace.NextSequence()} submit={submitId} name={draw.Name} export=0x{shader.Vertex.ExportAddress:X16} " +
            $"pixel=0x{shader.Pixel.Address:X16} pixelActive={state.PixelActive} count={draw.Count} instances={draw.InstanceCount} " +
            $"colors={state.ColorCount} color=0x{colorAddress:X10}:{colorWidth}x{colorHeight} targetMask=0x{banks.Context.RenderTargetMask:X8} " +
            $"depth=0x{depth.Target.DepthAddress:X10}:{depth.Target.Width}x{depth.Target.Height}:{(int)depth.Target.Format} " +
            $"depthState={(depth.State.DepthTestEnabled ? 1 : 0)}/{(depth.State.DepthWriteEnabled ? 1 : 0)}/{(int)depth.State.DepthCompare}");
    }
}
