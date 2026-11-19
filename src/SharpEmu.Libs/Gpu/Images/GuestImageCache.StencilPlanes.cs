// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Stencil planes are plain guest memory on the console, but a host depth image has one stencil aspect.
// A stencil proxy has DepthOwner = D only while D's stencil aspect holds that plane's bytes. When the aspect
// leaves a plane (another depth takes it, D names another plane, D is deleted), the proxy is released: the aspect's
// GPU-written bytes go to guest memory when they can be read back, and the proxy is detached either way.
public sealed partial class GuestImageCache
{
    private enum StencilProxyFate
    {
        // Backed proxies stay as ordinary images that reload from guest memory; unbacked ones are deleted.
        DetachIfBacked,
        // The caller reuses the record.
        Keep,
        Delete,
    }

    private readonly record struct StencilRelease(ResourceSlotIdentifier Proxy, GuestSpan Plane);

    // Buffers over the planes about to be released, by proxy, obtained before the lock was taken: obtaining one can
    // publish images back through this cache. A release without one detaches without a download.
    [ThreadStatic]
    private static Dictionary<ResourceSlotIdentifier, GpuBuffer>? _preparedStencilBuffers;

    // Depths whose eviction waits for their GPU-written planes to reach guest memory; flushed outside the lock.
    private readonly List<ResourceSlotIdentifier> _pendingStencilReleases = new();

