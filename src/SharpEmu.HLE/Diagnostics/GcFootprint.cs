// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Logging;

namespace SharpEmu.HLE.Diagnostics;

public static class GcFootprint
{
    private static readonly Lazy<bool> SmallAtCreation = new(() => Constrained(ReadMemory()));

    internal static bool UseCompactBuffers => SmallAtCreation.Value;

    internal const long SmallHostBytes = 16L << 30;
    private const long LowAvailableBytes = 2L << 30;

    internal static bool Constrained(HostMemoryAvailability memory) =>
        memory.TotalPhysicalBytes is > 0 and <= SmallHostBytes ||
        memory.AvailablePhysicalBytes is >= 0 and <= LowAvailableBytes;

    internal static HostMemoryAvailability ReadMemory()
    {
        if (!OperatingSystem.IsLinux()) return HostSystemInfo.GetMemoryAvailability();
        try
        {
            long total = -1, available = -1;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var colon = line.IndexOf(':');
                if (colon < 0 || line[..colon] is not ("MemAvailable" or "MemTotal")) continue;
                var value = line.AsSpan(colon + 1).Trim();
                if (!value.EndsWith("kB") || !long.TryParse(value[..^2].Trim(), out var kib) ||
                    kib < 0 || kib > long.MaxValue / 1024) continue;
                if (line.StartsWith("MemTotal:")) total = kib * 1024;
                else available = kib * 1024;
                if (total >= 0 && available >= 0) break;
            }
            return new(-1, available, total);
        }
        catch (IOException) { return new(-1, -1, -1); }
        catch (UnauthorizedAccessException) { return new(-1, -1, -1); }
    }

    // Install only missing defaults before child creation. The GUI restores exactly these names.
    public static IEnumerable<KeyValuePair<string, string>> ChildDefaults()
    {
        var compact = Constrained(ReadMemory());
        return MissingDefaults(compact, Environment.GetEnvironmentVariable);
    }

    internal static IEnumerable<KeyValuePair<string, string>> MissingDefaults(bool compact, Func<string, string?> get)
    {
        foreach (var (suffix, value) in Defaults(compact))
        {
            var key = "DOTNET_" + suffix;
            if (get(key) is null && get("COMPlus_" + suffix) is null)
                yield return new(key, value);
        }
    }

    private static IEnumerable<(string, string)> Defaults(bool compact)
    {
        // CoreCLR reads these hexadecimal values at startup. gen0size is the minimum budget per heap.
        yield return ("GCgen0size", compact ? "4000000" : "10000000");
        yield return ("GCGen1MaxBudget", "8000000");
        if (compact) yield return ("GCConserveMemory", "5");
    }
}
