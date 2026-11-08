// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed partial class RenderRecorder
{
    private void RecordTessellation(ResolvedDraw record, ref DrawState state)
    {
        var draw = record.Draw;
        var count = draw.Count;
        var instances = draw.InstanceCount;
        var firstInstance = draw.FirstInstance;
        var indexAddress = record.TessellationIndexAddress;
        var indexSize = record.TessellationIndexSize;
        var vertexOffset = record.TessellationVertexOffset;
        var tessellation = state.Programs.Tessellation ?? throw _host.Fatal("The draw has no prepared tessellation programs.");
        var hull = tessellation.Hull;
        var input = hull.Input;
        var layout = tessellation.HullConfiguration;
        var patches = count / layout.InputControlPoints;
        if (patches == 0) { _host.ResetBindings(); return; }
        var factorIndex = input.Stage.Program!.TessellationFactorBuffer;
        if (factorIndex < 0 || factorIndex >= input.Stage.Resources.Buffers.Length)
            throw _host.Fatal("The tessellation factor buffer is missing from the hull resources.");
        var descriptor = BufferDescriptorWords.From(input.Stage.Resources.Buffers[factorIndex]);
        var factorSize = descriptor.Footprint() ?? throw _host.Fatal("The tessellation factor-ring footprint overflows.");
        var patchFactorBytes = Gen5TessellationBridge.FactorCount(tessellation.Configuration.Domain) * 4;
        var groupFactorBytes = layout.PatchesPerGroup * patchFactorBytes;
        var factorBytes = Math.Min(patches, layout.PatchesPerGroup) * patchFactorBytes;
        if (descriptor.Address == 0 || factorSize < factorBytes || descriptor.SwizzleEnabled || descriptor.AddThreadId)
            throw _host.Fatal("The tessellation factor ring has an invalid address, extent or addressing mode.");
        // Groups that run together take their own off-chip buffer and their own run of the
        // factor ring. Without the guest's off-chip configuration, one group runs at a time.
        var (offchipBuffers, offchipSlotBytes) = Gpu.TessellationOffchip.Configuration;
        var (ringAddress, ringBytes) = (0ul, 0ul);
        var localDataSlotBytes = 0u;
        if (input.Stage.Program!.UsesTessellationLocalDataRing)
        {
            (ringAddress, ringBytes) = input.Stage.Program.Frontend?.TessellationLocalDataRing ?? default;
            localDataSlotBytes = System.Numerics.BitOperations.RoundUpToPowerOf2(Math.Max(input.LocalDataShareDwords, 1u)) * sizeof(uint);
            if (ringAddress == 0 || ringBytes < localDataSlotBytes)
                throw _host.Fatal("The tessellation local data ring is unavailable or smaller than one hull group.");
            offchipBuffers = (uint)Math.Min(ringBytes / localDataSlotBytes, uint.MaxValue);
        }
        var groupsPerBatch = offchipBuffers == 0 ? 1u : (uint)Math.Clamp(Math.Min(offchipBuffers, factorSize / groupFactorBytes), 1, uint.MaxValue);
        var batchCapacity = groupsPerBatch * layout.PatchesPerGroup;
        var originalHullStage = input.Stage;
        var originalDomainStage = state.Programs.VertexInput.Stage;
        var pipeline = _pipelines.CreateComputePipeline(input, hull.Program);
        // Patches are numbered across all instances, so instances share batches; each shader
        // thread splits its patch number into an instance and a patch within it.
        var totalPatches = (ulong)instances * patches;
        if (totalPatches > uint.MaxValue)
            throw _host.Fatal("The tessellated draw has more patches than one batch numbering covers.");
        if (RenderPhaseProfile.Enabled)
        {
            TessellationProfile.RecordDraw(instances, patches, (long)((totalPatches + batchCapacity - 1) / batchCapacity), groupsPerBatch == 1);
        }

        Span<GuestSpan> ranges = stackalloc GuestSpan[3];
        try
        {
            for (uint firstPatch = 0; firstPatch < (uint)totalPatches;)
            {
                var batchPatches = Math.Min(batchCapacity, (uint)totalPatches - firstPatch);
                var groups = (batchPatches + layout.PatchesPerGroup - 1) / layout.PatchesPerGroup;
                var data = new uint[Gen5TessellationData.DwordCount];
                data[Gen5TessellationData.FirstPatch] = firstPatch;
                data[Gen5TessellationData.PatchCount] = batchPatches;
                data[Gen5TessellationData.VertexOffset] = unchecked((uint)vertexOffset);
                data[Gen5TessellationData.InstanceId] = firstInstance;
                data[Gen5TessellationData.PatchesPerInstance] = patches;
                data[Gen5TessellationData.IndexSize] = indexSize;
                data[Gen5TessellationData.FactorBytes] = batchPatches * patchFactorBytes;
                data[Gen5TessellationData.GroupPatches] = offchipBuffers == 0 ? 0 : layout.PatchesPerGroup;
                data[Gen5TessellationData.OffchipSlotBytes] = offchipSlotBytes;
                data[Gen5TessellationData.FactorGroupBytes] = groupFactorBytes;
                data[Gen5TessellationData.MinimumLevel] = record.Context.ShaderInterface.MinTessellationLevel;
                data[Gen5TessellationData.MaximumLevel] = record.Context.ShaderInterface.MaxTessellationLevel;
                data[Gen5TessellationData.LocalDataSlotBytes] = localDataSlotBytes;
                using (_host.BeginPreparation())
                {
                    _host.EndRendering();
                    input.Stage = originalHullStage with { TessellationData = data };
                    var bindings = _host.PrepareBindings(input.Stage);
                    ranges[0] = new(descriptor.Address, factorSize);
                    var rangeCount = 1;
                    if (indexSize != 0)
                    {
                        if (indexAddress == 0) throw _host.Fatal("The tessellation indexed draw has no index buffer.");
                        ranges[rangeCount++] = new(indexAddress, (ulong)count * indexSize);
                    }
                    var localDataBytes = (ulong)groups * localDataSlotBytes;
                    if (localDataBytes != 0) ranges[rangeCount++] = new(ringAddress, localDataBytes);
                    _host.PrepareBufferAllocations(ranges[..rangeCount]);
                    if (input.Stage.Program!.UsesDeviceAddresses) _host.PrepareDeviceAddresses();
                    void Address(uint word, ulong address)
                    {
                        data[word] = (uint)address; data[word + 1] = (uint)(address >> 32);
                    }
                    Address(Gen5TessellationData.FactorAddress, _host.ObtainBufferDeviceAddress(descriptor.Address, factorSize, true));
                    if (indexSize != 0)
                    {
                        var bias = (uint)(indexAddress & 3);
                        var bytes = ((ulong)count * indexSize + bias + 3) & ~3ul;
                        Address(Gen5TessellationData.IndexAddress, _host.ObtainBufferDeviceAddress(indexAddress - bias, bytes, false));
                        data[Gen5TessellationData.IndexByteOffset] = bias;
                    }
                    if (localDataBytes != 0)
                        Address(Gen5TessellationData.LocalDataRingAddress, _host.ObtainBufferDeviceAddress(ringAddress, localDataBytes, true));
                    _host.UpdateTessellationData(bindings, data);
                    // Binding uploads shader data. Populate physical pointers
                    // before that upload, rather than only before descriptor commit.
                    _host.BindResources(bindings);
                    _host.CommitBindings(PipelineBindPoint.Compute, pipeline, [bindings]);
                    _host.BindPipeline(PipelineBindPoint.Compute, pipeline);
                    _host.Dispatch(groups, 1, 1);
                    _host.ShaderWriteBarrier(PipelineStageFlags.ComputeShaderBit);
                }
                state.Programs.VertexInput.Stage = originalDomainStage with { TessellationData = data };
                record.Draw = draw with { Count = batchPatches * layout.InputControlPoints, InstanceCount = 1 };
                RecordDrawCommands(record, ref state, inPlace: false);
                _host.EndRendering();
                // The next batch reuses relative patch IDs in the guest rings.
                _host.ShaderWriteHazardBarrier();
                firstPatch += batchPatches;
            }
        }
        finally
        {
            input.Stage = originalHullStage;
            state.Programs.VertexInput.Stage = originalDomainStage;
            _host.ResetBindings();
        }
    }
}