    // The image that becomes the depth's proxy of the plane: the same range with the depth's extent.
    private ResourceSlotIdentifier FindStencilAssociation(CachedImage depth, GuestSpan stencil)
    {
        var association = ResourceSlotIdentifier.Invalid;
        foreach (var imageIdentifier in FindImagesInRange(stencil.Address, stencil.Size, pageOverlap: false))
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner != null && owner.Description.Data == stencil &&
                owner.Description.Extent.Equals(depth.Description.Extent))
            {
                association = imageIdentifier;
            }
        }

        return association;
    }

    // Whether releasing the proxy has aspect bytes to save: GPU-written since they were last published, still
    // newest (the same checks as a publish of the image), and readable from the owner.
    private bool NeedsStencilDownload(ResourceSlotIdentifier proxyIdentifier, CachedImage proxy)
    {
        return _slots.TryGet(proxy.DepthOwner) is { } owner && owner.Backing.Exists &&
            !owner.Description.Metadata.StencilCompressed && owner.Description.Samples == 1 && owner.Backing.Samples == 1 &&
            proxy.SafeToDownload && !proxy.RawWriteAfterGpuWrite && !HasNewerHandedOverImage(proxyIdentifier, proxy) &&
            proxy.StencilPublishedSequence < owner.GpuWriteSequence;
    }

    // The proxies of the depth over exactly this plane. With downloadOnly, only those with bytes to save.
    private void FindStencilProxies(GuestSpan plane, ResourceSlotIdentifier depthIdentifier, bool downloadOnly, ref List<StencilRelease>? found)
    {
        if (!ImageDescription.IsValidRange(plane))
        {
            return;
        }

        foreach (var candidateIdentifier in FindImagesInRange(plane.Address, plane.Size, pageOverlap: false))
        {
            if (_slots.TryGet(candidateIdentifier) is { } candidate && candidate.DepthOwner == depthIdentifier &&
                candidate.Description.Data == plane && (!downloadOnly || NeedsStencilDownload(candidateIdentifier, candidate)))
            {
                (found ??= []).Add(new StencilRelease(candidateIdentifier, plane));
            }
        }
    }

    // Every proxy a depth owns, whatever its plane.
    private void FindStencilProxiesOf(ResourceSlotIdentifier depthIdentifier, bool downloadOnly, ref List<StencilRelease>? found)
    {
        List<StencilRelease>? owned = null;
        _slots.ForEach((identifier, image) =>
        {
            if (image.DepthOwner == depthIdentifier && (!downloadOnly || NeedsStencilDownload(identifier, image)))
            {
                (owned ??= []).Add(new StencilRelease(identifier, image.Description.Data));
            }
        });
        if (owned != null)
        {
            (found ??= []).AddRange(owned);
        }
    }

    // Obtains the buffers of the planes to save. Called without the lock.
    private Dictionary<ResourceSlotIdentifier, GpuBuffer>? ObtainStencilBuffers(List<StencilRelease>? releases)
    {
        if (releases == null || SubmissionScheduler.ReadOnlyThread)
        {
            return null;
        }

        Dictionary<ResourceSlotIdentifier, GpuBuffer>? buffers = null;
        foreach (var release in releases)
        {
            if (_bufferCache.ObtainBufferForImageWriteBack(release.Plane.Address, release.Plane.Size) is { } buffer)
            {
                (buffers ??= [])[release.Proxy] = buffer;
            }
        }

        return buffers;
    }

    // The planes a depth target acquisition releases: the one its aspect leaves, and those it takes from other depths.
    private Dictionary<ResourceSlotIdentifier, GpuBuffer>? PrepareStencilBuffers(ResourceSlotIdentifier depthIdentifier, in ImageRequest request)
    {
        if (SubmissionScheduler.ReadOnlyThread || !request.Description.HasStencil)
        {
            return null;
        }

        List<StencilRelease>? releases = null;
        using (var held = _lock.Hold())
        {
            if (_slots.TryGet(depthIdentifier) is not { } depth || !depth.Registered || depth.DepthOwner.IsValid ||
                !depth.Description.HasStencil)
            {
                return null;
            }

            var newPlane = StencilPlaneOfImage(depth.Description, request.Description);
            var oldPlane = depth.Description.Stencil;
            if (!oldPlane.Equals(newPlane))
            {
                FindStencilProxies(oldPlane, depthIdentifier, downloadOnly: true, ref releases);
            }

            CollectTakenStencilProxies(depthIdentifier, depth, newPlane, ref releases);
        }

        return ObtainStencilBuffers(releases);
    }

    // The plane a GPU write names for the depth (its current one by default): what it takes from other depths.
    private Dictionary<ResourceSlotIdentifier, GpuBuffer>? PrepareStencilBuffers(ResourceSlotIdentifier depthIdentifier, GuestSpan? plane = null)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            return null;
        }

        List<StencilRelease>? releases = null;
        using (var held = _lock.Hold())
        {
            if (_slots.TryGet(depthIdentifier) is { Registered: true } depth && !depth.DepthOwner.IsValid && depth.Description.HasStencil)
            {
                CollectTakenStencilProxies(depthIdentifier, depth, plane ?? depth.Description.Stencil, ref releases);
            }
        }

        return ObtainStencilBuffers(releases);
    }

    // The proxies a depth owns, as when it is deleted.
    private Dictionary<ResourceSlotIdentifier, GpuBuffer>? PrepareOwnedStencilBuffers(ResourceSlotIdentifier depthIdentifier)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            return null;
        }

        List<StencilRelease>? releases = null;
        using (var held = _lock.Hold())
        {
            FindStencilProxiesOf(depthIdentifier, downloadOnly: true, ref releases);
        }

        return ObtainStencilBuffers(releases);
    }

    private void CollectTakenStencilProxies(ResourceSlotIdentifier depthIdentifier, CachedImage depth, GuestSpan plane, ref List<StencilRelease>? releases)
    {
        if (!ImageDescription.IsValidRange(plane))
        {
            return;
        }

        // The same proxy AssociateStencilRange will release: the one it reuses, when another depth holds it.
        var association = FindStencilAssociation(depth, plane);
        if (association.IsValid && _slots[association] is { DepthOwner.IsValid: true } proxy && proxy.DepthOwner != depthIdentifier &&
            NeedsStencilDownload(association, proxy))
        {
            (releases ??= []).Add(new StencilRelease(association, plane));
        }
    }

    // Releases the proxies the depth owns over the plane its aspect leaves. The caller holds the lock.
    private void ReleaseLeftStencilPlane(ResourceSlotIdentifier depthIdentifier, GuestSpan plane)
    {
        List<StencilRelease>? found = null;
        FindStencilProxies(plane, depthIdentifier, downloadOnly: false, ref found);
        foreach (var release in found ?? [])
        {
            ReleaseStencilProxy(release.Proxy, StencilProxyFate.DetachIfBacked);
        }
    }

    // The one release path. The caller holds the lock, so only a buffer prepared before it was taken can be used.
    // An aspect that cannot be read back (compressed, multisampled, no buffer or unbacked range) is dropped like an
    // evicted image's contents: the proxy is detached regardless, never left pointing at an aspect that holds another
    // plane.
    private void ReleaseStencilProxy(ResourceSlotIdentifier proxyIdentifier, StencilProxyFate fate)
    {
        var proxy = _slots.TryGet(proxyIdentifier);
        if (proxy is null || !proxy.DepthOwner.IsValid)
        {
            return;
        }

        if (_preparedStencilBuffers != null && _preparedStencilBuffers.TryGetValue(proxyIdentifier, out var buffer) &&
            NeedsStencilDownload(proxyIdentifier, proxy) && TryDownloadStencilToBuffer(proxy, buffer))
        {
            // Older GPU-written images over the plane must not publish over what was just saved.
            MarkOlderOverlappingImagesOverwritten(proxyIdentifier, proxy);
        }

        proxy.DetachDepth();
        proxy.ClearGpuModified();
        if (fate == StencilProxyFate.Delete || (fate == StencilProxyFate.DetachIfBacked && !proxy.Backing.Exists))
        {
            DeleteImage(proxyIdentifier);
        }
        else
        {
            // Its native bytes, if any, predate the aspect's writes: they reload from guest memory.
            proxy.MarkBufferModified();
        }
    }

    // An overlap resolution replaces or drops a depth image under the lock, where no buffer can be obtained to save its
    // planes. The aspect's unsaved bytes move into each proxy's own R8 image instead, which becomes an ordinary
    // GPU-written image of the plane: it publishes to guest memory like any other, and the next depth that takes the
    // plane restores it natively. The caller holds the lock and records on the render thread.
    private void KeepStencilPlanesInProxies(ResourceSlotIdentifier depthIdentifier)
    {
        if (_slots.TryGet(depthIdentifier) is not { } depth || depth.DepthOwner.IsValid || !depth.Description.HasStencil)
        {
            return;
        }

        List<StencilRelease>? owned = null;
        FindStencilProxiesOf(depthIdentifier, downloadOnly: true, ref owned);
        foreach (var release in owned ?? [])
        {
            var proxy = _slots[release.Proxy];
            if (!proxy.Backing.Exists || proxy.Backing.Format is not (Format.R8Uint or Format.R8Unorm) ||
                proxy.Backing.ImageType != ImageType.Type2D || proxy.Backing.Samples != 1 || proxy.Backing.MipLevels != 1 ||
                depth.Backing.MipLevels != 1 || proxy.Backing.Layers != depth.Backing.Layers ||
                !SameExtent(proxy.Backing.Extent, depth.Backing.Extent))
            {
                continue;
            }

            depth.CopyStencilStorage(proxy, _bufferCache.GetUtilityBuffer(GpuBufferUsage.DeviceLocal), writeBack: false);
            proxy.DetachDepth();
            TakeGpuOwnership(proxy);
            // Older GPU-written images over the plane must not publish over what the proxy now holds.
            MarkOlderOverlappingImagesOverwritten(release.Proxy, proxy);
        }
    }

    // Releases the planes of depths whose eviction was deferred, with buffers obtained outside the lock.
    private bool ReleasePendingStencilPlanes()
    {
        List<ResourceSlotIdentifier> depths;
        using (var held = _lock.Hold())
        {
            if (_pendingStencilReleases.Count == 0)
            {
                return false;
            }

            depths = [.. _pendingStencilReleases];
            _pendingStencilReleases.Clear();
        }

        foreach (var depthIdentifier in depths)
        {
            _preparedStencilBuffers = PrepareOwnedStencilBuffers(depthIdentifier);
            try
            {
                using var held = _lock.Hold();
                List<StencilRelease>? owned = null;
                FindStencilProxiesOf(depthIdentifier, downloadOnly: false, ref owned);
                foreach (var release in owned ?? [])
                {
                    ReleaseStencilProxy(release.Proxy, StencilProxyFate.DetachIfBacked);
                }
            }
            finally
            {
                _preparedStencilBuffers = null;
            }
        }

        return true;
    }

    // Whether evicting the depth would lose plane bytes that a prepared release could save. The caller holds the lock.
    private bool HasUnsavedStencilPlane(ResourceSlotIdentifier depthIdentifier)
    {
        List<StencilRelease>? releases = null;
        FindStencilProxiesOf(depthIdentifier, downloadOnly: true, ref releases);
        return releases != null;
    }
}
