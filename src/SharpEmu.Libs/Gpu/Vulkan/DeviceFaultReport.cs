// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// Local diagnostic (SHARPEMU_DEVICE_FAULT_REPORT=1): on a lost device, print the driver's fault report
// (VK_EXT_device_fault) and the buffers whose device address ranges hold or precede each reported address.
internal static unsafe class DeviceFaultReport
{
    internal static readonly bool Requested = Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_FAULT_REPORT") == "1";

    private static readonly ConcurrentDictionary<ulong, (ulong Size, string Usage, ulong CpuAddress, long Created)> Buffers = new();
    private static readonly ConcurrentQueue<(ulong Address, ulong Size, string Usage, ulong CpuAddress, long Created, long Released)> Released = new();
    private static Vk? _vk;
    private static Device _device;
    private static int _written;
    private static long _sequence;

    internal static void Attach(Vk vk, Device device, bool vendorBinary)
    {
        _device = device;
        _written = 0;
        Done.Reset();
        _vk = vk;
        Console.Error.WriteLine($"[GPU][FAULT] VK_EXT_device_fault enabled vendor_binary={(vendorBinary ? 1 : 0)}");
    }

    internal static void Detach(Device device)
    {
        if (_device.Handle != device.Handle) return;
        _vk = null;
        Buffers.Clear();
        Released.Clear();
    }

    internal static void Track(ulong deviceAddress, ulong size, string usage, ulong cpuAddress)
    {
        if (!Requested || deviceAddress == 0) return;
        Buffers[deviceAddress] = (size, usage, cpuAddress, Interlocked.Increment(ref _sequence));
    }

    internal static void Untrack(ulong deviceAddress)
    {
        if (!Requested || deviceAddress == 0 || !Buffers.TryRemove(deviceAddress, out var buffer)) return;
        Released.Enqueue((deviceAddress, buffer.Size, buffer.Usage, buffer.CpuAddress, buffer.Created, Interlocked.Increment(ref _sequence)));
        while (Released.Count > 4096 && Released.TryDequeue(out _))
        {
        }
    }

    private static readonly ManualResetEventSlim Done = new(false);

