// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

public sealed partial class RenderExecutor
{
    private const uint TransientIndexAlignment = 16;

    private struct VertexBufferRange
    {
        public ulong BaseAddress;
        public ulong RequestedEnd;
        public ulong AcquiredEnd;
        public BufferBinding Binding;

        public readonly ulong RequestedSize => RequestedEnd - BaseAddress;
    }

    private readonly record struct PreparedIndexBuffer(BufferBinding Binding, ulong Size, IndexType Type);

    // Merges the vertex ranges, obtains one host buffer per merged range and offsets every slot into it.
    private BufferBinding[] AcquireVertexBuffers(VertexInputInfo vertexInput)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawVertexBufferAcquisition);
        var buffers = vertexInput.Buffers;
        if (buffers.Length > VertexInputInfo.MaxBuffers)
        {
            throw _host.Fatal($"The vertex input has too many buffers: count={buffers.Length} max={VertexInputInfo.MaxBuffers}.");
        }

        Span<VertexBufferRange> ranges = stackalloc VertexBufferRange[VertexInputInfo.MaxBuffers];
        Span<ulong> sizes = stackalloc ulong[buffers.Length];
        for (var slot = 0; slot < buffers.Length; slot++) sizes[slot] = buffers[slot].Size;
        foreach (var attribute in vertexInput.Attributes)
        {
            var slot = attribute.BufferIndex;
            var descriptor = attribute.Descriptor;
            if (buffers[slot].Stride != 0 || descriptor.RecordCount == 0 || descriptor.OutOfBounds != 2) continue;
            // This bounds mode tests only whether the record count is nonzero.
            // Constant attributes still need the full format extent.
            sizes[slot] = Math.Max(sizes[slot], (ulong)attribute.OffsetBytes + VertexAttributeFormats.StorageByteSize(descriptor));
        }
        var rangeCount = 0;
        for (var slot = 0; slot < buffers.Length; slot++)
        {
            ref readonly var vertex = ref buffers[slot];
            var size = sizes[slot];
            if (size == 0)
            {
                continue;
            }

            if (vertex.Address == 0 || size > ulong.MaxValue - vertex.Address)
            {
                throw _host.Fatal($"The vertex buffer range is invalid: address=0x{vertex.Address:X16} size=0x{size:X16}.");
            }

            ranges[rangeCount++] = new VertexBufferRange { BaseAddress = vertex.Address, RequestedEnd = vertex.Address + size };
        }

        ranges[..rangeCount].Sort(static (left, right) => left.BaseAddress.CompareTo(right.BaseAddress));
        Span<VertexBufferRange> merged = stackalloc VertexBufferRange[VertexInputInfo.MaxBuffers];
        var mergedCount = 0;
        for (var i = 0; i < rangeCount; i++)
        {
            ref readonly var range = ref ranges[i];
            if (mergedCount != 0 && merged[mergedCount - 1].RequestedEnd >= range.BaseAddress)
            {
                merged[mergedCount - 1].RequestedEnd = Math.Max(merged[mergedCount - 1].RequestedEnd, range.RequestedEnd);
                continue;
            }

            merged[mergedCount++] = new VertexBufferRange { BaseAddress = range.BaseAddress, RequestedEnd = range.RequestedEnd };
        }

        for (var i = 0; i < mergedCount; i++)
        {
            ref var range = ref merged[i];
            var size = _host.ClampMappedSize(range.BaseAddress, range.RequestedSize);
            range.AcquiredEnd = range.BaseAddress + size;
            range.Binding = _host.ObtainBuffer(range.BaseAddress, size, isWritten: false);
        }

        var prepared = new BufferBinding[buffers.Length];
        BufferBinding? nullBuffer = null;
        for (var slot = 0; slot < buffers.Length; slot++)
        {
            ref readonly var vertex = ref buffers[slot];
            if (sizes[slot] == 0)
            {
                nullBuffer ??= _host.NullBuffer;
                prepared[slot] = nullBuffer.Value;
                continue;
            }

            var found = -1;
            for (var i = 0; i < mergedCount; i++)
            {
                if (vertex.Address >= merged[i].BaseAddress && vertex.Address < merged[i].AcquiredEnd)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                throw _host.Fatal($"The vertex buffer address is outside the acquired range: address=0x{vertex.Address:X16}.");
            }

            ref readonly var owner = ref merged[found];
            prepared[slot] = new BufferBinding(owner.Binding.Handle, owner.Binding.Offset + vertex.Address - owner.BaseAddress);
        }

        return prepared;
    }

    private PreparedIndexBuffer AcquireIndexBuffer(in IndexSource source)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawIndexBufferAcquisition);
        if (!source.Enabled)
        {
            return default;
        }

        if (source.Size == 0)
        {
            throw _host.Fatal($"The index buffer is empty: address=0x{source.Address:X16} type={(int)source.Type}.");
        }

        var binding = source.HostData is { } hostData
            ? _host.UploadTransient(hostData, TransientIndexAlignment)
            : _host.ObtainBuffer(source.Address, source.Size, isWritten: false);
        return new PreparedIndexBuffer(binding, source.Size, source.Type);
    }

    // A written buffer resource with an address and a footprint needs a barrier after the stage.
    // Device-address accesses are not split into reads and writes, so they count as stores.
    private bool DrawWritesMemory(ShaderStageResources stage) =>
        stage.Program is { } program &&
        (program.UsesDeviceAddresses || WritesStorageImage(program) || HasBufferWrites(stage));

    private bool HasBufferWrites(ShaderStageResources stage)
    {
        var program = stage.Program ?? throw _host.Fatal("A shader stage has no program.");
        var descriptors = stage.Resources.Buffers;
        if (descriptors.Length != program.Buffers.Length)
        {
            throw _host.Fatal($"The buffer descriptor count does not match the program: descriptors={descriptors.Length} program={program.Buffers.Length}.");
        }

        for (var i = 0; i < program.Buffers.Length; i++)
        {
            if (!program.Buffers[i].Written)
            {
                continue;
            }

            if (descriptors[i].Length < 4)
            {
                throw _host.Fatal($"A written buffer descriptor is too short: index={i} words={descriptors[i].Length}.");
            }

            var descriptor = BufferDescriptorWords.From(descriptors[i]);
            var footprint = descriptor.Footprint() ?? throw _host.Fatal(
                $"The written buffer footprint overflows: index={i} stride={descriptor.Stride} records={descriptor.RecordCount}.");
            if (descriptor.Address != 0 && footprint != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void SetDrawDebugPhase(ulong submitId, in DrawCall draw, uint phase) =>
        _host.SetDebugInformation(draw.Operation, submitId, phase, draw.Count, 0, draw.InstanceCount, draw.FirstInstance);

    private IPreparedBindings PrepareBindings(in ShaderStageResources stage)
    {
        var bindings = _host.PrepareBindings(stage);
        NoteSampledTextures(stage);
        return bindings;
    }

    private static void NoteSampledTextures(in ShaderStageResources stage)
    {
        if (stage.Program is not { } program)
        {
            return;
        }

        var descriptors = stage.Resources.Images;
        for (var index = 0; index < program.Images.Length && index < descriptors.Length; index++)
        {
            if (program.Images[index].Class == ImageResourceClass.Sampled)
            {
                MipStatistics.Shared.NoteSampled(new TextureDescriptorWords(descriptors[index]));
            }
        }
    }

    // Binds everything the draw needs inside one preparation scope, then records it.
    private string RecordDraw(
        ulong submitId,
        RegisterBanks banks,
        in DrawCall draw,
        ref DrawState state,
        PrimitiveTopology topology,
        in DrawEmission emission,
        in IndexSource indexSource,
        bool primitiveRestart,
        bool setBindDebug,
        bool setAutoDebug,
        IndexedDrawTrace.Sample? indexedTrace = null,
        ulong packetAddress = 0)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawResourcePreparation);
        var context = banks.Context;
        var vertexInput = state.Programs.VertexInput;
        var pixelInput = state.Programs.PixelInput;
        var meshInput = state.Programs.MeshInput;
        using var preparation = _host.BeginPreparation();
        var meshVertexCount = draw.Count;
        if (MeshDrawTrace.Active)
            MeshDrawTrace.Write("programs", $"mesh={meshInput is not null} vertexHash=0x{vertexInput.Stage.Program?.Hash:X16} pixelHash=0x{pixelInput.Stage.Program?.Hash:X16} pixelActive={state.PixelActive}");
        if (meshInput is not null)
        {
            if (topology is not (PrimitiveTopology.PointList or PrimitiveTopology.TriangleList or PrimitiveTopology.TriangleStrip))
            {
                throw _host.Fatal("The mesh draw input topology is not supported.");
            }

            ulong indexAddress = 0;
            if (emission.Indexed)
            {
                var byteCount = checked((int)indexSource.Size);
                var elementSize = indexSource.Type == IndexType.Uint16 ? 2u : 4u;
                var triangleStrip = topology == PrimitiveTopology.TriangleStrip;
                var pointList = topology == PrimitiveTopology.PointList;
                var capacity = pointList ? byteCount / (int)elementSize
                    : MeshIndexAssembly.GetMaximumCount(byteCount, elementSize, triangleStrip);
                if (capacity == 0) return "no-primitives";
                byte[]? rentedBytes = null;
                uint[]? rentedIndices = null;
                try
                {
                    var bytes = indexSource.HostData is { } hostData ? hostData.AsSpan(0, byteCount)
                        : (rentedBytes = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
                    if (indexSource.HostData is null && !_host.TryReadGuest(indexSource.Address, bytes))
                        throw _host.Fatal("The mesh index buffer cannot be read.");
                    rentedIndices = ArrayPool<uint>.Shared.Rent(capacity);
                    var written = pointList
                        ? MeshIndexAssembly.ExpandPoints(bytes, rentedIndices.AsSpan(0, capacity), elementSize,
                            emission.VertexOffset, primitiveRestart, elementSize == 2 ? ushort.MaxValue : uint.MaxValue)
                        : MeshIndexAssembly.ExpandTriangles(bytes, rentedIndices.AsSpan(0, capacity),
                        elementSize, emission.VertexOffset, triangleStrip, primitiveRestart,
                        elementSize == 2 ? ushort.MaxValue : uint.MaxValue, context.RasterMode.ProvokingVertexLast);
                    meshVertexCount = checked((uint)written);
                    if (written == 0) return "no-primitives";
                    indexAddress = _host.UploadMeshVertexIndices(rentedIndices.AsSpan(0, written));
                }
                finally
                {
                    if (rentedIndices is not null) ArrayPool<uint>.Shared.Return(rentedIndices);
                    if (rentedBytes is not null) ArrayPool<byte>.Shared.Return(rentedBytes);
                }
            }
            vertexInput.Stage = vertexInput.Stage with
            {
                MeshDraw = new MeshDrawParameters(meshVertexCount, emission.Indexed ? 0 : emission.FirstVertex,
                    emission.FirstInstance, emission.Indexed ? 4u : 0u, indexAddress),
            };
        }
        IPreparedBindings vertexBindings;
        IPreparedBindings? pixelBindings;
        try
        {
            vertexBindings = PrepareBindings(vertexInput.Stage);
            pixelBindings = state.PixelActive ? PrepareBindings(pixelInput.Stage) : null;
        }
        catch (DrawImageTypeMismatchException rejection)
        {
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("resource-rejected", rejection.Message);
            IndexedDrawTrace.Write(indexedTrace, "resource-rejected", submitId, packetAddress);
            if (_strictDrawResources)
            {
                throw _host.Fatal(rejection.Message);
            }

            if (_reportedDrawImageTypeMismatches.Add(rejection.WarningKey))
            {
                Console.Error.WriteLine($"[GPU][WARN][DRAW_SKIPPED] {rejection.Message} " +
                    "The draw was not executed. Images and FPS can be incorrect. Set SHARPEMU_STRICT_COMPUTE=1 to stop on this failure.");
            }

            return "resource-rejected";
        }
        var vertexProgram = vertexInput.Stage.Program ?? throw _host.Fatal("The vertex stage has no program.");
        var pixelProgram = pixelBindings is null ? null : pixelInput.Stage.Program ?? throw _host.Fatal("The pixel stage has no program.");
        if (vertexProgram.UsesDeviceAddresses || (pixelProgram?.UsesDeviceAddresses ?? false))
        {
            _host.PrepareDeviceAddresses();
        }

        _host.BindResources(vertexBindings);
        if (pixelBindings is not null)
        {
            _host.BindResources(pixelBindings);
        }

        var vertexBuffers = meshInput is null ? AcquireVertexBuffers(vertexInput) : [];
        var indexBuffer = meshInput is null ? AcquireIndexBuffer(in indexSource) : default;
        var indirectArguments = emission.IndirectArgumentsAddress != 0
            ? _host.ObtainBuffer(emission.IndirectArgumentsAddress, IndexedIndirectArgumentsSize, isWritten: false)
            : default;
        DropUnwrittenColorTargets(context, ref state, pixelProgram);
        state.Rendering = AcquireAttachments(ref state);
        // Nothing after the pipeline touches guest memory.
        var pipeline = _pipelines.CreateGraphicsPipeline(
            BoundColors(ref state),
            in state.Depth,
            vertexInput,
            state.PixelActive ? pixelInput : null,
            context,
            in state.Rendering,
            topology,
            primitiveRestart,
            state.Programs.DisableBlending,
            state.Programs.Vertex,
            state.Programs.Pixel);
        if (setBindDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x100);
        }

        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x200);
        }

        if (meshInput is null)
        {
            _host.BindVertexBuffers(vertexBuffers, vertexInput);
        }

        if (pixelBindings is not null && setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x300);
        }

        Span<IPreparedBindings> stages = pixelBindings is null ? [vertexBindings] : [vertexBindings, pixelBindings];
        _host.CommitBindings(PipelineBindPoint.Graphics, in pipeline, stages);
        if (indexBuffer.Size != 0)
        {
            _host.BindIndexBuffer(indexBuffer.Binding, indexBuffer.Type);
        }

        _host.SetDynamicState(BuildDynamicState(context, in state));
        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x400);
        }

        if (DrawWritesMemory(vertexInput.Stage) || (pixelBindings is not null && DrawWritesMemory(pixelInput.Stage)))
        {
            _host.PrepareMemoryWritingDraw();
        }

        var imageSnapshot = _host.BeginImageSnapshot(pixelBindings);
        _host.BeginRendering(in state.Rendering);
        if (MeshDrawTrace.Active) MeshDrawTrace.Write("rendering", $"width={state.Rendering.Width} height={state.Rendering.Height} layers={state.Rendering.Layers} colors={state.ColorCount}");
        _host.BindPipeline(PipelineBindPoint.Graphics, in pipeline);
        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x500);
        }

        if (indexedTrace is { } trace)
            _host.InsertDrawTraceMarker(IndexedDrawTrace.CreateLabel(trace, submitId, packetAddress));

        if (meshInput is { } mesh)
        {
            var primitiveCount = mesh.Geometry.InputPointList ? meshVertexCount
                : mesh.Geometry.InputTriangleStrip ? (meshVertexCount >= 3 ? meshVertexCount - 2 : 0) : meshVertexCount / 3;
            var groupCount = (uint)(((ulong)primitiveCount + mesh.Geometry.InputPrimitiveCountPerWorkgroup - 1) / mesh.Geometry.InputPrimitiveCountPerWorkgroup);
            var totalGroupCount = (ulong)groupCount * draw.InstanceCount;
            if (groupCount > mesh.Execution.MaxGroupCountX || draw.InstanceCount > mesh.Execution.MaxGroupCountY ||
                totalGroupCount > mesh.Execution.MaxGroupTotalCount)
            {
                throw _host.Fatal($"The mesh draw exceeds device group limits: groups={groupCount}/{mesh.Execution.MaxGroupCountX} " +
                    $"instances={draw.InstanceCount}/{mesh.Execution.MaxGroupCountY} " +
                    $"total={totalGroupCount}/{mesh.Execution.MaxGroupTotalCount}.");
            }
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"MeshDraw shader=0x{banks.Shader.Vertex.ExportAddress:X16} " +
                    $"vertices={draw.Count} instances={draw.InstanceCount} groups={groupCount} " +
                    $"threads={mesh.Geometry.ThreadsPerGroup} ldsDwords={mesh.Geometry.LocalDataShareDwords}");
            }
            _host.DrawMeshTasks(groupCount, draw.InstanceCount, 1);
            if (MeshDrawTrace.Active)
            {
                MeshDrawTrace.Write("command-recorded", $"groups={groupCount}/{draw.InstanceCount}/1 threads={mesh.Geometry.ThreadsPerGroup} wave={mesh.Geometry.WaveSize} lds={mesh.Geometry.LocalDataShareDwords} firstVertex={emission.FirstVertex} firstInstance={emission.FirstInstance}");
                MeshDrawTrace.Recorded();
            }
        }
        else if (emission.IndirectArgumentsAddress != 0)
        {
            // Uploads and shader writes end with barriers to all commands, so the
            // indirect read sees them.
            _host.DrawIndexedIndirect(indirectArguments);
        }
        else
        {
            EmitDraw(banks.UserConfig, vertexInput, in draw, in emission);
        }
        _host.EndImageSnapshot(imageSnapshot, BoundColors(ref state));
        IndexedDrawTrace.Write(indexedTrace, "emitted", submitId, packetAddress);
        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x600);
        }

        var writeStages = PipelineStageFlags.None;
        if (HasBufferWrites(vertexInput.Stage))
        {
            writeStages |= meshInput is null ? PipelineStageFlags.VertexShaderBit : PipelineStageFlags.MeshShaderBitExt;
        }

        if (state.PixelActive && HasBufferWrites(pixelInput.Stage))
        {
            writeStages |= PipelineStageFlags.FragmentShaderBit;
        }

        if (writeStages != PipelineStageFlags.None)
        {
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("write-barrier", $"sourceStages={writeStages}");
            _host.EndRendering();
            _host.ShaderWriteBarrier(writeStages);
        }

        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x700);
        }

        return "recorded";
    }

    private const ulong IndexedIndirectArgumentsSize = 20;

    private void EmitDraw(UserConfigRegisters userConfig, VertexInputInfo vertexInput, in DrawCall draw, in DrawEmission emission)
    {
        switch ((GuestPrimitiveType)userConfig.PrimitiveType)
        {
            case GuestPrimitiveType.PointList:
            case GuestPrimitiveType.LineList:
            case GuestPrimitiveType.LineStrip:
            case GuestPrimitiveType.TriangleList:
            case GuestPrimitiveType.TriangleFan:
            case GuestPrimitiveType.TriangleStrip:
            case GuestPrimitiveType.Polygon:
            case GuestPrimitiveType.RectangleList:
                if (emission.Indexed)
                {
                    _host.DrawIndexed(draw.Count, draw.InstanceCount, 0, emission.VertexOffset, emission.FirstInstance);
                }
                else
                {
                    _host.Draw(draw.Count, draw.InstanceCount, emission.FirstVertex, emission.FirstInstance);
                }

                break;
            case GuestPrimitiveType.RectangleListLegacy:
                if (emission.Indexed)
                {
                    throw _host.Fatal($"The primitive type is unknown for an indexed draw: primitiveType={userConfig.PrimitiveType}.");
                }

                if (_host.SupportsNativeRectangles)
                {
                    // Keep guest vertex IDs unchanged; the host fills each three-vertex rectangle.
                    _host.Draw(draw.Count, draw.InstanceCount, emission.FirstVertex, emission.FirstInstance);
                    break;
                }

                if (draw.Count != 3 || vertexInput.Buffers.Length != 0)
                {
                    throw _host.Fatal($"A legacy rectangle list needs three vertices and no vertex buffers: count={draw.Count} buffers={vertexInput.Buffers.Length}.");
                }

                _host.Draw(4, draw.InstanceCount, emission.FirstVertex, emission.FirstInstance);
                break;
            case GuestPrimitiveType.QuadListLegacy:
                if ((draw.Count & 0x3) != 0)
                {
                    throw _host.Fatal($"A legacy quad list count is not a multiple of four: count={draw.Count}.");
                }

                for (var i = 0u; i < draw.Count; i += 4)
                {
                    if (emission.Indexed)
                    {
                        _host.DrawIndexed(4, draw.InstanceCount, i, emission.VertexOffset, emission.FirstInstance);
                    }
                    else
                    {
                        _host.Draw(4, draw.InstanceCount, i + emission.FirstVertex, emission.FirstInstance);
                    }
                }

                break;
            default:
                throw _host.Fatal($"The primitive type is unknown: primitiveType={userConfig.PrimitiveType}.");
        }
    }
}
