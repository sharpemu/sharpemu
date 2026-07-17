// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Metal;

// Feedback reads (draws sampling a live guest render target or depth image)
// need a fresh ordered snapshot per draw. Creating and destroying an MTLTexture
// — and for depth reads a private staging MTLBuffer — per draw is measurable
// CPU and allocator churn at hundreds of feedback draws per second, so both
// recycle through a pool with the same lifecycle as the upload arena pages:
// acquired snapshots are tagged with the command buffer that samples them at
// commit, and return to the free list once that command buffer completes (the
// command queue is serial, so the earlier snapshot-blit command buffer is
// necessarily complete by then too). Everything here runs on the render thread.
internal static partial class MetalVideoPresenter
{
    private const int MaxFreeSnapshotResources = 16;

    private sealed class PooledSnapshotResource
    {
        public nint Handle;
        public bool IsBuffer;

        /// <summary>Texture identity (unused for buffers).</summary>
        public uint Format;
        public uint Width;
        public uint Height;
        public nint Usage;

        /// <summary>Buffer capacity in bytes (unused for textures).</summary>
        public nuint Capacity;

        /// <summary>Retained handle of the command buffer that samples this
        /// snapshot; the resource is reusable once it completes.</summary>
        public nint LastCommandBuffer;
    }

    private static readonly List<PooledSnapshotResource> _retiredSnapshotResources = [];
    private static readonly List<PooledSnapshotResource> _pendingSnapshotResources = [];
    private static readonly List<PooledSnapshotResource> _freeSnapshotResources = [];

    /// <summary>Returns completed snapshot resources to the free list; called
    /// once per render-loop drain, next to the upload-page recycler.</summary>
    private static void RecycleCompletedSnapshotResources()
    {
        for (var index = _retiredSnapshotResources.Count - 1; index >= 0; index--)
        {
            var resource = _retiredSnapshotResources[index];
            if (resource.LastCommandBuffer != 0)
            {
                // MTLCommandBufferStatus: Completed = 4, Error = 5.
                var status = MetalNative.Send(
                    resource.LastCommandBuffer, MetalNative.Selector("status"));
                if (status < 4)
                {
                    continue;
                }

                MetalNative.SendVoid(resource.LastCommandBuffer, MetalNative.Selector("release"));
                resource.LastCommandBuffer = 0;
            }

            _retiredSnapshotResources.RemoveAt(index);
            if (_freeSnapshotResources.Count < MaxFreeSnapshotResources)
            {
                _freeSnapshotResources.Add(resource);
            }
            else
            {
                MetalNative.SendVoid(resource.Handle, MetalNative.Selector("release"));
            }
        }
    }

    /// <summary>Pops a pooled snapshot texture matching the exact identity, or
    /// creates one. The returned handle is owned by the pool — callers must not
    /// release it, and it must be tagged at the next commit.</summary>
    private static nint AcquireSnapshotTexture(
        nint device,
        MtlPixelFormat format,
        uint width,
        uint height,
        nint usage)
    {
        for (var index = 0; index < _freeSnapshotResources.Count; index++)
        {
            var candidate = _freeSnapshotResources[index];
            if (!candidate.IsBuffer &&
                candidate.Format == (uint)format &&
                candidate.Width == width &&
                candidate.Height == height &&
                candidate.Usage == usage)
            {
                _freeSnapshotResources.RemoveAt(index);
                _pendingSnapshotResources.Add(candidate);
                return candidate.Handle;
            }
        }

        var descriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)format,
            width,
            height,
            mipmapped: false);
        MetalNative.Send(descriptor, MetalNative.Selector("setUsage:"), usage);
        var handle = MetalNative.Send(
            device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
        if (handle == 0)
        {
            return 0;
        }

        _pendingSnapshotResources.Add(new PooledSnapshotResource
        {
            Handle = handle,
            Format = (uint)format,
            Width = width,
            Height = height,
            Usage = usage,
        });
        return handle;
    }

    /// <summary>Pops a pooled private-storage staging buffer of at least
    /// <paramref name="minimumBytes"/>, or creates one. Pool-owned like
    /// <see cref="AcquireSnapshotTexture"/>.</summary>
    private static nint AcquireSnapshotBuffer(nint device, nuint minimumBytes)
    {
        for (var index = 0; index < _freeSnapshotResources.Count; index++)
        {
            var candidate = _freeSnapshotResources[index];
            if (candidate.IsBuffer && candidate.Capacity >= minimumBytes)
            {
                _freeSnapshotResources.RemoveAt(index);
                _pendingSnapshotResources.Add(candidate);
                return candidate.Handle;
            }
        }

        // MTLResourceStorageModePrivate = 32: staging never touches the CPU.
        var handle = MetalNative.SendNewBuffer(
            device, MetalNative.Selector("newBufferWithLength:options:"), minimumBytes, 32);
        if (handle == 0)
        {
            return 0;
        }

        _pendingSnapshotResources.Add(new PooledSnapshotResource
        {
            Handle = handle,
            IsBuffer = true,
            Capacity = minimumBytes,
        });
        return handle;
    }

    /// <summary>Marks every snapshot resource acquired since the previous tag
    /// as owing its lifetime to <paramref name="commandBuffer"/>. Called at the
    /// same commit sites as <see cref="TagUploadPages"/>; a resource acquired
    /// for a draw that never committed is tagged by the next commit, which is
    /// conservative but safe.</summary>
    private static void TagSnapshotResources(nint commandBuffer)
    {
        if (_pendingSnapshotResources.Count == 0)
        {
            return;
        }

        foreach (var resource in _pendingSnapshotResources)
        {
            resource.LastCommandBuffer = MetalNative.Send(
                commandBuffer, MetalNative.Selector("retain"));
            _retiredSnapshotResources.Add(resource);
        }

        _pendingSnapshotResources.Clear();
    }
}
