// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Metal;

// Per-draw upload data (guest global buffers, uniforms, vertex and index
// bytes) bump-allocates from shared-storage arena pages bound by offset,
// instead of creating one MTLBuffer and one managed copy per binding per
// draw — which dominated allocation churn (hundreds of MB/s) and held the
// guest flip rate well under the display rate. Pages recycle once the last
// command buffer that referenced them reports completion; everything here
// runs on the render thread, so no state is locked.
internal static partial class MetalVideoPresenter
{
    private const int UploadPageBytes = 8 * 1024 * 1024;

    // Superset of every Metal bind-offset alignment rule (constant address
    // space on Intel Macs is the strictest at 256), and conveniently the
    // guest storage-buffer alignment the shader bias contract assumes.
    private const int UploadAlignment = 256;

    private sealed class UploadPage
    {
        public nint Buffer;
        public nint Contents;
        public int Capacity;
        public int Offset;

        /// <summary>Retained handle of the last command buffer that consumed
        /// data from this page; the page is reusable once it completes.</summary>
        public nint LastCommandBuffer;

        /// <summary>Stamp of the last TagUploadPages call that saw this page,
        /// so a commit only re-tags pages it actually touched.</summary>
        public int TouchStamp;
    }

    private static readonly List<UploadPage> _retiredUploadPages = [];
    private static readonly Stack<UploadPage> _freeUploadPages = new();
    private static readonly List<UploadPage> _touchedUploadPages = [];
    private static UploadPage? _currentUploadPage;
    private static int _uploadTouchStamp;

    /// <summary>Returns completed pages to the free stack. Called once per
    /// render-loop drain; completion is polled (command buffer status) rather
    /// than block-based so the ObjC interop stays block-free.</summary>
    private static void RecycleCompletedUploadPages()
    {
        for (var index = _retiredUploadPages.Count - 1; index >= 0; index--)
        {
            var page = _retiredUploadPages[index];
            if (page.LastCommandBuffer != 0)
            {
                // MTLCommandBufferStatus: Completed = 4, Error = 5.
                var status = MetalNative.Send(
                    page.LastCommandBuffer, MetalNative.Selector("status"));
                if (status < 4)
                {
                    continue;
                }

                MetalNative.SendVoid(page.LastCommandBuffer, MetalNative.Selector("release"));
                page.LastCommandBuffer = 0;
            }

            _retiredUploadPages.RemoveAt(index);
            if (page.Capacity == UploadPageBytes)
            {
                page.Offset = 0;
                _freeUploadPages.Push(page);
            }
            else
            {
                // Oversized one-off allocation; not worth pooling.
                MetalNative.SendVoid(page.Buffer, MetalNative.Selector("release"));
            }
        }
    }

    /// <summary>Bump-allocates an aligned slice for CPU-written upload data.
    /// The returned span is the slice's shared-storage memory; bind the
    /// buffer at the returned offset.</summary>
    private static unsafe Span<byte> AllocateUpload(
        nint device,
        int length,
        out nint buffer,
        out int offset)
    {
        var page = _currentUploadPage;
        var aligned = page is null
            ? 0
            : (page.Offset + UploadAlignment - 1) & ~(UploadAlignment - 1);
        if (page is null || aligned + length > page.Capacity)
        {
            if (page is not null)
            {
                _retiredUploadPages.Add(page);
            }

            page = AcquireUploadPage(device, length);
            _currentUploadPage = page;
            aligned = 0;
        }

        if (page.TouchStamp != _uploadTouchStamp)
        {
            page.TouchStamp = _uploadTouchStamp;
            _touchedUploadPages.Add(page);
        }

        buffer = page.Buffer;
        offset = aligned;
        page.Offset = aligned + length;
        return new Span<byte>((void*)(page.Contents + aligned), length);
    }

    private static UploadPage AcquireUploadPage(nint device, int minimumBytes)
    {
        if (minimumBytes <= UploadPageBytes && _freeUploadPages.Count > 0)
        {
            return _freeUploadPages.Pop();
        }

        var capacity = Math.Max(minimumBytes, UploadPageBytes);
        // Options 0 = MTLResourceStorageModeShared: CPU writes are coherent
        // and write-backs read the GPU's stores after waitUntilCompleted.
        var handle = MetalNative.SendNewBuffer(
            device, MetalNative.Selector("newBufferWithLength:options:"), (nuint)capacity, 0);
        return new UploadPage
        {
            Buffer = handle,
            Contents = MetalNative.Send(handle, MetalNative.Selector("contents")),
            Capacity = capacity,
        };
    }

    /// <summary>Marks every page touched since the previous tag as owing its
    /// lifetime to <paramref name="commandBuffer"/>. Called after each commit
    /// that consumed arena data.</summary>
    private static void TagUploadPages(nint commandBuffer)
    {
        if (_touchedUploadPages.Count == 0)
        {
            _uploadTouchStamp++;
            return;
        }

        foreach (var page in _touchedUploadPages)
        {
            if (page.LastCommandBuffer != 0)
            {
                MetalNative.SendVoid(page.LastCommandBuffer, MetalNative.Selector("release"));
            }

            page.LastCommandBuffer = MetalNative.Send(
                commandBuffer, MetalNative.Selector("retain"));
        }

        _touchedUploadPages.Clear();
        _uploadTouchStamp++;
    }
}
