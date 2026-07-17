// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Metal;

// Guest draws and compute dispatches batch into one command buffer per drain
// instead of one per work item, mirroring the Vulkan presenter's batched guest
// commands: commit overhead dominated CPU time for scenes with dozens of draws
// per frame. Ordering inside the batch is by encoder sequence (snapshot blits
// for a draw's feedback reads are encoded before its render pass opens), and
// everything that must observe batched work on the serial queue — flips, image
// writes/blits, CPU-visible write-backs, the present pass — flushes first.
internal static partial class MetalVideoPresenter
{
    private static nint _batchCommandBuffer;
    private static bool _batchOpen;

    /// <summary>Returns the open batch command buffer, opening one on first
    /// use. Render thread only, like the drain it serves.</summary>
    private static nint BeginBatchedGuestCommands(nint queue)
    {
        if (_batchOpen)
        {
            return _batchCommandBuffer;
        }

        _batchCommandBuffer = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
        _batchOpen = _batchCommandBuffer != 0;
        return _batchCommandBuffer;
    }

    /// <summary>Commits the open batch (if any), tagging the upload pages and
    /// snapshot resources it consumed. Returns the committed command buffer so
    /// write-back sites can wait on it, or 0 when nothing was open.</summary>
    private static nint FlushBatchedGuestCommands()
    {
        if (!_batchOpen)
        {
            return 0;
        }

        _batchOpen = false;
        var commandBuffer = _batchCommandBuffer;
        _batchCommandBuffer = 0;
        MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
        TagUploadPages(commandBuffer);
        TagSnapshotResources(commandBuffer);
        return commandBuffer;
    }
}
