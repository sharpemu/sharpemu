// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Diagnostics;
using Silk.NET.Vulkan;

// This partial executes translated Vulkan compute dispatches.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private readonly HashSet<(ulong Shader, uint X, uint Y, uint Z, string Reason)>
            _rejectedComputeDispatches = new();
        private static string BuildComputeDebugName(VulkanComputeGuestDispatch dispatch)
        {
            var storage = dispatch.Textures.FirstOrDefault(texture => texture.IsStorage && texture.Address != 0);
            return storage is null
                ? $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}"
                : $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"storage=0x{storage.Address:X16} " +
                  $"{storage.Width}x{storage.Height} fmt{storage.Format} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}";
        }

        private const uint MaxComputeZSlicesPerSubmission = 8;
        // An indirect guest dispatch above this size is not credible frame work
        // (at the minimum 64-thread group used by the captured title this is
        // already over one billion invocations).  Treat it as poisoned
        // indirect-command data and quarantine it instead of feeding a host
        // API a multi-billion-workgroup command.  This is validation, not a
        // clamp: the raw dimensions remain visible in the trace so the
        // producer can be fixed without changing the guest value.
        private const ulong MaxCredibleGuestWorkgroupsPerDispatch = 16UL * 1024 * 1024;

        private void ExecuteComputeDispatch(VulkanComputeGuestDispatch work)
        {
            var perfStart = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();
            try
            {
                ExecuteComputeDispatchCore(work);
            }
            finally
            {
                Interlocked.Add(
                    ref _perfDrawTicks,
                    Stopwatch.GetTimestamp() - perfStart);
            }
        }

        private void ExecuteComputeDispatchCore(VulkanComputeGuestDispatch work)
        {
            FlushBatchedGuestCommands();
            if (_deviceLost)
            {
                ReturnPooledGuestData(work);
                return;
            }

            PumpHostMovieFrame();

            if (_skipAllCompute ||
                AddressListContains("SHARPEMU_SKIP_COMPUTE_CS", work.ShaderAddress) ||
                (_skipTallComputeZ > 0 && work.GroupCountZ >= _skipTallComputeZ))
            {
                TraceVulkanShader(
                    $"vk.compute_skip cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"textures={work.Textures.Count}");
                ReturnPooledGuestData(work);
                return;
            }

            if (!TryValidateComputeDispatch(work, out var validationError))
            {
                LogRejectedComputeDispatch(work, validationError);
                ReturnPooledGuestData(work);
                return;
            }

            if (!TryValidateStorageImageBindings(work, out validationError))
            {
                LogRejectedComputeDispatch(work, validationError);
                ReturnPooledGuestData(work);
                return;
            }

            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            var chunksSubmitted = 0;
            try
            {
                EnsureGuestSubmissionCapacity();
                resources = CreateComputeDispatchResources(work);

                FlushBatchedGuestCommands();

                var batchCount = Math.Max(
                    1u,
                    (uint)Math.Ceiling(work.GroupCountZ / (double)MaxComputeZSlicesPerSubmission));
                var threadLimits = stackalloc uint[3]
                {
                    work.ThreadCountX,
                    work.ThreadCountY,
                    work.ThreadCountZ,
                };

                for (var batchIndex = 0u; batchIndex < batchCount; batchIndex++)
                {
                    var zStart = batchIndex * MaxComputeZSlicesPerSubmission;
                    var zCount = Math.Min(MaxComputeZSlicesPerSubmission, work.GroupCountZ - zStart);
                    var isFirstBatch = batchIndex == 0;
                    var isLastBatch = batchIndex == batchCount - 1;

                    if (!isFirstBatch)
                    {
                        // Each chunk is its own queue submission; without
                        // this the in-flight submission cap only applies to
                        // the first chunk of a tall dispatch.
                        EnsureGuestSubmissionCapacity();
                    }

                    commandBuffer = AllocateGuestCommandBuffer();
                    _commandBuffer = commandBuffer;
                    var beginInfo = new CommandBufferBeginInfo
                    {
                        SType = StructureType.CommandBufferBeginInfo,
                        Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                    };
                    Check(
                        _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                        "vkBeginCommandBuffer(compute)");

                    BeginDebugLabel(_commandBuffer, resources.DebugName);
                    if (isFirstBatch)
                    {
                        RecordGlobalBufferVisibilityBarrier(
                            _commandBuffer,
                            resources,
                            PipelineStageFlags.ComputeShaderBit);
                        RecordTextureUploads(resources, PipelineStageFlags.ComputeShaderBit);
                        RecordStorageImagesForWrite(resources, PipelineStageFlags.ComputeShaderBit);
                    }
                    else
                    {
                        // Chunks are submitted without CPU waits; this
                        // barrier orders them against the previous chunk's
                        // shader writes on the same queue.
                        var chunkBarrier = new MemoryBarrier
                        {
                            SType = StructureType.MemoryBarrier,
                            SrcAccessMask = AccessFlags.ShaderWriteBit,
                            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                        };
                        _vk.CmdPipelineBarrier(
                            _commandBuffer,
                            PipelineStageFlags.ComputeShaderBit,
                            PipelineStageFlags.ComputeShaderBit,
                            0,
                            1,
                            &chunkBarrier,
                            0,
                            null,
                            0,
                            null);
                    }

                    _vk.CmdBindPipeline(
                        _commandBuffer,
                        PipelineBindPoint.Compute,
                        resources.Pipeline);
                    if (resources.DescriptorSet.Handle != 0)
                    {
                        var descriptorSet = resources.DescriptorSet;
                        _vk.CmdBindDescriptorSets(
                            _commandBuffer,
                            PipelineBindPoint.Compute,
                            resources.PipelineLayout,
                            0,
                            1,
                            &descriptorSet,
                            0,
                            null);
                    }

                    _vk.CmdPushConstants(
                        _commandBuffer,
                        resources.PipelineLayout,
                        ShaderStageFlags.ComputeBit,
                        0,
                        3 * sizeof(uint),
                        threadLimits);

                    RecordChunkedComputeDispatch(_commandBuffer, work, zStart, zCount);
                    MarkGlobalBufferShaderWrites(
                        resources,
                        work.WritesGlobalMemory);

                    if (isLastBatch)
                    {
                        RecordStorageImagesForRead(resources, PipelineStageFlags.ComputeShaderBit);
                    }

                    EndDebugLabel(_commandBuffer);
                    Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer(compute)");

                    TraceVulkanShader(
                        $"vk.compute_submit cs=0x{work.ShaderAddress:X16} " +
                        $"batch={batchIndex}/{batchCount} z={zStart}..{zStart + zCount}");
                    if (isLastBatch)
                    {
                        SubmitGuestCommandBuffer(
                            commandBuffer,
                            [resources],
                            GetTraceImages(resources, shaderAddress: work.ShaderAddress),
                            useComputeQueue: true);
                        submitted = true;
                    }
                    else
                    {
                        SubmitGuestCommandBuffer(
                            commandBuffer,
                            [],
                            [],
                            referencedResources: [resources],
                            useComputeQueue: true);
                        chunksSubmitted++;
                        commandBuffer = default;
                    }
                }

                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);
                TraceVulkanShader(
                    $"vk.compute_dispatch groups={work.GroupCountX}x" +
                    $"{work.GroupCountY}x{work.GroupCountZ} " +
                    $"base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ} " +
                    $"textures={work.Textures.Count} cs=0x{work.ShaderAddress:X16} " +
                    $"batches={batchCount}");
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan device lost during compute " +
                        $"cs=0x{work.ShaderAddress:X16} " +
                        $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                        $"textures={work.Textures.Count} " +
                        $"globals={work.GlobalMemoryBuffers.Count} " +
                        $"writes_global={(work.WritesGlobalMemory ? 1 : 0)} " +
                        $"indirect={(work.IsIndirect ? 1 : 0)} " +
                        $"spirv={work.ComputeSpirv.Length}");
                    return;
                }

                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan compute dispatch failed " +
                    $"cs=0x{work.ShaderAddress:X16}: {exception.Message}");
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                if (!submitted && commandBuffer.Handle != 0)
                {
                    _vk.FreeCommandBuffers(
                        _device,
                        _commandPool,
                        1,
                        &commandBuffer);
                }

                if (!submitted && resources is not null)
                {
                    if (chunksSubmitted > 0)
                    {
                        // Earlier chunks were submitted with empty resource
                        // lists and may still execute against these
                        // pipelines/images; destroy only after every
                        // submission issued so far has completed.
                        _deferredResourceDestroys.Enqueue((resources, _submitTimeline));
                    }
                    else
                    {
                        DestroyTranslatedDrawResources(resources);
                    }
                }
            }
        }

        private bool TryValidateComputeDispatch(
            VulkanComputeGuestDispatch work,
            out string error)
        {
            if (work.LocalSizeX == 0 || work.LocalSizeY == 0 || work.LocalSizeZ == 0)
            {
                error = "zero-local-size";
                return false;
            }

            if (work.LocalSizeX > _maxComputeWorkGroupSizeX ||
                work.LocalSizeY > _maxComputeWorkGroupSizeY ||
                work.LocalSizeZ > _maxComputeWorkGroupSizeZ)
            {
                error =
                    $"local-size-exceeds-device({work.LocalSizeX}x{work.LocalSizeY}x{work.LocalSizeZ}>" +
                    $"{_maxComputeWorkGroupSizeX}x{_maxComputeWorkGroupSizeY}x{_maxComputeWorkGroupSizeZ})";
                return false;
            }

            var localInvocations =
                (ulong)work.LocalSizeX * work.LocalSizeY * work.LocalSizeZ;
            if (localInvocations > _maxComputeWorkGroupInvocations)
            {
                error =
                    $"local-invocations-exceed-device({localInvocations}>" +
                    $"{_maxComputeWorkGroupInvocations})";
                return false;
            }

            if ((ulong)work.BaseGroupX + work.GroupCountX > _maxComputeWorkGroupCountX ||
                (ulong)work.BaseGroupY + work.GroupCountY > _maxComputeWorkGroupCountY ||
                (ulong)work.BaseGroupZ + work.GroupCountZ > _maxComputeWorkGroupCountZ)
            {
                error =
                    $"group-range-exceeds-device(base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ}," +
                    $"count={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ}," +
                    $"limit={_maxComputeWorkGroupCountX}x{_maxComputeWorkGroupCountY}x" +
                    $"{_maxComputeWorkGroupCountZ})";
                return false;
            }

            if (work.IsIndirect)
            {
                ulong totalWorkgroups;
                try
                {
                    totalWorkgroups = checked(
                        (ulong)work.GroupCountX * work.GroupCountY * work.GroupCountZ);
                }
                catch (OverflowException)
                {
                    error = "indirect-workgroup-count-overflow";
                    return false;
                }

                if (totalWorkgroups > MaxCredibleGuestWorkgroupsPerDispatch)
                {
                    error =
                        $"poisoned-indirect-workgroup-count({totalWorkgroups}>" +
                        $"{MaxCredibleGuestWorkgroupsPerDispatch})";
                    return false;
                }
            }

            // Empty resource tables with non-trivial SPIR-V usually means the
            // SRT/EUD walk failed (scalar_pointer_fallback / srt=0). Binding
            // nothing while the module still declares descriptors is a common
            // device-loss trigger on the subsequent QueueSubmit.
            if (work.Textures.Count == 0 &&
                work.GlobalMemoryBuffers.Count == 0 &&
                work.ComputeSpirv.Length > 0)
            {
                error = "empty-resources";
                return false;
            }

            // Address-0 storage is host scratch for legitimate descriptors, but
            // after an empty SRT walk every binding can collapse to Address-0
            // fallbacks with no real globals — that path has lost the device
            // on Astro Bot right after the first presented frame.
            var hasUsableStorage = false;
            for (var i = 0; i < work.Textures.Count; i++)
            {
                var texture = work.Textures[i];
                if (texture.IsStorage && texture.Address != 0)
                {
                    hasUsableStorage = true;
                    break;
                }
            }

            var hasUsableGlobal = false;
            for (var i = 0; i < work.GlobalMemoryBuffers.Count; i++)
            {
                if (work.GlobalMemoryBuffers[i].BaseAddress != 0)
                {
                    hasUsableGlobal = true;
                    break;
                }
            }

            if (!hasUsableStorage && !hasUsableGlobal)
            {
                error = "no-usable-resources";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private bool TryValidateStorageImageBindings(
            VulkanComputeGuestDispatch work,
            out string error)
        {
            var storageTextures = work.Textures
                .Where(static texture => texture.IsStorage)
                .ToArray();
            if (storageTextures.Length == 0)
            {
                error = string.Empty;
                return true;
            }

            if (!TryReadSpirvStorageImageContracts(
                    work.ComputeSpirv,
                    out var shaderContracts,
                    out error))
            {
                error = $"storage-contract-parse-failed({error})";
                return false;
            }

            if (shaderContracts.Length != storageTextures.Length)
            {
                error = $"storage-binding-count-mismatch(spirv={shaderContracts.Length}," +
                    $"guest={storageTextures.Length})";
                return false;
            }

            for (var index = 0; index < storageTextures.Length; index++)
            {
                var texture = storageTextures[index];
                var shaderContract = shaderContracts[index];
                var vulkanFormat = GetStorageImageFormat(
                    GetTextureFormat(texture.Format, texture.NumberType));
                if (!TryValidateStorageImageContract(
                        shaderContract,
                        texture.Format,
                        texture.NumberType,
                        texture.Type,
                        SupportsStorageImage(vulkanFormat),
                        out _,
                        out var bindingError))
                {
                    error = $"storage-binding[{index}]-invalid(" +
                        $"addr=0x{texture.Address:X16},reason={bindingError})";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private void LogRejectedComputeDispatch(
            VulkanComputeGuestDispatch work,
            string reason)
        {
            if (_rejectedComputeDispatches.Count >= 256 ||
                !_rejectedComputeDispatches.Add(
                    (work.ShaderAddress,
                     work.GroupCountX,
                     work.GroupCountY,
                     work.GroupCountZ,
                     reason)))
            {
                return;
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] vk.compute_reject cs=0x{work.ShaderAddress:X16} " +
                $"source={(work.IsIndirect ? "indirect" : "direct")} " +
                $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                $"base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ} " +
                $"local={work.LocalSizeX}x{work.LocalSizeY}x{work.LocalSizeZ} " +
                $"reason={reason}");
        }

        private void RecordGlobalBufferVisibilityBarrier(
            CommandBuffer commandBuffer,
            TranslatedDrawResources resources,
            PipelineStageFlags destinationStages)
        {
            if (resources.GlobalMemoryBuffers.Length == 0)
            {
                return;
            }
            if (!ShouldRecordGlobalBufferVisibilityBarrier())
            {
                return;
            }

            // Queue submission order alone is not a shader-memory dependency.
            // This makes stores through any aliased guest view available to
            // later vertex/fragment/compute reads and writes on the same queue.
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                destinationStages,
                0,
                1,
                &barrier,
                0,
                null,
                0,
                null);
        }
        private void RecordChunkedComputeDispatch(
            CommandBuffer commandBuffer,
            VulkanComputeGuestDispatch work,
            uint zStart,
            uint zCount)
        {
            const uint maxWorkgroupsPerCommand = 4096;
            ulong commandCount = 0;
            var maxXChunk = Math.Max(
                1u,
                Math.Min(
                    work.GroupCountX,
                    Math.Min(_maxComputeWorkGroupCountX, maxWorkgroupsPerCommand)));
            for (var x = 0u; x < work.GroupCountX;)
            {
                var countX = Math.Min(maxXChunk, work.GroupCountX - x);
                var xyBudget = Math.Max(maxWorkgroupsPerCommand / countX, 1u);
                var maxYChunk = Math.Max(
                    1u,
                    Math.Min(
                        work.GroupCountY,
                        Math.Min(_maxComputeWorkGroupCountY, xyBudget)));
                for (var y = 0u; y < work.GroupCountY;)
                {
                    var countY = Math.Min(maxYChunk, work.GroupCountY - y);
                    var xyzBudget = Math.Max(xyBudget / countY, 1u);
                    var maxZChunk = Math.Max(
                        1u,
                        Math.Min(
                            zCount,
                            Math.Min(_maxComputeWorkGroupCountZ, xyzBudget)));
                    for (var z = 0u; z < zCount;)
                    {
                        var countZ = Math.Min(maxZChunk, zCount - z);
                        _vk.CmdDispatchBase(
                            commandBuffer,
                            checked(work.BaseGroupX + x),
                            checked(work.BaseGroupY + y),
                            checked(work.BaseGroupZ + zStart + z),
                            countX,
                            countY,
                            countZ);
                        commandCount++;
                        z += countZ;
                    }

                    y += countY;
                }

                x += countX;
            }

            if (commandCount > 1)
            {
                TraceVulkanShader(
                    $"vk.compute_chunked cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ} " +
                    $"z_range={zStart}..{zStart + zCount} commands={commandCount} " +
                    $"command_budget={maxWorkgroupsPerCommand} " +
                    $"device_limit={_maxComputeWorkGroupCountX}x" +
                    $"{_maxComputeWorkGroupCountY}x{_maxComputeWorkGroupCountZ}");
            }
        }
    }
}
