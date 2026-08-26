// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// This partial releases Vulkan host infrastructure.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private void DisposeVulkan()
        {
            if (!_vulkanReady)
            {
                return;
            }

            if (_debugUtils is not null && _debugMessenger.Handle != 0)
            {
                _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            }
            _vulkanReady = false;
            _vk.DeviceWaitIdle(_device);
            SavePipelineCache(force: true);
            DrainFrameSlots();
            CollectCompletedGuestSubmissions(waitForOldest: false);
            DestroyFeedbackSnapshotPool();
            ReportFeedbackSnapshotTelemetry(final: true);
            ClearCachedTextureIdentities();
            foreach (var pipeline in _computePipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _computePipelines.Clear();
            foreach (var pipeline in _graphicsPipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _graphicsPipelines.Clear();
            foreach (var layout in _descriptorLayouts.Values)
            {
                _vk.DestroyPipelineLayout(_device, layout.PipelineLayout, null);
                if (layout.DescriptorSetLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(
                        _device,
                        layout.DescriptorSetLayout,
                        null);
                }
            }
            _descriptorLayouts.Clear();
            while (_recycledDescriptorPools.TryPop(out var recycledDescriptorPool))
            {
                _vk.DestroyDescriptorPool(_device, recycledDescriptorPool, null);
            }
            foreach (var sampler in _samplers.Values)
            {
                _vk.DestroySampler(_device, sampler, null);
            }
            _samplers.Clear();
            _shaderDigests.Clear();
            WriteBackAllDirtyGuestBuffers();
            foreach (var allocation in _guestBufferAllocations)
            {
                DestroyGuestBufferAllocation(allocation);
            }
            _guestBufferAllocations.Clear();
            _dirtyGuestBufferIndex.Clear();
            _dirtyGuestBufferCandidates.Clear();
            PerfOverlay.SetGuestBufferCacheBytes(0);
            _hostBufferPool.Dispose();
            if (_useDedicatedComputeQueue)
            {
                Console.Error.WriteLine(
                    $"[LOADER][PERF] vk.dedicated_compute_queue " +
                    $"graphics_submits={_graphicsQueueSubmitCount} " +
                    $"compute_submits={_computeQueueSubmitCount}");
            }
            foreach (var guestImage in _guestImages.Values)
            {
                DestroyGuestImage(guestImage);
            }
            _guestImages.Clear();
            foreach (var guestImageVariant in _guestImageVariants.Values)
            {
                DestroyGuestImage(guestImageVariant);
            }
            _guestImageVariants.Clear();
            var deferredVariantsAtTeardown = _deferredGuestImageVariantDestroys.Count;
            var retainedVariantsAtTeardown = _retiredGuestImageVariants.Count;
            while (_deferredGuestImageVariantDestroys.TryDequeue(out var deferredVariant))
            {
                DestroyGuestImage(deferredVariant.Image);
            }
            foreach (var retiredGuestImageVariant in _retiredGuestImageVariants)
            {
                DestroyGuestImage(retiredGuestImageVariant);
            }
            _retiredGuestImageVariants.Clear();
            if (_guestImageVariantConflictCount != 0 ||
                _guestImageVariantSameResourceCount != 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] vk.guest_image_variant_summary " +
                    $"conflicts={_guestImageVariantConflictCount} " +
                    $"same_resource={_guestImageVariantSameResourceCount} " +
                    $"replacements={_guestImageVariantReplacementCount} " +
                    $"retired_during_runtime={_guestImageVariantDeferredDestroyCount} " +
                    $"deferred_at_teardown={deferredVariantsAtTeardown} " +
                    $"retained_at_teardown={retainedVariantsAtTeardown}");
            }
            foreach (var guestImageVersion in _guestImageVersions.Values)
            {
                DestroyGuestImage(guestImageVersion);
            }
            _guestImageVersions.Clear();
            while (_deferredGuestImageVersionDestroys.TryDequeue(out var deferredVersion))
            {
                DestroyGuestImage(deferredVersion.Image);
            }
            foreach (var guestDepth in _guestDepthImages.Values)
            {
                DestroyGuestDepth(guestDepth);
            }
            _guestDepthImages.Clear();
            lock (_gate)
            {
                _availableGuestImages.Clear();
                _cpuBackedUploadGenerations.Clear();
                _untrackedGuestImageContentProbes.Clear();
                _lastOrderedGuestFlipVersions.Clear();
            }
            DestroySwapchainResources();
            _detilePass?.Dispose();
            _detilePass = null;
            if (_device.Handle != 0)
            {
                Volatile.Write(ref _gpuLabelTimelineAvailable, false);
                if (_gpuLabelTimelineEnabled)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][PERF] vk.gpu_label_timeline " +
                        $"signals={_gpuLabelTimelineSignalCount} " +
                        $"cross_queue_waits={_gpuLabelTimelineCrossQueueWaitCount} " +
                        $"graphics_value={_graphicsGuestTimelineValue} " +
                        $"compute_value={_computeGuestTimelineValue}");
                }
                if (_graphicsGuestTimelineSemaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(
                        _device,
                        _graphicsGuestTimelineSemaphore,
                        null);
                    _graphicsGuestTimelineSemaphore = default;
                }
                if (_computeGuestTimelineSemaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(
                        _device,
                        _computeGuestTimelineSemaphore,
                        null);
                    _computeGuestTimelineSemaphore = default;
                }
                if (_pipelineCache.Handle != 0)
                {
                    _vk.DestroyPipelineCache(_device, _pipelineCache, null);
                    _pipelineCache = default;
                }
                _vk.DestroyDevice(_device, null);
                _device = default;
            }
            if (_surface.Handle != 0)
            {
                _surfaceApi.DestroySurface(_instance, _surface, null);
                _surface = default;
            }
            if (_instance.Handle != 0)
            {
                _vk.DestroyInstance(_instance, null);
                _instance = default;
            }
        }
    }
}
