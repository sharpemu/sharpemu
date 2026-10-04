// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Logging;
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
    Polygon = 21,
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
    // Prospero uses both the full NGG mask and compact primitive-shader masks
    // when the task/mesh front end has already folded the upper enable bits
    // away. Little Nightmares also emits 0x2030 for a primitive path with no
    // geometry shader; the remaining bits describe the fixed NGG setup.
    // GS_W32_EN (bit 22) and VS_W32_EN (bit 23) only select the wave size;
    // graphics stages always compile as wave32, so they do not change the path.
    private const uint VgtShaderStagesWaveSizeBits = (1u << 22) | (1u << 23);

    private static bool IsPrimitiveShaderStageMask(uint stages) =>
        (stages & ~VgtShaderStagesWaveSizeBits) is 0x02002000 or 0x00002000 or 0x00002030;
    private const uint MaxOutputPerSubgroupLimit = 0x40;
    private static readonly SharpEmuLogger GeometryLog = SharpEmuLog.For("GPU.Geometry");
    private static readonly bool LogGeometry = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_GEOMETRY"),
        "1",
        StringComparison.Ordinal);
    // A low-volume shader identity trace for comparing the first executed
    // graphics program pairs with another renderer. This is deliberately
    // independent of SHARPEMU_LOG_AGC, whose per-draw output changes timing.
    private static readonly bool TraceGraphicsProgramPairs = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GRAPHICS_PROGRAMS"),
        "1",
        StringComparison.Ordinal);
    private const int MaxGraphicsProgramTracePairs = 128;
    // Legacy fused draws are uncommon and often appear well after the generic
    // geometry sample is exhausted. Keep this trace uncapped so a title-screen
    // ES+GS draw cannot disappear behind earlier primitive-shader traffic.
    private static readonly bool LogLegacyFusedGeometry =
        LogGeometry ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_LEGACY_FUSED_GEOMETRY"),
            "1",
            StringComparison.Ordinal);
    // Native indirect dispatch is an optimization and remains opt-in until all
    // guest buffer ownership and synchronization paths are validated.
    private static readonly bool NativeIndirectDispatchEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_INDIRECT_DISPATCH"), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_INDIRECT_DISPATCH"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly bool ExperimentalLegacyFusedGeometryEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_EXPERIMENTAL_LEGACY_GEOMETRY"), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_EXPERIMENTAL_LEGACY_GEOMETRY"), "true", StringComparison.OrdinalIgnoreCase);
    // Diagnostic guard: build and validate fused mesh pipelines without ever
    // recording a mesh dispatch. This isolates translation/pipeline creation
    // from execution when investigating a host device loss.
    private static readonly bool ExperimentalLegacyFusedGeometryCompileOnly =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_EXPERIMENTAL_LEGACY_GEOMETRY_COMPILE_ONLY"), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_EXPERIMENTAL_LEGACY_GEOMETRY_COMPILE_ONLY"), "true", StringComparison.OrdinalIgnoreCase);
    private static int _geometryTraceDraws;
    private static int _geometryWarningShown;

    private readonly IRenderHost _host;
    private readonly IShaderPipelineProvider _pipelines;
    private readonly bool _strictDrawResources;
    private readonly bool _nativeIndirectDispatch;
    private readonly bool _experimentalLegacyFusedGeometry;
    private readonly HashSet<(ulong VertexProgramId, ulong PixelProgramId)> _reportedGraphicsProgramPairs = [];
    private readonly HashSet<(ulong ShaderHash, ImageType ImageType, ImageViewType ViewType)> _reportedDrawImageTypeMismatches = [];

    public RenderExecutor(IRenderHost host, IShaderPipelineProvider pipelines)
        : this(
            host,
            pipelines,
            Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE") != "0",
            NativeIndirectDispatchEnabled,
            ExperimentalLegacyFusedGeometryEnabled)
    {
    }

    internal RenderExecutor(
        IRenderHost host,
        IShaderPipelineProvider pipelines,
        bool strictDrawResources,
        bool nativeIndirectDispatch = false,
        bool experimentalLegacyFusedGeometry = false)
    {
        _host = host;
        _pipelines = pipelines;
        _strictDrawResources = strictDrawResources;
        _nativeIndirectDispatch = nativeIndirectDispatch;
        _experimentalLegacyFusedGeometry = experimentalLegacyFusedGeometry;
    }

    private readonly record struct DrawCall(string Name, RecordedOperation Operation, uint Count, uint InstanceCount, uint FirstInstance);

    // IndirectArgumentsAddress: the GPU reads the indexed draw's counts from guest memory there.
    private readonly record struct DrawEmission(bool Indexed, int VertexOffset, uint FirstVertex, uint FirstInstance, ulong IndirectArgumentsAddress = 0);

    private readonly record struct IndexSource(
        bool Enabled,
        ulong Address,
        byte[]? HostData,
        ulong Size,
        IndexType Type,
        uint GuestElementSize);

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

    private bool IsLegacyFusedGeometryDraw(RegisterBanks banks)
    {
        var exportAddress = banks.Shader.Vertex.ExportAddress;
        return MergedGeometryProgramResolver.HasMergedGeometryStage(banks.Context.ShaderStages) ||
               (exportAddress != 0 && _pipelines.IsFusedGraphicsProgram(exportAddress));
    }

    private void TraceLegacyFusedDisposition(
        RegisterBanks banks,
        string drawKind,
        uint count,
        uint instanceCount,
        string disposition,
        string detail = "")
    {
        if (!LogLegacyFusedGeometry || !IsLegacyFusedGeometryDraw(banks))
        {
            return;
        }

        var vertex = banks.Shader.Vertex;
        var shaderInterface = banks.Context.ShaderInterface;
        var continuationAddress = 0ul;
        var fusedRegistered = vertex.ExportAddress != 0 &&
            _pipelines.TryGetFusedGraphicsProgram(vertex.ExportAddress, out continuationAddress);
        GeometryLog.Info(
            $"LegacyFusedDisposition draw={drawKind} disposition={disposition} count={count} instances={instanceCount} " +
            $"stages=0x{banks.Context.ShaderStages:X8} export=0x{vertex.ExportAddress:X16} geometry=0x{vertex.GeometryAddress:X16} " +
            $"pixel=0x{banks.Shader.Pixel.Address:X16} primitive=0x{banks.UserConfig.PrimitiveType:X8} " +
            $"maxVerticesOut=0x{shaderInterface.GeometryMaxVerticesOut:X8} maxOutput=0x{shaderInterface.MaxOutputPerSubgroup:X8} " +
            $"experimental={_experimentalLegacyFusedGeometry} meshSupported={_pipelines.SupportsFusedGeometry} " +
            $"fusedRegistered={fusedRegistered} continuation=0x{continuationAddress:X16} " +
            $"continuationMatch={MergedGeometryProgramResolver.ContinuationMatches(vertex.GeometryAddress, continuationAddress)}" +
            $"{(detail.Length == 0 ? string.Empty : $" {detail}")}");
    }

    public void DrawIndexed(ulong submitId, RegisterBanks banks, in DrawIndexedArguments arguments)
    {
        if (arguments.IndirectArgumentsAddress != 0 && !CanDrawIndirectOnGpu(banks, in arguments))
        {
            DrawIndexedWithCpuArguments(submitId, banks, in arguments);
            return;
        }

        DrawIndexedCore(submitId, banks, in arguments);
    }

    // The GPU reads the arguments of an indirect draw unless the draw is emulated from
    // its counts: strips (metadata clear quads), legacy primitives, 8-bit indices and
    // restart indices converted on the CPU.
    private static bool CanDrawIndirectOnGpu(RegisterBanks banks, in DrawIndexedArguments arguments)
    {
        switch ((GuestPrimitiveType)banks.UserConfig.PrimitiveType)
        {
            case GuestPrimitiveType.PointList:
            case GuestPrimitiveType.LineList:
            case GuestPrimitiveType.LineStrip:
            case GuestPrimitiveType.TriangleList:
            case GuestPrimitiveType.TriangleFan:
            case GuestPrimitiveType.Polygon:
            case GuestPrimitiveType.RectangleList:
                break;
            default:
                return false;
        }

        var index32 = (GuestIndexType)arguments.IndexTypeAndSize == GuestIndexType.Index32;
        if (!index32 && (GuestIndexType)arguments.IndexTypeAndSize != GuestIndexType.Index16)
        {
            return false;
        }

        if ((banks.UserConfig.PrimitiveResetControl & 0x1) != 0)
        {
            var indexMask = index32 ? uint.MaxValue : 0xFFFFu;
            if ((banks.Context.PrimitiveResetIndex & indexMask) != indexMask)
            {
                return false;
            }
        }

        return (ulong)arguments.IndexCount * (index32 ? 4ul : 2ul) <= MaxIndirectIndexBufferBytes;
    }

    private const ulong MaxIndirectIndexBufferBytes = 64ul << 20;

    // Reads the indirect arguments now and draws from them, as the interpreter would have.
    private void DrawIndexedWithCpuArguments(ulong submitId, RegisterBanks banks, in DrawIndexedArguments arguments)
    {
        Span<byte> bytes = stackalloc byte[(int)IndexedIndirectArgumentsSize];
        if (!_host.TryReadGuest(arguments.IndirectArgumentsAddress, bytes))
        {
            throw _host.Fatal($"The indirect draw arguments are unreadable: address=0x{arguments.IndirectArgumentsAddress:X16}.");
        }

        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes);
        var elementSize = (GuestIndexType)arguments.IndexTypeAndSize switch
        {
            GuestIndexType.Index16 => 2ul,
            GuestIndexType.Index32 => 4ul,
            _ => 1ul,
        };
        var resolved = arguments with
        {
            IndexCount = arguments.UnboundedIndexBuffer ? words[0] : Math.Min(words[0], arguments.IndexCount),
            InstanceCount = words[1],
            IndexAddress = arguments.IndexAddress + (words[2] * elementSize),
            BaseVertex = unchecked((int)words[3]),
            FirstInstance = words[4],
            IndirectArgumentsAddress = 0,
            UnboundedIndexBuffer = false,
        };
        if (resolved.IndexCount == 0 || resolved.InstanceCount == 0)
        {
            _host.ResetBindings();
            return;
        }

        DrawIndexedCore(submitId, banks, in resolved);
    }

    private void DrawIndexedCore(ulong submitId, RegisterBanks banks, in DrawIndexedArguments arguments)
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
            TraceLegacyFusedDisposition(banks, "indexed", arguments.IndexCount, arguments.InstanceCount, "empty-draw");
            return;
        }

        if (ConsumesColorMetadataOperation(banks.Context) || TryDepthStencilCopy(banks.Context))
        {
            TraceLegacyFusedDisposition(banks, "indexed", arguments.IndexCount, arguments.InstanceCount, "metadata-or-depth-copy");
            _host.ResetBindings();
            return;
        }

        if (!HasValidVertexShader(shader))
        {
            TraceLegacyFusedDisposition(banks, "indexed", arguments.IndexCount, arguments.InstanceCount, "missing-vertex-program");
            return;
        }

        if (IsUnsupportedGeometryStage(submitId, "indexed", banks))
        {
            TraceLegacyFusedDisposition(banks, "indexed", arguments.IndexCount, arguments.InstanceCount, "unsupported-geometry-stage");
            return;
        }

        TraceLegacyFusedDisposition(banks, "indexed", arguments.IndexCount, arguments.InstanceCount, "stage-gate-passed");

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write(
                $"DrawIndexed submit={submitId} indexTypeAndSize=0x{arguments.IndexTypeAndSize:X8} count=0x{arguments.IndexCount:X8} " +
                $"indexAddress=0x{arguments.IndexAddress:X16} instances=0x{arguments.InstanceCount:X8} baseVertex=0x{(uint)arguments.BaseVertex:X8} firstInstance=0x{arguments.FirstInstance:X8}");
        }

        ValidateDrawRegisters(banks);
        if (!ResolveTopology(userConfig, autoDraw: false, out var topology))
        {
            TraceLegacyFusedDisposition(banks, "indexed", arguments.IndexCount, arguments.InstanceCount, "no-primitive-topology");
            return;
        }

        var primitiveRestart = ResolvePrimitiveRestart(banks, topology, arguments.IndexTypeAndSize);
        IndexType indexType;
        ulong indexSize;
        uint guestIndexElementSize;
        var expandIndex8 = false;
        switch ((GuestIndexType)arguments.IndexTypeAndSize)
        {
            case GuestIndexType.Index16:
                indexType = IndexType.Uint16;
                guestIndexElementSize = 2;
                indexSize = 2ul * arguments.IndexCount;
                break;
            case GuestIndexType.Index32:
                indexType = IndexType.Uint32;
                guestIndexElementSize = 4;
                indexSize = 4ul * arguments.IndexCount;
                break;
            case GuestIndexType.Index8:
                indexType = IndexType.Uint16;
                guestIndexElementSize = 1;
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
            indexType,
            guestIndexElementSize);
        var state = DrawState.Create();
        if (!TryResolveDrawTargets(banks, in draw, ref state))
        {
            TraceLegacyFusedDisposition(banks, "indexed", arguments.IndexCount, arguments.InstanceCount, "target-resolution-failed");
            _host.ResetBindings();
            return;
        }

        if (arguments.IndirectArgumentsAddress != 0 && state.ColorCount == 0 && !state.Depth.HasTarget)
        {
            // A targetless draw may be retained and replayed later; it needs its counts.
            DrawIndexedWithCpuArguments(submitId, banks, in arguments);
            return;
        }

        ResolveShaderPrograms(banks, ref state);
        if (arguments.IndirectArgumentsAddress != 0 && state.Programs.VertexInput.Mesh.IsActive)
        {
            // Mesh dispatch dimensions and push data are derived from the draw
            // counts. Resolve the guest arguments before either is calculated;
            // vkCmdDrawIndexedIndirect is not a mesh-task command.
            DrawIndexedWithCpuArguments(submitId, banks, in arguments);
            return;
        }

        if (!ApplyProgramAdaptations(banks, in draw, ref state, new TargetlessDrawArguments(submitId, true, arguments, default)))
        {
            _host.ResetBindings();
            return;
        }

        if (PruneUnexportedColorTargets(banks, in draw, ref state))
        {
            // Pixel output locations are compact host attachment indices. Re-specialize the
            // programs after removing a hole (for example stale MRT0 with exported MRT1), so
            // the retained guest slot writes the same compact attachment that RecordDraw binds.
            ResolveShaderPrograms(banks, ref state);
            if (!state.Programs.Available)
            {
                TraceDrawDisposition(banks, in draw, "program-unavailable-after-mrt-prune");
                TraceLegacyFusedDisposition(banks, "indexed", arguments.IndexCount, arguments.InstanceCount, "program-unavailable-after-mrt-prune");
                _host.ResetBindings();
                return;
            }
        }

        if (state.ColorCount == 0 && !state.Depth.HasTarget && !state.PixelActive)
        {
            TraceDrawDisposition(banks, in draw, "no-framebuffer-after-mrt-prune");
            TraceLegacyFusedDisposition(banks, "indexed", arguments.IndexCount, arguments.InstanceCount, "no-framebuffer-after-mrt-prune");
            _host.ResetBindings();
            return;
        }

        TraceLegacyFusedDisposition(
            banks,
            "indexed",
            arguments.IndexCount,
            arguments.InstanceCount,
            "record-begin",
            $"meshActive={state.Programs.VertexInput.Mesh.IsActive} pixelActive={state.PixelActive} colors={state.ColorCount} depth={state.Depth.HasTarget}");

        TraceDrawState(submitId, banks, in draw, in state);
        var indirect = arguments.OffsetSource == DrawOffsetSource.IndirectArguments;
        var vertexOffset = indirect
            ? arguments.BaseVertex
            : ResolveVertexOffset(userConfig.IndexOffset, state.Programs.VertexInput) + arguments.BaseVertex;
        var emission = new DrawEmission(
            true,
            vertexOffset,
            0,
            indirect ? arguments.FirstInstance : ResolveInstanceOffset(state.Programs.VertexInput),
            arguments.IndirectArgumentsAddress);
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
            TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "empty-draw");
            return;
        }

        if (ConsumesColorMetadataOperation(banks.Context) || TryDepthStencilCopy(banks.Context))
        {
            TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "metadata-or-depth-copy");
            _host.ResetBindings();
            return;
        }

        if (!HasValidVertexShader(shader))
        {
            TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "missing-vertex-program");
            return;
        }

        if (IsUnsupportedGeometryStage(submitId, "auto", banks))
        {
            TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "unsupported-geometry-stage");
            return;
        }

        TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "stage-gate-passed");

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
            TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "target-resolution-failed");
            _host.ResetBindings();
            return;
        }

        if (!ResolveTopology(userConfig, autoDraw: true, out var topology))
        {
            TraceDrawDisposition(banks, in draw, "no-primitive-topology");
            TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "no-primitive-topology");
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

            TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "rectangle-without-parameter-exports");
            _host.ResetBindings();
            return;
        }

        if (!ApplyProgramAdaptations(banks, in draw, ref state, new TargetlessDrawArguments(submitId, false, default, arguments)))
        {
            _host.ResetBindings();
            return;
        }

        if (PruneUnexportedColorTargets(banks, in draw, ref state))
        {
            ResolveShaderPrograms(banks, ref state);
            if (!state.Programs.Available)
            {
                TraceDrawDisposition(banks, in draw, "program-unavailable-after-mrt-prune");
                TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "program-unavailable-after-mrt-prune");
                _host.ResetBindings();
                return;
            }
        }

        if (state.ColorCount == 0 && !state.Depth.HasTarget && !state.PixelActive)
        {
            TraceDrawDisposition(banks, in draw, "no-framebuffer-after-mrt-prune");
            TraceLegacyFusedDisposition(banks, "auto", arguments.VertexCount, arguments.InstanceCount, "no-framebuffer-after-mrt-prune");
            _host.ResetBindings();
            return;
        }

        TraceLegacyFusedDisposition(
            banks,
            "auto",
            arguments.VertexCount,
            arguments.InstanceCount,
            "record-begin",
            $"meshActive={state.Programs.VertexInput.Mesh.IsActive} pixelActive={state.PixelActive} colors={state.ColorCount} depth={state.Depth.HasTarget}");

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

    // Legacy ES+GS halves run only when AGC registered the export address as a
    // fused program.  That keeps arbitrary geometry state out of the ordinary
    // vertex path while letting the translator consume both registered halves.
    private bool IsUnsupportedGeometryStage(ulong submitId, string drawKind, RegisterBanks banks)
    {
        var context = banks.Context;
        var shaderInterface = context.ShaderInterface;
        var vertex = banks.Shader.Vertex;
        var stages = context.ShaderStages;
        var knownOutputPrimitive = IsKnownGeometryOutputPrimitiveType(shaderInterface.GeometryOutputPrimitiveType);
        var continuationAddress = 0ul;
        var fusedRegistered = vertex.ExportAddress != 0 &&
            MergedGeometryProgramResolver.HasMergedGeometryStage(stages) &&
            _pipelines.TryGetFusedGraphicsProgram(vertex.ExportAddress, out continuationAddress);
        var activeMergedGeometry = MergedGeometryProgramResolver.IsActive(
            stages,
            vertex.ExportAddress,
            vertex.GeometryAddress,
            fusedRegistered,
            continuationAddress);
        var primitiveShaderVertexPath =
            !activeMergedGeometry &&
            IsPrimitiveShaderStageMask(stages) && vertex.ExportAddress != 0 &&
            (vertex.GeometryAddress == 0 ||
             (shaderInterface.GeometryMaxVerticesOut == 0 && knownOutputPrimitive));
        var legacyFusedGeometryPath =
            activeMergedGeometry &&
            shaderInterface.GeometryMaxVerticesOut != 0 && knownOutputPrimitive &&
            _pipelines.SupportsFusedGeometry;
        var unsupportedMergedGeometry = activeMergedGeometry && !legacyFusedGeometryPath;
        var unsupportedStageMask = stages != 0 && !IsPrimitiveShaderStageMask(stages) && !legacyFusedGeometryPath;
        var unsupportedGeometryStage =
            vertex.ExportAddress != 0 && vertex.GeometryAddress != 0 &&
            !primitiveShaderVertexPath && !legacyFusedGeometryPath;
        var geometryRegisters = !primitiveShaderVertexPath && !legacyFusedGeometryPath &&
            ((shaderInterface.PrimitiveShaderSubgroupControl != 0 && shaderInterface.PrimitiveShaderSubgroupControl != 1) ||
             shaderInterface.GeometryMaxVerticesOut != 0 ||
             !knownOutputPrimitive ||
             shaderInterface.MaxOutputPerSubgroup > MaxOutputPerSubgroupLimit);
        var unsupported = unsupportedMergedGeometry || unsupportedStageMask || unsupportedGeometryStage || geometryRegisters;

        if (LogLegacyFusedGeometry &&
            (MergedGeometryProgramResolver.HasMergedGeometryStage(stages) || fusedRegistered))
        {
            GeometryLog.Info(
                $"LegacyFusedDecision submit={submitId} draw={drawKind} decision={(unsupported ? "reject" : "accept")} " +
                $"export=0x{vertex.ExportAddress:X16} geometry=0x{vertex.GeometryAddress:X16} " +
                $"experimental={_experimentalLegacyFusedGeometry} meshSupported={_pipelines.SupportsFusedGeometry} " +
                $"fusedRegistered={fusedRegistered} continuation=0x{continuationAddress:X16} " +
                $"continuationMatch={MergedGeometryProgramResolver.ContinuationMatches(vertex.GeometryAddress, continuationAddress)} " +
                $"knownOutputPrimitive={knownOutputPrimitive} " +
                $"maxVerticesOut=0x{shaderInterface.GeometryMaxVerticesOut:X8} maxOutput=0x{shaderInterface.MaxOutputPerSubgroup:X8} " +
                $"primitiveGroup=0x{banks.UserConfig.GeometryEngineControl.PrimitiveGroupSize:X4} " +
                $"vertexGroup=0x{banks.UserConfig.GeometryEngineControl.VertexGroupSize:X4} " +
                $"activeMerged={activeMergedGeometry} legacyFusedPath={legacyFusedGeometryPath} " +
                $"unsupportedMerged={unsupportedMergedGeometry} unsupportedMask={unsupportedStageMask} " +
                $"unsupportedGeometry={unsupportedGeometryStage} unsupportedRegisters={geometryRegisters}");
        }

        var geometryRelevant = stages != 0 || vertex.GeometryAddress != 0 || unsupported;
        if (LogGeometry && geometryRelevant && Interlocked.Increment(ref _geometryTraceDraws) <= 32)
        {
            var geometryControl = banks.UserConfig.GeometryEngineControl;
            GeometryLog.Info(
                $"GeometryTrace submit={submitId} draw={drawKind} decision={(unsupported ? "reject" : "accept")} " +
                $"stages=0x{stages:X8} export=0x{vertex.ExportAddress:X16} geometry=0x{vertex.GeometryAddress:X16} " +
                $"primitiveGroup=0x{geometryControl.PrimitiveGroupSize:X4} vertexGroup=0x{geometryControl.VertexGroupSize:X4} " +
                $"subgroupControl=0x{shaderInterface.PrimitiveShaderSubgroupControl:X8} onChipControl=0x{shaderInterface.GeometryOnChipControl:X8} " +
                $"exportVertices=0x{shaderInterface.ExportVerticesPerSubgroup:X8} geometryPrimitives=0x{shaderInterface.GeometryPrimitivesPerSubgroup:X8} " +
                $"instancedPrimitives=0x{shaderInterface.GeometryInstancedPrimitivesInSubgroup:X8} maxOutput=0x{shaderInterface.MaxOutputPerSubgroup:X8} " +
                $"maxVerticesOut=0x{shaderInterface.GeometryMaxVerticesOut:X8} outputPrimitive=0x{shaderInterface.GeometryOutputPrimitiveType:X8} " +
                $"primitiveVertexPath={primitiveShaderVertexPath} activeMerged={activeMergedGeometry} legacyFusedPath={legacyFusedGeometryPath} " +
                $"unsupportedMerged={unsupportedMergedGeometry} " +
                $"unsupportedMask={unsupportedStageMask} unsupportedGeometry={unsupportedGeometryStage} unsupportedRegisters={geometryRegisters}");
        }

        if (!unsupported)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _geometryWarningShown, 1) == 0)
        {
            Console.Error.WriteLine(
                "Warning: the title uses unsupported graphics pipelines; some draw calls were skipped. " +
                $"stages=0x{stages:X8} subgroup=0x{shaderInterface.PrimitiveShaderSubgroupControl:X8} " +
                $"maxOutput=0x{shaderInterface.MaxOutputPerSubgroup:X8} " +
                $"maxVerticesOut=0x{shaderInterface.GeometryMaxVerticesOut:X8} " +
                $"outputPrimitive=0x{shaderInterface.GeometryOutputPrimitiveType:X8} " +
                $"export=0x{vertex.ExportAddress:X16} geometry=0x{vertex.GeometryAddress:X16} " +
                $"unsupportedMerged={unsupportedMergedGeometry} unsupportedMask={unsupportedStageMask} unsupportedGeometry={unsupportedGeometryStage} " +
                $"unsupportedRegisters={geometryRegisters}.");
        }

        // The dedicated geometry trace must retain rejected draws even after
        // its small accepted-draw sample is exhausted. Requiring the broad
        // AGC trace here hid the actual rejected state behind thousands of
        // unrelated pipeline lines.
        if ((RenderTrace.Enabled || LogGeometry) && RenderTrace.GeometryStageSkip())
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
        // The export format describes data; it does not enable shader execution.
        return control.KillEnable || control.DepthExportEnable ||
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
            case GuestPrimitiveType.Polygon:
                topology = PrimitiveTopology.TriangleFan;
                break;
            case GuestPrimitiveType.TriangleStrip:
                topology = PrimitiveTopology.TriangleStrip;
                break;
            case GuestPrimitiveType.RectangleList:
            case GuestPrimitiveType.RectangleListLegacy:
                // Keep both guest rectangle encodings on the rectangle-list marker path.
                // Vulkan lowers this to a three-control-point patch whose fixed TCS/TES
                // reconstruct the fourth corner and its parameter exports.
                topology = PrimitiveTopology.PatchList;
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
        uint boundColorSlots = 0;
        foreach (ref readonly var color in BoundColors(ref state))
        {
            targetExportMapping[(int)color.Slot] = color.Resolution.ExportMapping;
            boundColorSlots |= 1u << checked((int)color.Slot);
        }

        state.Programs = _pipelines.GetGraphicsPrograms(
            banks.Shader.Vertex,
            banks.Shader.Pixel,
            context.ShaderInterface,
            context,
            banks.UserConfig,
            targetExportMapping,
            boundColorSlots,
            state.PixelActive,
            state.Depth.HasTarget);
    }

    // A guest can leave stale render-target registers enabled while its pixel program exports
    // only a subset of the MRT slots. Those targets are not attachments on hardware: retaining
    // them with a zero write mask still lets host load/clear/store operations and render-area
    // selection affect their contents. Program adaptations intentionally run before this filter
    // because recognized clear/metadata draws consume the register-bound targets themselves.
    private static bool PruneUnexportedColorTargets(RegisterBanks banks, in DrawCall draw, ref DrawState state)
    {
        var before = state.ColorCount;
        if (before == 0)
        {
            return false;
        }

        var exportMasks = state.PixelActive
            ? state.Programs.PixelInput.Stage.Program?.PixelColorExportMasks ?? 0u
            : 0u;
        var write = 0u;
        for (var read = 0u; read < before; read++)
        {
            var color = state.Colors[(int)read];
            var slotMask = (exportMasks >> checked((int)(color.Slot * 4))) & 0xFu;
            if (slotMask == 0)
            {
                continue;
            }

            state.Colors[(int)write++] = color;
        }

        for (var index = write; index < before; index++)
        {
            state.Colors[(int)index] = default;
        }

        state.ColorCount = write;
        if (write != before && (RenderTrace.Enabled || TraceGraphicsProgramPairs) && RenderTrace.MrtExportPrune())
        {
            var message =
                $"MrtExportPrune name={draw.Name} pixel=0x{banks.Shader.Pixel.Address:X16} " +
                $"pixelActive={state.PixelActive} before={before} after={write} exportMask=0x{exportMasks:X8}";
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write(message);
            }
            else if (TraceGraphicsProgramPairs)
            {
                Console.Error.WriteLine($"[GPU][TRACE][MRT_EXPORT_PRUNE] {message}");
            }
        }

        return write != before;
    }

    [System.Runtime.CompilerServices.InlineArray(RenderingState.ColorAttachmentCapacity)]
    private struct ColorComponentMapArray
    {
        private ColorComponentMap _element0;
    }

    private void TraceDrawState(ulong submitId, RegisterBanks banks, in DrawCall draw, in DrawState state)
    {
        TraceGraphicsProgramPair(submitId, banks, in draw, in state);
        if (!RenderTrace.Enabled)
        {
            return;
        }

        var shader = banks.Shader;
        var colorAddress = state.ColorCount != 0 ? state.Colors[0].Resolution.BaseAddress : 0;
        var colorWidth = state.ColorCount != 0 ? state.Colors[0].Resolution.Extent.Width : 0;
        var colorHeight = state.ColorCount != 0 ? state.Colors[0].Resolution.Extent.Height : 0;
        var depth = state.Depth.HasTarget ? state.Depth.Target : default;
        ref readonly var viewport = ref banks.Context.ScreenViewport.Viewports[0];
        var clip = banks.Context.Clip;
        RenderTrace.Write(
            $"DrawState seq={RenderTrace.NextSequence()} submit={submitId} name={draw.Name} export=0x{shader.Vertex.ExportAddress:X16} " +
            $"pixel=0x{shader.Pixel.Address:X16} pixelActive={state.PixelActive} count={draw.Count} instances={draw.InstanceCount} " +
            $"colors={state.ColorCount} color=0x{colorAddress:X10}:{colorWidth}x{colorHeight} targetMask=0x{banks.Context.RenderTargetMask:X8} " +
            $"depth=0x{depth.Target.DepthAddress:X10}:{depth.Target.Width}x{depth.Target.Height}:{(int)depth.Target.Format} " +
            $"depthState={(depth.State.DepthTestEnabled ? 1 : 0)}/{(depth.State.DepthWriteEnabled ? 1 : 0)}/{(int)depth.State.DepthCompare} " +
            $"viewportTransform=0x{banks.Context.ScreenViewport.TransformControl:X8} directXClip={clip.DirectXClipSpace} " +
            $"clipDisabled={clip.ClipDisable} nearClipDisabled={clip.NearZClipDisable} farClipDisabled={clip.FarZClipDisable} " +
            $"zScale={viewport.ZScale:R} zOffset={viewport.ZOffset:R} zBounds={viewport.MinDepth:R}/{viewport.MaxDepth:R} " +
            $"hostDepthRange={viewport.ZOffset - (clip.DirectXClipSpace ? 0f : viewport.ZScale):R}/{viewport.ZScale + viewport.ZOffset:R}");
    }

    private void TraceGraphicsProgramPair(ulong submitId, RegisterBanks banks, in DrawCall draw, in DrawState state)
    {
        if (!TraceGraphicsProgramPairs)
        {
            return;
        }

        var programs = state.Programs;
        var key = (programs.Vertex.Id, programs.Pixel.Id);
        int ordinal;
        lock (_reportedGraphicsProgramPairs)
        {
            if (_reportedGraphicsProgramPairs.Count >= MaxGraphicsProgramTracePairs ||
                !_reportedGraphicsProgramPairs.Add(key))
            {
                return;
            }

            ordinal = _reportedGraphicsProgramPairs.Count;
        }

        var shader = banks.Shader;
        var vertexInfo = programs.VertexInput.Stage.Program;
        var pixelInfo = programs.PixelInput.Stage.Program;
        Console.Error.WriteLine(
            $"[GPU][TRACE][GRAPHICS_PROGRAM] ordinal={ordinal} submit={submitId} draw={draw.Name} " +
            $"count={draw.Count} instances={draw.InstanceCount} stages=0x{banks.Context.ShaderStages:X8} " +
            $"export=0x{shader.Vertex.ExportAddress:X16} geometry=0x{shader.Vertex.GeometryAddress:X16} " +
            $"pixel=0x{shader.Pixel.Address:X16} vertexHash=0x{vertexInfo?.Hash ?? 0:X16} " +
            $"pixelHash=0x{pixelInfo?.Hash ?? 0:X16} vertexId=0x{programs.Vertex.Id:X16} " +
            $"pixelId=0x{programs.Pixel.Id:X16} meshActive={programs.VertexInput.Mesh.IsActive} " +
            $"pixelActive={state.PixelActive} colors={state.ColorCount} depth={state.Depth.HasTarget} " +
            $"vertexFetchBuffers={programs.VertexInput.Buffers.Length} " +
            $"vertexResources={vertexInfo?.Buffers.Length ?? 0}/{vertexInfo?.Images.Length ?? 0}/{vertexInfo?.SamplerCount ?? 0} " +
            $"pixelResources={pixelInfo?.Buffers.Length ?? 0}/{pixelInfo?.Images.Length ?? 0}/{pixelInfo?.SamplerCount ?? 0} " +
            $"deviceAddress={vertexInfo?.UsesDeviceAddresses ?? false}/{pixelInfo?.UsesDeviceAddresses ?? false}");
    }
}
