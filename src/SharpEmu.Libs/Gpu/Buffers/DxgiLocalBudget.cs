// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// IDXGIAdapter3 reports the current process's local-segment usage. Select the Vulkan
// adapter and node explicitly, including on hybrid and linked-adapter systems.
internal sealed unsafe class DxgiLocalBudget : SafeHandle
{
    private readonly uint _node;
    private DxgiLocalBudget(nint adapter, uint node) : base(0, true)
    {
        SetHandle(adapter);
        _node = node;
    }
    public override bool IsInvalid => handle == 0;

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(Guid* iid, nint* factory);

    internal static DxgiLocalBudget? Open(Vk vk, PhysicalDevice physical)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return null;
        var id = new PhysicalDeviceIDProperties { SType = StructureType.PhysicalDeviceIDProperties };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &id };
        vk.GetPhysicalDeviceProperties2(physical, &props);
        if (!id.DeviceLuidvalid || !BitOperations.IsPow2(id.DeviceNodeMask)) return null;
        var factoryIid = new Guid("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
        nint factory;
        if (CreateDXGIFactory1(&factoryIid, &factory) < 0) return null;
        try
        {
            var adapterIid = new Guid("645967a4-1392-4310-a798-8053ce3e93fd");
            nint adapter;
            // IDXGIFactory4::EnumAdapterByLuid, dxgi1_4.h.
            var byLuid = (delegate* unmanaged[Stdcall]<nint, ulong, Guid*, nint*, int>)(*(nint**)factory)[26];
            var luid = BitConverter.ToUInt64(new ReadOnlySpan<byte>(id.DeviceLuid, 8));
            return byLuid(factory, luid, &adapterIid, &adapter) < 0 ? null :
                new DxgiLocalBudget(adapter, (uint)BitOperations.TrailingZeroCount(id.DeviceNodeMask));
        }
        finally { Marshal.Release(factory); }
    }

    internal (ulong Budget, ulong Usage) Read()
    {
        // DXGI_QUERY_VIDEO_MEMORY_INFO is four UINT64s. Segment group zero is LOCAL.
        ulong* info = stackalloc ulong[4];
        bool held = false;
        try
        {
            DangerousAddRef(ref held);
            var query = (delegate* unmanaged[Stdcall]<nint, uint, uint, ulong*, int>)(*(nint**)handle)[14];
            return query(handle, _node, 0, info) < 0 ? default : (info[0], info[1]);
        }
        finally { if (held) DangerousRelease(); }
    }

    protected override bool ReleaseHandle()
    {
        Marshal.Release(handle);
        return true;
    }
}
