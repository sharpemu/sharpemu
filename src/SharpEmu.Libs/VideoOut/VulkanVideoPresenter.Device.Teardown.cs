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

            ShutdownScheduler();
            if (_debugUtils is not null && _debugMessenger.Handle != 0)
            {
                _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            }
            _vulkanReady = false;
            lock (_queueGate)
            {
                _vk.DeviceWaitIdle(_device);
            }

            SavePipelineCache(force: true);
            DrainFrameSlots();
            CollectCompletedGuestSubmissions(waitForOldest: false);
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
            _shaderDigests.Clear();
            _imageCache.Dispose();
            _samplerStore.Dispose();
            _bufferCache.Dispose();
            PerfOverlay.SetGuestBufferCacheBytes(0);
            _hostBufferPool.Dispose();
            foreach (var guestImageVersion in _guestImageVersions.Values)
            {
                DestroyGuestImage(guestImageVersion);
            }
            _guestImageVersions.Clear();
            while (_deferredGuestImageVersionDestroys.TryDequeue(out var deferredVersion))
            {
                DestroyGuestImage(deferredVersion.Image);
            }
            lock (_gate)
            {
                _lastOrderedGuestFlipVersions.Clear();
            }
            DestroySwapchainResources();
            if (_device.Handle != 0)
            {
                Volatile.Write(ref _gpuLabelTimelineAvailable, false);
                if (_gpuLabelTimelineEnabled)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][PERF] vk.gpu_label_timeline " +
                        $"signals={_gpuLabelTimelineSignalCount} " +
                        $"waits={_gpuLabelTimelineWaitCount} " +
                        $"graphics_value={_graphicsGuestTimelineValue}");
                }
                if (_graphicsGuestTimelineSemaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(
                        _device,
                        _graphicsGuestTimelineSemaphore,
                        null);
                    _graphicsGuestTimelineSemaphore = default;
                }
                _scheduler.Dispose();
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
