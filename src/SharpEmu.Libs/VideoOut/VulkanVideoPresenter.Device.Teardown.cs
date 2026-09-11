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
            DestroyRenderPipelines();
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
            PerfOverlay.SetGuestCacheStatistics(0, 0, _deviceInfo.LiveAllocations, _deviceInfo.PeakAllocations);
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
            DestroySwapchainResources();
            Console.Error.WriteLine(
                $"[LOADER][INFO] vk.device_memory live_allocations={_deviceInfo.LiveAllocations} " +
                $"peak_allocations={_deviceInfo.PeakAllocations} limit={_deviceInfo.MaxMemoryAllocationCount}");
            if (_device.Handle != 0)
            {
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