    // Every lost-device fatal waits here, so the first report is complete before any of them stops the process.
    internal static void Write()
    {
        var vk = _vk;
        if (vk is null) return;
        if (Interlocked.Exchange(ref _written, 1) != 0)
        {
            Done.Wait(TimeSpan.FromSeconds(20));
            return;
        }

        var report = new System.Text.StringBuilder();
        try
        {
            var get = (delegate* unmanaged<Device, DeviceFaultCountsEXT*, DeviceFaultInfoEXT*, Result>)
                vk.GetDeviceProcAddr(_device, "vkGetDeviceFaultInfoEXT").Handle;
            if (get is null)
            {
                report.AppendLine("[GPU][FAULT] vkGetDeviceFaultInfoEXT is unavailable");
                return;
            }

            var counts = new DeviceFaultCountsEXT { SType = StructureType.DeviceFaultCountsExt };
            var result = get(_device, &counts, null);
            report.AppendLine($"[GPU][FAULT] counts result={result} addresses={counts.AddressInfoCount} vendor={counts.VendorInfoCount} binary={counts.VendorBinarySize}");
            if (result != Result.Success && result != Result.Incomplete) return;
            var addresses = new DeviceFaultAddressInfoEXT[Math.Max(1, checked((int)counts.AddressInfoCount))];
            var vendors = new DeviceFaultVendorInfoEXT[Math.Max(1, checked((int)counts.VendorInfoCount))];
            var binary = new byte[Math.Max(1, checked((int)counts.VendorBinarySize))];
            fixed (DeviceFaultAddressInfoEXT* addressInfos = addresses)
            fixed (DeviceFaultVendorInfoEXT* vendorInfos = vendors)
            fixed (byte* binaryData = binary)
            {
                var info = new DeviceFaultInfoEXT
                {
                    SType = StructureType.DeviceFaultInfoExt,
                    PAddressInfos = counts.AddressInfoCount != 0 ? addressInfos : null,
                    PVendorInfos = counts.VendorInfoCount != 0 ? vendorInfos : null,
                    PVendorBinaryData = counts.VendorBinarySize != 0 ? binaryData : null,
                };
                result = get(_device, &counts, &info);
                report.AppendLine($"[GPU][FAULT] info result={result} description=\"{Text(info.Description)}\"");
                if (result != Result.Success && result != Result.Incomplete) return;
                var live = Buffers.ToArray();
                Array.Sort(live, (left, right) => left.Key.CompareTo(right.Key));
                var released = Released.ToArray();
                var seen = new HashSet<(DeviceFaultAddressTypeEXT, ulong)>();
                for (var index = 0u; index < counts.AddressInfoCount; index++)
                {
                    var address = addressInfos[index];
                    if (!seen.Add((address.AddressType, address.ReportedAddress))) continue;
                    report.AppendLine(
                        $"[GPU][FAULT] address[{index}] type={address.AddressType} reported=0x{address.ReportedAddress:X16} precision=0x{address.AddressPrecision:X}");
                    DescribeBuffers(report, live, released, address.ReportedAddress);
                }

                for (var index = 0u; index < counts.VendorInfoCount; index++)
                {
                    var vendor = vendorInfos[index];
                    report.AppendLine(
                        $"[GPU][FAULT] vendor code=0x{vendor.VendorFaultCode:X} data=0x{vendor.VendorFaultData:X} description=\"{Text(vendor.Description)}\"");
                }

                if (counts.VendorBinarySize != 0)
                {
                    var path = Path.Combine(Path.GetTempPath(), $"sharpemu-device-fault-{Environment.ProcessId}.bin");
                    File.WriteAllBytes(path, binary.AsSpan(0, checked((int)counts.VendorBinarySize)).ToArray());
                    report.AppendLine($"[GPU][FAULT] vendor binary written: {path}");
                }
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"[GPU][FAULT] report failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            try
            {
                Console.Error.Write(report.ToString());
                Console.Error.Flush();
            }
            finally
            {
                Done.Set();
            }
        }
    }

    private static void DescribeBuffers(System.Text.StringBuilder report,
        KeyValuePair<ulong, (ulong Size, string Usage, ulong CpuAddress, long Created)>[] live,
        (ulong Address, ulong Size, string Usage, ulong CpuAddress, long Created, long Released)[] released, ulong address)
    {
        var low = 0;
        var high = live.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (live[middle].Key <= address) low = middle + 1;
            else high = middle;
        }

        for (var index = Math.Max(0, low - 2); index < low; index++)
        {
            var (start, buffer) = (live[index].Key, live[index].Value);
            var end = start + buffer.Size;
            var relation = address < end ? "inside" : $"past_end_by=0x{address - end:X}";
            report.AppendLine(
                $"[GPU][FAULT]   buffer 0x{start:X16}..0x{end:X16} size=0x{buffer.Size:X} usage={buffer.Usage} cpu=0x{buffer.CpuAddress:X16} seq={buffer.Created} {relation}");
        }

        if (low < live.Length)
        {
            var (start, buffer) = (live[low].Key, live[low].Value);
            report.AppendLine(
                $"[GPU][FAULT]   next buffer 0x{start:X16} size=0x{buffer.Size:X} usage={buffer.Usage} cpu=0x{buffer.CpuAddress:X16} gap=0x{start - address:X}");
        }

        foreach (var entry in released)
        {
            if (address >= entry.Address && address - entry.Address < entry.Size)
            {
                report.AppendLine(
                    $"[GPU][FAULT]   released buffer 0x{entry.Address:X16} size=0x{entry.Size:X} usage={entry.Usage} cpu=0x{entry.CpuAddress:X16} seq={entry.Created}..{entry.Released}");
            }
        }
    }

    private static string Text(byte* text) => Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;
}
