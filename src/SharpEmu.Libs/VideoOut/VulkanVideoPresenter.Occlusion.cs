// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        public bool TryReadOcclusionCounter(int queueId, out ulong value)
        {
            value = 0;
            if (!_supportsPreciseOcclusion) return false;

            // Complete earlier GPU writes before the interpreter publishes the ready bit.
            FlushAndWait();
            _occlusionQueries ??= new VulkanOcclusionQueries(_vk, _device);
            value = _occlusionQueries.Read(queueId);
            return true;
        }

        private void SetOcclusionCounting()
        {
            const uint incrementDisabled = 1u;
            const uint counterZeroEnabled = 1u << 8;
            var control = _commandStream.GetInterpreter(_occlusionQueueId).TypedRegisters.Context.DepthCountControl;
            var enabled = (control & (incrementDisabled | counterZeroEnabled)) == counterZeroEnabled;
            if (enabled != _occlusionCounting)
                EndRendering();
            if (enabled && _supportsPreciseOcclusion)
                _occlusionQueries ??= new VulkanOcclusionQueries(_vk, _device);
            _occlusionCounting = enabled;
        }
    }
}
