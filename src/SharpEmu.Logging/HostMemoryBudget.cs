// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpEmu.Logging;

/// <summary>
/// Host-aware memory budgets so PCs with ~16 GiB RAM do not advertise a full
/// PS5-scale direct-memory pool and then commit it into host OOM.
/// Override with SHARPEMU_DIRECT_MEMORY_MB (MiB, clamped).
/// </summary>
public static class HostMemoryBudget
{
    /// <summary>PS5-class direct memory pool size reported by real hardware.</summary>
    public const ulong Ps5DirectMemoryBytes = 16384UL * 1024 * 1024;

    private const ulong MinimumDirectMemoryBytes = 2048UL * 1024 * 1024;
    private const ulong HostRuntimeHeadroomBytes = 6144UL * 1024 * 1024;
    private const ulong UnknownHostFallbackDirectMemoryBytes = 8192UL * 1024 * 1024;

    private static readonly Lazy<Budget> Resolved = new(Resolve);

    /// <summary>Best-effort total host physical RAM, or 0 when unknown.</summary>
    public static ulong TotalPhysicalMemoryBytes => Resolved.Value.TotalPhysicalBytes;

    /// <summary>Size exposed through sceKernelGetDirectMemorySize / DM allocators.</summary>
    public static ulong AdvertisedDirectMemoryBytes => Resolved.Value.AdvertisedDirectMemoryBytes;

    /// <summary>
    /// Non-executable mappings larger than this prefer reserve + lazy commit when
    /// the host is memory-constrained (still after a full-commit attempt fails).
    /// </summary>
    public static ulong FullCommitRegionLimitBytes => Resolved.Value.FullCommitRegionLimitBytes;

    /// <summary>Minimum size before a mapping may use the lazy-reserve fallback.</summary>
    public static ulong LargeDataReserveThresholdBytes => Resolved.Value.LargeDataReserveThresholdBytes;

    /// <summary>Default pending guest GPU work queue cap when env is unset.</summary>
    public static ulong DefaultPendingGuestWorkBytes => Resolved.Value.DefaultPendingGuestWorkBytes;

    /// <summary>Default host buffer cache cap when env is unset.</summary>
    public static ulong DefaultCachedHostBufferBytes => Resolved.Value.DefaultCachedHostBufferBytes;

    /// <summary>Default SHARPEMU_RENDER_SCALE when env is unset.</summary>
    public static double DefaultRenderScale => Resolved.Value.DefaultRenderScale;

    /// <summary>True when the advertised DM pool is smaller than the PS5 default.</summary>
    public static bool IsConstrained => AdvertisedDirectMemoryBytes < Ps5DirectMemoryBytes;

    /// <summary>One-line diagnostic for startup logs.</summary>
    public static string Summary => Resolved.Value.Summary;

    private static Budget Resolve()
    {
        var totalPhysical = TryGetTotalPhysicalMemoryBytes();
        var advertised = ResolveAdvertisedDirectMemory(totalPhysical, out var source);
        var constrained = advertised < Ps5DirectMemoryBytes ||
            (totalPhysical != 0 && totalPhysical <= 20UL * 1024 * 1024 * 1024);

        // On constrained hosts, allow lazy reserve for multi-hundred-MiB maps so
        // guest "allocate everything" paths do not pin 8+ GiB of host RAM.
        var largeThreshold = constrained ? 512UL * 1024 * 1024 : 1024UL * 1024 * 1024;
        var fullCommitLimit = constrained ? 512UL * 1024 * 1024 : 4UL << 30;
        var pendingWorkMb = constrained ? 128UL : 256UL;
        var cachedBuffers = constrained ? 64UL * 1024 * 1024 : 128UL * 1024 * 1024;
        var renderScale = constrained ? 0.75 : 1.0;

        if (totalPhysical != 0 && totalPhysical < 12UL * 1024 * 1024 * 1024)
        {
            pendingWorkMb = 64UL;
            cachedBuffers = 32UL * 1024 * 1024;
            renderScale = 0.5;
        }

        var hostText = totalPhysical == 0
            ? "unknown"
            : $"{totalPhysical / (1024 * 1024):N0} MB";
        var summary =
            $"Host memory budget: host_ram={hostText}, " +
            $"direct_memory={advertised / (1024 * 1024):N0} MB ({source}), " +
            $"lazy_threshold={largeThreshold / (1024 * 1024)} MB, " +
            $"full_commit_limit={fullCommitLimit / (1024 * 1024)} MB, " +
            $"pending_gpu_work={pendingWorkMb} MB, " +
            $"render_scale_default={renderScale.ToString("0.##", CultureInfo.InvariantCulture)}";

        return new Budget(
            totalPhysical,
            advertised,
            fullCommitLimit,
            largeThreshold,
            pendingWorkMb * 1024UL * 1024UL,
            cachedBuffers,
            renderScale,
            summary);
    }

    private static ulong ResolveAdvertisedDirectMemory(ulong totalPhysical, out string source)
    {
        var overrideMb = Environment.GetEnvironmentVariable("SHARPEMU_DIRECT_MEMORY_MB");
        if (ulong.TryParse(overrideMb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) &&
            mb > 0)
        {
            // Keep a playable floor and never advertise more than real PS5 DM.
            var clamped = Math.Clamp(mb, 512UL, 16384UL) * 1024UL * 1024UL;
            source = $"SHARPEMU_DIRECT_MEMORY_MB={mb}";
            return clamped;
        }

        if (totalPhysical == 0)
        {
            source = "fallback";
            return UnknownHostFallbackDirectMemoryBytes;
        }

        // Leave headroom for the OS, GPU driver, .NET runtime, and Vulkan images.
        var usable = totalPhysical > HostRuntimeHeadroomBytes
            ? totalPhysical - HostRuntimeHeadroomBytes
            : MinimumDirectMemoryBytes;
        var advertised = Math.Clamp(usable, MinimumDirectMemoryBytes, Ps5DirectMemoryBytes);
        source = "auto";
        return advertised;
    }

    private static ulong TryGetTotalPhysicalMemoryBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MemoryStatusEx
                {
                    dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
                };
                if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
                {
                    return status.ullTotalPhys;
                }
            }
            catch (Exception)
            {
                // Diagnostic / budgeting only.
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 &&
                        ulong.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                    {
                        return kb * 1024UL;
                    }
                }
            }
            catch (Exception)
            {
                // Diagnostic / budgeting only.
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                // sysctl hw.memsize
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/sbin/sysctl",
                    Arguments = "-n hw.memsize",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process is not null)
                {
                    var text = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(2000);
                    if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) &&
                        bytes > 0)
                    {
                        return bytes;
                    }
                }
            }
            catch (Exception)
            {
                // Diagnostic / budgeting only.
            }
        }

        return 0;
    }

    private readonly record struct Budget(
        ulong TotalPhysicalBytes,
        ulong AdvertisedDirectMemoryBytes,
        ulong FullCommitRegionLimitBytes,
        ulong LargeDataReserveThresholdBytes,
        ulong DefaultPendingGuestWorkBytes,
        ulong DefaultCachedHostBufferBytes,
        double DefaultRenderScale,
        string Summary);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
