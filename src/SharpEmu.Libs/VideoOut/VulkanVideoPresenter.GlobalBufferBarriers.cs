// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal struct GlobalBufferBarrierTracker
{
    private bool _shaderWritesPending;

    internal readonly bool ShouldRecordBarrier(bool elideRedundantBarriers) =>
        !elideRedundantBarriers || _shaderWritesPending;

    internal void BarrierRecorded(bool elideRedundantBarriers)
    {
        if (elideRedundantBarriers)
        {
            _shaderWritesPending = false;
        }
    }

    internal void MarkShaderWrites(bool writesGlobalMemory)
    {
        if (writesGlobalMemory)
        {
            _shaderWritesPending = true;
        }
    }
}

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly bool _elideRedundantGlobalBufferBarriers =
        !string.Equals(
            Environment.GetEnvironmentVariable(
                "SHARPEMU_ELIDE_REDUNDANT_GLOBAL_BUFFER_BARRIERS"),
            "0",
            StringComparison.Ordinal);

    private sealed partial class Presenter
    {
        private GlobalBufferBarrierTracker _globalBufferBarrierTracker;

        private bool ShouldRecordGlobalBufferVisibilityBarrier()
        {
            var recordBarrier = _globalBufferBarrierTracker.ShouldRecordBarrier(
                _elideRedundantGlobalBufferBarriers);
            if (!recordBarrier)
            {
                return false;
            }

            _globalBufferBarrierTracker.BarrierRecorded(
                _elideRedundantGlobalBufferBarriers);
            return true;
        }

        private void MarkGlobalBufferShaderWrites(
            TranslatedDrawResources resources,
            bool writesGlobalMemory = false)
        {
            if (!_elideRedundantGlobalBufferBarriers)
            {
                return;
            }

            var hasWritableDescriptor = resources.GlobalMemoryBuffers.Any(
                static buffer => buffer.Writable);
            _globalBufferBarrierTracker.MarkShaderWrites(
                writesGlobalMemory || hasWritableDescriptor);
        }
    }
}
