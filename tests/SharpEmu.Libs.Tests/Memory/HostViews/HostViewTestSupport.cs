// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;
using SharpEmu.HLE.Host.Windows;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.HostViews;

internal static class HostViewTestSupport
{
    public const ulong Segment = 0x4000;
    public const ulong BackingSize = 1UL << 20;
    public const ulong Marker = 0x5348_5250_5649_4557;

    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static IHostMemory PlatformMemory => OperatingSystem.IsWindows() ? new WindowsHostMemory() : new PosixHostMemory();

    public static ulong HoleSize(IHostViewMemory views) => AlignUp(4 * Segment, views.Granularity);

    // Keep search tests below the guest address limit and outside reserved graphics memory.
    public static ulong ProbeGuestAddress(IHostViewMemory views, ulong size)
    {
        size = AlignUp(size, views.Granularity);
        var step = AlignUp(Math.Max(size, 0x1000000UL), views.Granularity);
        for (var candidate = 0x70_0000_0000UL; candidate < 0x71_0000_0000UL; candidate += step)
        {
            if (views.ReserveHole(candidate, size) != candidate) continue;
            Assert.True(views.FreeHole(candidate, size));
            return candidate;
        }
        Assert.Fail("No free guest test range was found.");
        return 0;
    }

    public static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    // Reserves then frees a probe so the address is free and granularity-aligned.
    public static ulong ProbeFreeAddress(IHostViewMemory views, ulong size)
    {
        var memory = PlatformMemory;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var probe = memory.Reserve(0, size, HostPageProtection.ReadWrite);
            Assert.NotEqual(0UL, probe);
            Assert.True(memory.Free(probe));
            if (probe % views.Granularity == 0)
            {
                return probe;
            }
        }

        Assert.Fail("no aligned free address after 8 attempts");
        return 0;
    }

    // No fixed replacement may run unless ReserveHole returned this exact address.
    public static ulong ReserveFreeHole(IHostViewMemory views, ulong size)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var probe = ProbeFreeAddress(views, size);
            if (views.ReserveHole(probe, size) == probe)
            {
                return probe;
            }
        }

        Assert.Fail("no free hole could be reserved after 8 attempts");
        return 0;
    }

    public static HostBackingObject CreateBacking(IHostViewMemory views)
    {
        Assert.True(views.TryCreateBacking(BackingSize, out var backing, out var failure));
        Assert.Equal(HostViewFailure.None, failure);
        return backing!;
    }
}
