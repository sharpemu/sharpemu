// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal sealed class GuestImageResource
{
    public ulong Address;
    public ulong GuestSize;
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public Format Format;
    public Image Image;
    public DeviceMemory Memory;
    public ImageView View;
    public ImageView[] MipViews = [];
    public Dictionary<(Format Format, uint MipLevel, uint LevelCount, uint DstSelect), ImageView> FormatViews { get; } = new();
    public RenderPass RenderPass;
    public Framebuffer Framebuffer;
    public bool Initialized;
    public bool InitialUploadPending;
    public bool IsCpuBacked;
    public ulong CpuContentFingerprint;
}

internal sealed class GuestImageCache : IDisposable
{
    private readonly Dictionary<ulong, GuestImageResource> _resources = new();
    private readonly Action _waitForIdle;
    private readonly Action<GuestImageResource> _destroy;

    public GuestImageCache(Action waitForIdle, Action<GuestImageResource> destroy)
    {
        _waitForIdle = waitForIdle;
        _destroy = destroy;
    }

    public bool TryGetValue(ulong address, out GuestImageResource resource) =>
        _resources.TryGetValue(address, out resource!);

    public bool ContainsKey(ulong address) => _resources.ContainsKey(address);

    public void Add(GuestImageResource resource)
    {
        ArgumentOutOfRangeException.ThrowIfZero(resource.GuestSize);
        _resources.Add(resource.Address, resource);
    }

    public IReadOnlyList<GuestImageResource> InvalidateOverlaps(ulong address, ulong size)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        var end = checked(address + size);
        // ponytail: image counts are small; use an interval index only if profiling justifies it.
        var overlaps = _resources.Values
            .Where(resource => RangesOverlap(address, end, resource.Address, resource.GuestSize))
            .ToArray();
        if (overlaps.Length == 0)
        {
            return overlaps;
        }

        _waitForIdle();
        foreach (var resource in overlaps)
        {
            _resources.Remove(resource.Address);
            _destroy(resource);
        }

        return overlaps;
    }

    public void Dispose()
    {
        if (_resources.Count == 0)
        {
            return;
        }

        _waitForIdle();
        foreach (var resource in _resources.Values)
        {
            _destroy(resource);
        }

        _resources.Clear();
    }

    private static bool RangesOverlap(ulong leftStart, ulong leftEnd, ulong rightStart, ulong rightSize)
    {
        var rightEnd = checked(rightStart + rightSize);
        return leftStart < rightEnd && rightStart < leftEnd;
    }
}
