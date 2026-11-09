// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
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
    private static int _geometryWarningShown;
    private static int _tessellationWarningShown;

    private readonly IRenderHost _host;
    private readonly DrawResolution _drawResolution = new();
    private readonly IShaderPipelineProvider _pipelines;
    private readonly IResolvedCommandSink _recorder;
    private readonly RecordPool _pool;
    private readonly Func<string, Exception> _fatal;

    public RenderExecutor(IRenderHost host, IShaderPipelineProvider pipelines)
        : this(host, pipelines, DefaultStrictDrawResources)
    {
    }

    internal RenderExecutor(IRenderHost host, IShaderPipelineProvider pipelines, bool strictDrawResources)
        : this(host, pipelines, new PendingGpuWrites(), new RecordPool(), strictDrawResources)
    {
    }

    private RenderExecutor(IRenderHost host, IShaderPipelineProvider pipelines, PendingGpuWrites pendingWrites, RecordPool pool, bool strictDrawResources)
        : this(host, pipelines, new RenderRecorder(host, pipelines, strictDrawResources, pendingWrites, pool), pendingWrites, pool)
    {
    }

    internal RenderExecutor(IRenderHost host, IShaderPipelineProvider pipelines, IResolvedCommandSink recorder, PendingGpuWrites? pendingWrites = null, RecordPool? pool = null)
    {
        _host = host;
        _fatal = host.Fatal;
        _pipelines = pipelines;
        _recorder = recorder;
        PendingWrites = pendingWrites ?? new PendingGpuWrites();
        _pool = pool ?? new RecordPool();
    }

    internal static bool DefaultStrictDrawResources => Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE") != "0";

    public void DrawIndexed(ulong submitId, RegisterBanks banks, in DrawIndexedArguments arguments)
    {
        using var meshTrace = MeshDrawTrace.Begin(submitId, banks.Shader.Vertex.ExportAddress, banks.Context.ShaderStages);
        try
        {
            if (arguments.IndirectArgumentsAddress != 0 &&
                (UsesMeshExecution(banks) ? !CanDrawMeshIndirectOnGpu(banks, in arguments) : !CanDrawIndirectOnGpu(banks, in arguments)))
            {
                DrawIndexedWithCpuArguments(submitId, banks, in arguments);
                return;
            }

            DrawIndexedCore(submitId, banks, in arguments);
        }
        catch (Exception exception) when (meshTrace is not null)
        {
            meshTrace?.Complete("failed");
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("failure", $"type={exception.GetType().Name} message={exception.Message}");
            throw;
        }
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

    // List inputs ignore restart and use the learned index extent as their capacity.
    // Other mesh inputs keep the CPU path.
    private bool CanDrawMeshIndirectOnGpu(RegisterBanks banks, in DrawIndexedArguments arguments)
    {
        if (!_host.SupportsIndirectMeshDraw ||
            arguments.IndexCount is 0 or > IndirectMeshDrawRequest.MaximumIndexCapacity ||
            (GuestPrimitiveType)banks.UserConfig.PrimitiveType is not (GuestPrimitiveType.PointList or GuestPrimitiveType.TriangleList) ||
            (GuestIndexType)arguments.IndexTypeAndSize is not (GuestIndexType.Index16 or GuestIndexType.Index32))
            return false;
        var elementSize = (GuestIndexType)arguments.IndexTypeAndSize == GuestIndexType.Index16 ? 2ul : 4ul;
        var start = arguments.IndexAddress & ~3ul;
        var size = ((arguments.IndexAddress - start + arguments.IndexCount * elementSize) + 3) & ~3ul;
        if ((arguments.IndirectArgumentsAddress & 3) != 0 || (arguments.IndexAddress & (elementSize - 1)) != 0 ||
            start > ulong.MaxValue - size || arguments.IndirectArgumentsAddress > ulong.MaxValue - IndexedIndirectArgumentsSize)
            return false;
        return _host.ClampMappedSize(start, size) == size &&
            _host.ClampMappedSize(arguments.IndirectArgumentsAddress, IndexedIndirectArgumentsSize) == IndexedIndirectArgumentsSize;
    }

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
            var skipped = _pool.RentDraw();
            skipped.ResetBindings = true;
            Record(skipped);
            return;
        }

        DrawIndexedCore(submitId, banks, in resolved);
    }

    private void DrawIndexedCore(ulong submitId, RegisterBanks banks, in DrawIndexedArguments arguments)
    {
        if (MeshDrawTrace.Active) MeshDrawTrace.Write("selection", $"indexed=true count={arguments.IndexCount}");
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawExecutor);
        var record = _pool.RentDraw();
        record.Announce = true;
        record.Debug = new DebugInformation(RecordedOperation.DrawIndex, submitId, arguments.IndexCount, 0, 1, arguments.InstanceCount, arguments.IndexAddress);
        record.SubmitId = submitId;
        var traceSample = IndexedDrawTrace.Enabled && _host is ICommandStreamHost traceHost
            ? IndexedDrawTrace.Capture(arguments.IndexCount, arguments.IndexTypeAndSize, arguments.IndexAddress, traceHost.Memory.TryRead)
            : null;
        var tracePacket = arguments.PacketAddress;
        void TraceDisposition(string disposition)
        {
            IndexedDrawTrace.Write(traceSample, disposition == "recorded" ? "record-path-returned" : disposition, submitId, tracePacket);
            TraceDrawTargets(banks, disposition);
        }
        IndexedDrawTrace.Write(traceSample, "executor", submitId, tracePacket);
        if (arguments.OffsetSource == DrawOffsetSource.Packet && arguments.FirstInstance != 0)
        {
            throw _host.Fatal($"A packet indexed draw carries a first instance: firstInstance={arguments.FirstInstance}.");
        }

        var userConfig = banks.UserConfig;
        var shader = banks.Shader;
        if (arguments.IndexCount == 0 || arguments.InstanceCount == 0)
        {
            TraceDisposition("zero-count");
            Record(record);
            return;
        }

        if (ConsumesColorMetadataOperation(banks.Context) || TryResolveDepthStencilCopy(banks.Context, record))
        {
            TraceDisposition("metadata-operation");
            record.ResetBindings = true;
            Record(record);
            return;
        }

        if (Pipelines.ShaderPipelineCache.IsMergedTessellationMask(banks.Context.ShaderStages))
        {
            if (IsSupportedTessellation(banks))
            {
                DrawTessellationIndexed(submitId, banks, arguments, record);
            }
            else Record(record);

            TraceDisposition("tessellation");
            return;
        }

        if (!HasValidVertexShader(shader))
        {
            TraceDisposition("missing-vertex-shader");
            Record(record);
            return;
        }

        if (IsUnsupportedGeometryStage(banks, autoDraw: false))
        {
            TraceDisposition("unsupported-geometry-stage");
            Record(record);
            return;
        }

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write(
                $"DrawIndexed submit={submitId} indexTypeAndSize=0x{arguments.IndexTypeAndSize:X8} count=0x{arguments.IndexCount:X8} " +
                $"indexAddress=0x{arguments.IndexAddress:X16} instances=0x{arguments.InstanceCount:X8} baseVertex=0x{(uint)arguments.BaseVertex:X8} firstInstance=0x{arguments.FirstInstance:X8}");
        }

        ValidateChangedDrawRegisters(banks);
        if (!ResolveTopology(userConfig, autoDraw: false, out var topology))
        {
            TraceDisposition("no-topology");
            Record(record);
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
        ref var state = ref record.State;
        state.Begin(arguments.StartInstanceUserData);
        if (!TryResolveDrawTargets(banks, in draw, ref state, record))
        {
            TraceDisposition("no-targets");
            record.ResetBindings = true;
            Record(record);
            return;
        }

        record.TargetsResolved = true;
        if (arguments.IndirectArgumentsAddress != 0 && state.ColorCount == 0 && !state.Depth.HasTarget)
        {
            Record(record);
            DrawIndexedWithCpuArguments(submitId, banks, in arguments);
            return;
        }

        ResolveShaderPrograms(banks, ref state, indexed: true);
        if (!ApplyProgramAdaptations(banks, in draw, ref state, new TargetlessDrawArguments(submitId, true, arguments, default), record))
        {
            TraceDisposition("adapted");
            record.ResetBindings = true;
            Record(record);
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
            indirect ? arguments.FirstInstance : ResolveInstanceOffset(state.Programs.VertexInput),
            arguments.IndirectArgumentsAddress);
        record.IndexedTrace = traceSample;
        record.PacketAddress = tracePacket;
        FillDraw(record, banks, in draw, ref state, topology, in emission, in indexSource, primitiveRestart, setBindDebug: true, setAutoDebug: false);
        TraceDisposition("recorded");
        Record(record);
    }

    public void DrawAuto(ulong submitId, RegisterBanks banks, in DrawAutoArguments arguments)
    {
        using var meshTrace = MeshDrawTrace.Begin(submitId, banks.Shader.Vertex.ExportAddress, banks.Context.ShaderStages);
        try
        {
            if (arguments.IndirectArgumentsAddress != 0 && !CanDrawAutoIndirectOnGpu(banks, arguments.IndirectArgumentsAddress))
            {
                DrawAutoWithCpuArguments(submitId, banks, in arguments);
                return;
            }

            DrawAutoCore(submitId, banks, arguments);
        }
        catch (Exception exception) when (meshTrace is not null)
        {
            meshTrace?.Complete("failed");
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("failure", $"type={exception.GetType().Name} message={exception.Message}");
            throw;
        }
    }

    private bool CanDrawAutoIndirectOnGpu(RegisterBanks banks, ulong argumentsAddress)
    {
        if (UsesMeshExecution(banks) || Pipelines.ShaderPipelineCache.IsMergedTessellationMask(banks.Context.ShaderStages) ||
            (argumentsAddress & 3) != 0 || argumentsAddress > ulong.MaxValue - AutoIndirectArgumentsSize ||
            _host.ClampMappedSize(argumentsAddress, AutoIndirectArgumentsSize) != AutoIndirectArgumentsSize)
        {
            return false;
        }

        return (GuestPrimitiveType)banks.UserConfig.PrimitiveType is GuestPrimitiveType.PointList or GuestPrimitiveType.LineList or
            GuestPrimitiveType.LineStrip or GuestPrimitiveType.TriangleList or GuestPrimitiveType.TriangleStrip or
            GuestPrimitiveType.TriangleFan or GuestPrimitiveType.Polygon or GuestPrimitiveType.RectangleList;
    }

    private void DrawAutoWithCpuArguments(ulong submitId, RegisterBanks banks, in DrawAutoArguments arguments)
    {
        Span<byte> bytes = stackalloc byte[(int)AutoIndirectArgumentsSize];
        if (!_host.TryReadGuest(arguments.IndirectArgumentsAddress, bytes))
        {
            throw _host.Fatal($"The indirect draw arguments are unreadable: address=0x{arguments.IndirectArgumentsAddress:X16}.");
        }

        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes);
        DrawAutoCore(submitId, banks, arguments with
        {
            VertexCount = words[0],
            InstanceCount = words[1],
            FirstVertex = words[2],
            FirstInstance = words[3],
            IndirectArgumentsAddress = 0,
        });
    }

    private void DrawAutoCore(ulong submitId, RegisterBanks banks, in DrawAutoArguments arguments)
    {
        if (MeshDrawTrace.Active)
            MeshDrawTrace.Write("selection", $"count={arguments.VertexCount} instances={arguments.InstanceCount} firstVertex={arguments.FirstVertex} firstInstance={arguments.FirstInstance} primitive={banks.UserConfig.PrimitiveType} geometry=0x{banks.Shader.Vertex.GeometryAddress:X16} hull=0x{banks.Shader.Vertex.HullAddress:X16} local=0x{banks.Shader.Vertex.LocalAddress:X16}");
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawExecutor);
        var record = _pool.RentDraw();
        record.Announce = true;
        record.Debug = new DebugInformation(RecordedOperation.DrawIndexAuto, submitId, arguments.VertexCount, 0, arguments.FirstVertex, arguments.InstanceCount, arguments.FirstInstance);
        record.SubmitId = submitId;
        if (arguments.OffsetSource == DrawOffsetSource.Packet && arguments.FirstInstance != 0)
        {
            throw _host.Fatal($"A packet automatic draw carries a first instance: firstInstance={arguments.FirstInstance}.");
        }

        var userConfig = banks.UserConfig;
        var shader = banks.Shader;
        if (arguments.VertexCount == 0 || arguments.InstanceCount == 0)
        {
            TraceDrawTargets(banks, "zero-count");
            Record(record);
            return;
        }

        if (ConsumesColorMetadataOperation(banks.Context) || TryResolveDepthStencilCopy(banks.Context, record))
        {
            TraceDrawTargets(banks, "metadata-operation");
            record.ResetBindings = true;
            Record(record);
            return;
        }

        if (Pipelines.ShaderPipelineCache.IsMergedTessellationMask(banks.Context.ShaderStages))
        {
            var tessellationVertexOffset = unchecked((int)arguments.FirstVertex +
                (arguments.OffsetSource == DrawOffsetSource.IndirectArguments ? 0 : (int)userConfig.IndexOffset));
            if (IsSupportedTessellation(banks))
            {
                DrawTessellation(submitId, banks, arguments.VertexCount, arguments.InstanceCount, 0, 0, tessellationVertexOffset, arguments.FirstInstance, record);
            }
            else Record(record);

            TraceDrawTargets(banks, "tessellation");
            return;
        }

        if (!HasValidVertexShader(shader))
        {
            TraceDrawTargets(banks, "missing-vertex-shader");
            Record(record);
            return;
        }

        if (IsUnsupportedGeometryStage(banks, autoDraw: true))
        {
            TraceDrawTargets(banks, "unsupported-geometry-stage");
            Record(record);
            return;
        }

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write(
                $"DrawAuto submit={submitId} count=0x{arguments.VertexCount:X8} instances=0x{arguments.InstanceCount:X8} " +
                $"firstVertex=0x{arguments.FirstVertex:X8} firstInstance=0x{arguments.FirstInstance:X8}");
        }

        ValidateChangedDrawRegisters(banks);
        var draw = new DrawCall("DrawAuto", RecordedOperation.DrawIndexAuto, arguments.VertexCount, arguments.InstanceCount, arguments.FirstInstance);
        ref var state = ref record.State;
        state.Begin(arguments.StartInstanceUserData);
        if (!TryResolveDrawTargets(banks, in draw, ref state, record))
        {
            TraceDrawTargets(banks, "no-targets");
            record.ResetBindings = true;
            Record(record);
            return;
        }

        record.TargetsResolved = true;
        if (arguments.IndirectArgumentsAddress != 0 && state.ColorCount == 0 && !state.Depth.HasTarget)
        {
            Record(record);
            DrawAutoWithCpuArguments(submitId, banks, in arguments);
            return;
        }

        if (!ResolveTopology(userConfig, autoDraw: true, out var topology))
        {
            TraceDrawDisposition(banks, in draw, "no-primitive-topology");
            TraceDrawTargets(banks, "no-topology");
            record.ResetBindings = true;
            Record(record);
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

            TraceDrawTargets(banks, "rectangle-list-without-exports");
            record.ResetBindings = true;
            Record(record);
            return;
        }

        if (!ApplyProgramAdaptations(banks, in draw, ref state, new TargetlessDrawArguments(submitId, false, default, arguments), record))
        {
            TraceDrawTargets(banks, "adapted");
            record.ResetBindings = true;
            Record(record);
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
            indirect ? arguments.FirstInstance : ResolveInstanceOffset(vertexInput),
            arguments.IndirectArgumentsAddress);
        record.PacketAddress = arguments.PacketAddress;
        FillDraw(record, banks, in draw, ref state, topology, in emission, default, primitiveRestart: false, setBindDebug: false, setAutoDebug: true);
        TraceDrawTargets(banks, "recorded");
        Record(record);
    }

    private void FillDraw(ResolvedDraw record, RegisterBanks banks, in DrawCall draw, ref DrawState state, PrimitiveTopology topology,
        in DrawEmission emission, in IndexSource indexSource, bool primitiveRestart, bool setBindDebug, bool setAutoDebug)
    {
        record.Kind = ResolvedDrawKind.Draw;
        var context = banks.ShareContext();
        var vertexInput = state.Programs.VertexInput;
        var pixelInput = state.Programs.PixelInput;
        var pixelProgram = state.PixelActive ? pixelInput.Stage.Program : null;
        var writeStages = PipelineStageFlags.None;
        if (HasBufferWrites(vertexInput.Stage))
        {
            writeStages |= state.Programs.MeshInput is not null ? PipelineStageFlags.MeshShaderBitExt
                : vertexInput.Tessellation is not null ? PipelineStageFlags.TessellationEvaluationShaderBit : PipelineStageFlags.VertexShaderBit;
        }

        if (state.PixelActive && HasBufferWrites(pixelInput.Stage))
        {
            writeStages |= PipelineStageFlags.FragmentShaderBit;
        }

        record.Draw = draw;
        record.Topology = topology;
        record.PrimitiveRestart = primitiveRestart;
        record.PrimitiveType = (GuestPrimitiveType)banks.UserConfig.PrimitiveType;
        record.Emission = emission;
        record.Index = indexSource;
        record.Context = context;
        record.ContextStamp = banks.ContextStamp;
        record.DynamicStamp = banks.Stamp(RegisterStamp.ContextDynamic);
        record.TargetVersion = _drawResolution.TargetVersion;
        record.Dynamic = ResolveDynamicState(banks, in state, WrittenColorTargets(context, in state, pixelProgram), record.SubmitId, record.PacketAddress);
        record.WritesMemory = DrawWritesMemory(vertexInput.Stage) || (state.PixelActive && DrawWritesMemory(pixelInput.Stage));
        record.WriteStages = writeStages;
        record.SetBindDebug = setBindDebug;
        record.SetAutoDebug = setAutoDebug;
        record.ResetBindings = true;
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

    // Records active color-target bases with the draw's outcome for the image-clear trace.
    private void TraceDrawTargets(RegisterBanks banks, string disposition)
    {
        if (!Images.ImageClearTrace.Enabled) return;
        var context = banks.Context;
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (context.RenderTargetMaskForSlot(slot) == 0) continue;
            var address = context.ColorTargets[slot].BaseAddress;
            if (address != 0)
                _host.TraceDrawTarget(disposition, address, banks.Shader.Pixel.Address, banks.Shader.Vertex.ExportAddress);
        }
    }

    private static bool IsKnownGeometryOutputPrimitiveType(uint value) => value <= 4;

    // A tessellation configuration the bridge cannot express skips the draw, as an
    // unsupported geometry stage does, instead of stopping the emulator.
    private bool IsSupportedTessellation(RegisterBanks banks)
    {
        var shaderInterface = banks.Context.ShaderInterface;
        string? error = null;
        if (!_host.SupportsTessellation)
        {
            error = "the render host does not support native tessellation";
        }
        else if (!Gen5TessellationInfo.TryDecode(shaderInterface.TessellationFactorParameter, out var tessellation, out var factorError))
        {
            error = factorError;
        }
        else if (tessellation.Spacing == Gen5TessellationSpacing.PowerOfTwo)
        {
            error = "power-of-two partitioning needs factor conversion";
        }
        else if (!Gen5TessellationHullInfo.TryDecode(shaderInterface.LocalHullConfiguration, 0, out _, out var hullError))
        {
            error = hullError;
        }

        if (error is null)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _tessellationWarningShown, 1) == 0)
        {
            Console.Error.WriteLine(
                $"Warning: the title uses an unsupported tessellation configuration; those draw calls are skipped. {error} " +
                $"tf_param=0x{shaderInterface.TessellationFactorParameter:X8} ls_hs_config=0x{shaderInterface.LocalHullConfiguration:X8}");
        }

        return false;
    }

    private bool IsUnsupportedGeometryStage(RegisterBanks banks, bool autoDraw)
    {
        var context = banks.Context;
        var shaderInterface = context.ShaderInterface;
        var vertex = banks.Shader.Vertex;
        var stages = context.ShaderStages;
        if (UsesMeshExecution(banks))
        {
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("gate", "accepted=true");
            return false;
        }
        var primitiveShaderVertexPath =
            IsPrimitiveShaderStageMask(stages) && vertex.ExportAddress != 0 &&
            (vertex.GeometryAddress == 0 ||
             (shaderInterface.GeometryMaxVerticesOut == 0 && IsKnownGeometryOutputPrimitiveType(shaderInterface.GeometryOutputPrimitiveType)));
        var unsupportedStageMask = stages != 0 && !IsPrimitiveShaderStageMask(stages);
        var unsupportedGeometryStage = vertex.ExportAddress != 0 && vertex.GeometryAddress != 0 && !primitiveShaderVertexPath;
        var geometryRegisters = !primitiveShaderVertexPath &&
            ((shaderInterface.PrimitiveShaderSubgroupControl != 0 && shaderInterface.PrimitiveShaderSubgroupControl != 1) ||
             shaderInterface.GeometryMaxVerticesOut != 0 ||
             !IsKnownGeometryOutputPrimitiveType(shaderInterface.GeometryOutputPrimitiveType) ||
             shaderInterface.MaxOutputPerSubgroup > MaxOutputPerSubgroupLimit);
        if (!unsupportedStageMask && !unsupportedGeometryStage && !geometryRegisters)
        {
            return false;
        }

        _pipelines.DumpRejectedGraphics(banks, autoDraw);
        if (Interlocked.Exchange(ref _geometryWarningShown, 1) == 0)
        {
            Console.Error.WriteLine(
                "Warning: the title uses unsupported graphics pipelines; some draw calls were skipped. " +
                $"autoDraw={autoDraw} meshSupported={_pipelines.MeshShadersSupported} " +
                $"stages=0x{stages:X8} subgroup=0x{shaderInterface.PrimitiveShaderSubgroupControl:X8} " +
                $"maxOutput=0x{shaderInterface.MaxOutputPerSubgroup:X8} " +
                $"maxVerticesOut=0x{shaderInterface.GeometryMaxVerticesOut:X8} " +
                $"outputPrimitive=0x{shaderInterface.GeometryOutputPrimitiveType:X8} " +
                $"export=0x{vertex.ExportAddress:X16} geometry=0x{vertex.GeometryAddress:X16} " +
                $"hull=0x{vertex.HullAddress:X16} local=0x{vertex.LocalAddress:X16} " +
                $"unsupportedMask={unsupportedStageMask} unsupportedGeometry={unsupportedGeometryStage} " +
                $"unsupportedRegisters={geometryRegisters}.");
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

        if (MeshDrawTrace.Active) MeshDrawTrace.Write("gate", $"accepted=false auto={autoDraw} supported={_pipelines.MeshShadersSupported}");
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
                topology = PrimitiveTopology.PatchList;
                break;
            case GuestPrimitiveType.RectangleListLegacy:
                if (!autoDraw)
                {
                    throw _host.Fatal($"The primitive type is unknown for an indexed draw: primitiveType={userConfig.PrimitiveType}.");
                }

                topology = _host.SupportsNativeRectangles ? PrimitiveTopology.PatchList : PrimitiveTopology.TriangleStrip;
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

    private static bool RequiresMeshExecution(uint stages) =>
        (stages & 0x20u) != 0 || (stages & 0x0200203Fu) == 0x2000u;

    private const uint TessellationStageBits = 0x7u;

    private bool UsesMeshExecution(RegisterBanks banks) =>
        _pipelines.MeshShadersSupported && RequiresMeshExecution(banks.Context.ShaderStages) &&
        banks.Shader.Vertex.ExportAddress != 0 &&
        (banks.Context.ShaderStages & TessellationStageBits) == 0;

    private void ValidateChangedDrawRegisters(RegisterBanks banks)
    {
        var resolution = _drawResolution;
        var contextStamp = banks.ContextStamp;
        var userConfigStamp = banks.Stamp(RegisterStamp.UserConfig);
        if (resolution.ValidatedContextStamp == contextStamp && resolution.ValidatedUserConfigStamp == userConfigStamp)
        {
            return;
        }

        ValidateDrawRegisters(banks);
        resolution.ValidatedContextStamp = contextStamp;
        resolution.ValidatedUserConfigStamp = userConfigStamp;
    }

    private void ResolveShaderPrograms(RegisterBanks banks, ref DrawState state, bool indexed = false)
    {
        if (state.Programs is not null)
        {
            return;
        }

        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawProgramResolution);
        Span<ColorComponentMapArray> mappingStorage = stackalloc ColorComponentMapArray[1];
        Span<ColorComponentMap> targetExportMapping = mappingStorage[0];
        targetExportMapping.Fill(ColorComponentMap.Identity);
        foreach (ref readonly var color in DrawState.BoundColors(ref state))
        {
            targetExportMapping[(int)color.Slot] = color.Resolution.ExportMapping;
        }

        if (UsesMeshExecution(banks))
        {
            var userConfig = banks.UserConfig;
            if (indexed && userConfig.PrimitiveType is
                (uint)GuestPrimitiveType.TriangleList or (uint)GuestPrimitiveType.TriangleStrip)
            {
                userConfig = userConfig.Copy();
                userConfig.PrimitiveType = (uint)GuestPrimitiveType.TriangleList;
            }

            state.Programs = _pipelines.GetMeshGraphicsPrograms(banks, userConfig, targetExportMapping, state.PixelActive, state.Depth.HasTarget);
            return;
        }

        state.Programs = _pipelines.GetGraphicsPrograms(banks, targetExportMapping, state.PixelActive, state.Depth.HasTarget, state.StartInstanceUserData);
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
        ref readonly var viewport = ref banks.Context.ScreenViewport.Primary;
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
}
