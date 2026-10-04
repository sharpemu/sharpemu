// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
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
    private void AcquireVertexBuffers(VertexInputInfo vertexInput, Span<BufferBinding> prepared)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawVertexBufferAcquisition);
        var buffers = vertexInput.Buffers;
        if (buffers.Length > VertexInputInfo.MaxBuffers)
        {
            throw _host.Fatal($"The vertex input has too many buffers: count={buffers.Length} max={VertexInputInfo.MaxBuffers}.");
        }

        if (prepared.Length < buffers.Length)
        {
            throw _host.Fatal($"The vertex binding scratch size is invalid: scratch={prepared.Length} buffers={buffers.Length}.");
        }

        Span<VertexBufferRange> ranges = stackalloc VertexBufferRange[VertexInputInfo.MaxBuffers];
        Span<ulong> sizes = stackalloc ulong[VertexInputInfo.MaxBuffers];
        var rangeCount = 0;
        for (var slot = 0; slot < buffers.Length; slot++)
        {
            ref readonly var vertex = ref buffers[slot];
            var size = vertexInput.BufferSize(slot);
            sizes[slot] = size;
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

        BufferBinding? nullBuffer = null;
        for (var slot = 0; slot < buffers.Length; slot++)
        {
            ref readonly var vertex = ref buffers[slot];
            if (sizes[slot] == 0)
            {
                nullBuffer ??= _host.NullBuffer;
                prepared[slot] = nullBuffer.Value with { Size = 0 };
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
            prepared[slot] = new BufferBinding(
                owner.Binding.Handle,
                owner.Binding.Offset + vertex.Address - owner.BaseAddress,
                Math.Min(sizes[slot], owner.AcquiredEnd - vertex.Address));
        }

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

    // Binds everything the draw needs inside one preparation scope, then records it.
    private void RecordDraw(
        ulong submitId,
        RegisterBanks banks,
        in DrawCall draw,
        ref DrawState state,
        PrimitiveTopology topology,
        in DrawEmission emission,
        in IndexSource indexSource,
        bool primitiveRestart,
        bool setBindDebug,
        bool setAutoDebug)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawResourcePreparation);
        var context = banks.Context;
        var vertexInput = state.Programs.VertexInput;
        var pixelInput = state.Programs.PixelInput;
        var mesh = vertexInput.Mesh;
        var meshActive = mesh.IsActive;
        IMeshRenderHost? meshHost = null;
        var meshGroups = 0u;
        if (meshActive)
        {
            if (primitiveRestart)
            {
                throw _host.Fatal(
                    $"Primitive restart is not supported for a mesh draw: primitiveType={banks.UserConfig.PrimitiveType} indexed={emission.Indexed}.");
            }

            meshHost = _host as IMeshRenderHost;
            var capabilities = meshHost?.MeshShaderCapabilities ?? default;
            if (meshHost is null || !capabilities.Supported)
            {
                throw _host.Fatal("The draw needs VK_EXT_mesh_shader, but the render host does not support it.");
            }

            if (mesh.PrimitivesPerGroup == 0)
            {
                throw _host.Fatal("A mesh draw has no input primitives per workgroup.");
            }

            var primitiveCount = mesh.InputPrimitiveCount(draw.Count);
            if (primitiveCount == 0)
            {
                TraceLegacyFusedDisposition(banks, draw.Name, draw.Count, draw.InstanceCount, "zero-mesh-primitives");
                return;
            }

            meshGroups = ((primitiveCount - 1u) / mesh.PrimitivesPerGroup) + 1u;
            if (meshGroups > capabilities.MaxWorkGroupCountX ||
                draw.InstanceCount > capabilities.MaxWorkGroupCountY ||
                (ulong)meshGroups * draw.InstanceCount > capabilities.MaxWorkGroupTotalCount)
            {
                throw _host.Fatal(
                    $"The mesh draw exceeds host workgroup limits: groups={meshGroups} instances={draw.InstanceCount} " +
                    $"maxX={capabilities.MaxWorkGroupCountX} maxY={capabilities.MaxWorkGroupCountY} total={capabilities.MaxWorkGroupTotalCount}.");
            }
        }

        using var preparation = _host.BeginPreparation();
        if (meshActive && emission.Indexed)
        {
            if (indexSource.Address == 0 || indexSource.GuestElementSize == 0)
            {
                throw _host.Fatal(
                    $"An indexed mesh draw has an invalid source: address=0x{indexSource.Address:X16} elementSize={indexSource.GuestElementSize}.");
            }

            var indexBytes = checked((ulong)draw.Count * indexSource.GuestElementSize);
            _host.RegisterDeviceAddressRange(indexSource.Address, indexBytes);
        }

        IPreparedBindings vertexBindings;
        IPreparedBindings? pixelBindings;
        try
        {
            vertexBindings = _host.PrepareBindings(vertexInput.Stage);
            pixelBindings = state.PixelActive ? _host.PrepareBindings(pixelInput.Stage) : null;
        }
        catch (DrawImageTypeMismatchException rejection)
        {
            if (_strictDrawResources)
            {
                throw _host.Fatal(rejection.Message);
            }

            if (_reportedDrawImageTypeMismatches.Add(rejection.WarningKey))
            {
                Console.Error.WriteLine($"[GPU][WARN][DRAW_SKIPPED] {rejection.Message} " +
                    "The draw was not executed. Images and FPS can be incorrect. Set SHARPEMU_STRICT_COMPUTE=1 to stop on this failure.");
            }

            TraceLegacyFusedDisposition(
                banks,
                draw.Name,
                draw.Count,
                draw.InstanceCount,
                "resource-binding-rejected",
                $"message={rejection.Message}");
            return;
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

        Span<BufferBinding> vertexBuffers = stackalloc BufferBinding[VertexInputInfo.MaxBuffers];
        var vertexBufferCount = meshActive ? 0 : vertexInput.Buffers.Length;
        if (!meshActive)
        {
            AcquireVertexBuffers(vertexInput, vertexBuffers);
        }
        var indexBuffer = meshActive ? default : AcquireIndexBuffer(in indexSource);
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
        if (meshActive && ExperimentalLegacyFusedGeometryCompileOnly)
        {
            TraceLegacyFusedDisposition(
                banks,
                draw.Name,
                draw.Count,
                draw.InstanceCount,
                "compile-only",
                $"meshGroups={meshGroups}");
            return;
        }

        if (setBindDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x100);
        }

        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x200);
        }

        if (!meshActive)
        {
            _host.BindVertexBuffers(vertexBuffers[..vertexBufferCount], vertexInput);
        }

        if (pixelBindings is not null && setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x300);
        }

        Span<IPreparedBindings> stages = pixelBindings is null ? [vertexBindings] : [vertexBindings, pixelBindings];
        _host.CommitBindings(PipelineBindPoint.Graphics, in pipeline, stages);
        if (!meshActive && indexBuffer.Size != 0)
        {
            _host.BindIndexBuffer(indexBuffer.Binding, indexBuffer.Type);
        }

        if (meshActive)
        {
            Span<uint> drawData = stackalloc uint[6]
            {
                draw.Count,
                emission.Indexed ? unchecked((uint)emission.VertexOffset) : emission.FirstVertex,
                emission.FirstInstance,
                emission.Indexed ? indexSource.GuestElementSize : 0u,
                emission.Indexed ? (uint)indexSource.Address : 0u,
                emission.Indexed ? (uint)(indexSource.Address >> 32) : 0u,
            };
            meshHost!.PushMeshDrawData(in pipeline, drawData);
        }

        Span<DynamicViewportState> viewports = stackalloc DynamicViewportState[ScreenViewportRegisters.ViewportCount];
        var dynamicState = BuildDynamicState(context, in state, viewports, out var viewportCount);
        _host.SetDynamicState(in dynamicState, viewports[..viewportCount]);
        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x400);
        }

        if (DrawWritesMemory(vertexInput.Stage) || (pixelBindings is not null && DrawWritesMemory(pixelInput.Stage)))
        {
            _host.PrepareMemoryWritingDraw();
        }

        _host.BeginRendering(in state.Rendering);
        _host.BindPipeline(PipelineBindPoint.Graphics, in pipeline);
        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x500);
        }

        if (meshActive)
        {
            TraceLegacyFusedDisposition(
                banks,
                draw.Name,
                draw.Count,
                draw.InstanceCount,
                "mesh-dispatch",
                $"meshGroups={meshGroups}");
            meshHost!.DrawMeshTasks(meshGroups, draw.InstanceCount, 1);
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
        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x600);
        }

        var writeStages = PipelineStageFlags.None;
        if (HasBufferWrites(vertexInput.Stage))
        {
            writeStages |= meshActive ? PipelineStageFlags.MeshShaderBitExt : PipelineStageFlags.VertexShaderBit;
        }

        if (state.PixelActive && HasBufferWrites(pixelInput.Stage))
        {
            writeStages |= PipelineStageFlags.FragmentShaderBit;
        }

        if (writeStages != PipelineStageFlags.None)
        {
            _host.EndRendering();
            _host.ShaderWriteBarrier(writeStages);
        }

        if (setAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x700);
        }
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
            case GuestPrimitiveType.RectangleListLegacy:
                if (emission.Indexed)
                {
                    _host.DrawIndexed(draw.Count, draw.InstanceCount, 0, emission.VertexOffset, emission.FirstInstance);
                }
                else
                {
                    _host.Draw(draw.Count, draw.InstanceCount, emission.FirstVertex, emission.FirstInstance);
                }
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
