// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpEmu.HLE.Host.DualSense;

namespace SharpEmu.HLE.Host.Mac;

/// <summary>
/// DualSense transport over macOS IOKit. There is no raw HID device node on
/// macOS, so the pad is matched and opened through IOHIDManager and input
/// reports arrive on a CFRunLoop callback, which this transport runs on its
/// own thread and drains into <see cref="Read"/>.
/// </summary>
// unsafe: the IOKit input-report callback hands back a raw byte*.
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacDualSenseTransport : DualSenseTransport
{
    /// <summary>Full input report sizes including the leading report id.</summary>
    private const int UsbInputReportSize = 64;
    private const int BluetoothInputReportSize = 78;

    /// <summary>Generous upper bound for the callback's report buffer.</summary>
    private const int InputBufferSize = 256;

    /// <summary>CFRunLoopRunInMode: the loop had no source and returned at once.</summary>
    private const int KCFRunLoopRunFinished = 1;

    // The callback is passed to native code as a function pointer; the field
    // keeps the delegate alive for the process lifetime.
    private static readonly MacHidNative.IOHIDReportCallback ReportCallback = OnInputReport;
    private static readonly nint ReportCallbackPointer =
        Marshal.GetFunctionPointerForDelegate(ReportCallback);

    private readonly nint _manager;
    private readonly nint _device;
    private readonly bool _bluetooth;
    private readonly nint _inputBuffer;
    private readonly GCHandle _self;
    private readonly Thread _runLoopThread;
    private readonly ManualResetEventSlim _runLoopReady = new(false);

    private readonly object _gate = new();
    private readonly byte[] _latest = new byte[InputBufferSize];
    private int _latestLength;
    private bool _hasReport;
    private nint _runLoop;

    // Written under _gate but also read by the run-loop thread outside it.
    private volatile bool _disposed;

    private MacDualSenseTransport(nint manager, nint device, bool bluetooth)
    {
        _manager = manager;
        _device = device;
        _bluetooth = bluetooth;
        _inputBuffer = Marshal.AllocHGlobal(InputBufferSize);
        _self = GCHandle.Alloc(this, GCHandleType.Normal);

        _runLoopThread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "DualSense IOKit run loop",
        };
        _runLoopThread.Start();
        // The run loop must be scheduled before reports can arrive; waiting
        // keeps a rumble sent right after open from being dropped.
        _runLoopReady.Wait(TimeSpan.FromSeconds(2));
    }

    internal override bool Bluetooth => _bluetooth;

    /// <summary>IOKit publishes the device's transport, so the bus is known up front.</summary>
    internal override bool ReportsTransport => true;

    /// <summary>
    /// Opens the first DualSense IOKit exposes. <paramref name="unavailableReason"/>
    /// is set only when a pad was matched but could not be opened, which on
    /// macOS means another process holds it exclusively or Input Monitoring
    /// was denied.
    /// </summary>
    internal static MacDualSenseTransport? TryOpen(out string? unavailableReason)
    {
        unavailableReason = null;

        var manager = MacHidNative.IOHIDManagerCreate(0, MacHidNative.KIOHIDOptionsTypeNone);
        if (manager == 0)
        {
            return null;
        }

        // Ownership of the manager passes to the transport only on success;
        // every early return below has to hand it back.
        var transferred = false;
        try
        {
            if (!TrySetDualSenseMatching(manager) ||
                MacHidNative.IOHIDManagerOpen(manager, MacHidNative.KIOHIDOptionsTypeNone) != MacHidNative.KIOReturnSuccess)
            {
                return null;
            }

            var device = CopyFirstMatchingDevice(manager);
            if (device == 0)
            {
                return null; // matching worked, nothing plugged in
            }

            if (MacHidNative.IOHIDDeviceOpen(device, MacHidNative.KIOHIDOptionsTypeNone) != MacHidNative.KIOReturnSuccess)
            {
                unavailableReason =
                    "A DualSense is connected but macOS would not open it. Another application may " +
                    "be holding the controller, or SharpEmu needs Input Monitoring permission in " +
                    "System Settings > Privacy & Security.";
                return null;
            }

            var bluetooth = IsBluetooth(device);
            RequestFullReportMode(device);
            var transport = new MacDualSenseTransport(manager, device, bluetooth);
            transferred = true;
            return transport;
        }
        finally
        {
            if (!transferred)
            {
                _ = MacHidNative.IOHIDManagerClose(manager, MacHidNative.KIOHIDOptionsTypeNone);
                MacHidNative.CFRelease(manager);
            }
        }
    }

    internal override int Read(Span<byte> buffer)
    {
        lock (_gate)
        {
            while (!_hasReport && !_disposed)
            {
                Monitor.Wait(_gate);
            }

            if (_disposed)
            {
                return 0;
            }

            var length = Math.Min(_latestLength, buffer.Length);
            _latest.AsSpan(0, length).CopyTo(buffer);
            _hasReport = false;
            return length;
        }
    }

    internal override bool Write(ReadOnlySpan<byte> report)
    {
        if (report.IsEmpty)
        {
            return false;
        }

        var native = Marshal.AllocHGlobal(report.Length);
        try
        {
            Marshal.Copy(report.ToArray(), 0, native, report.Length);
            // The report id leads the buffer and is also passed separately,
            // matching IOKit's numbered-report convention.
            return MacHidNative.IOHIDDeviceSetReport(
                _device,
                MacHidNative.KIOHIDReportTypeOutput,
                report[0],
                native,
                report.Length) == MacHidNative.KIOReturnSuccess;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Monitor.PulseAll(_gate); // release a blocked Read
        }

        var runLoop = Volatile.Read(ref _runLoop);
        if (runLoop != 0)
        {
            MacHidNative.CFRunLoopStop(runLoop);
        }

        _runLoopThread.Join(TimeSpan.FromSeconds(2));

        _ = MacHidNative.IOHIDDeviceClose(_device, MacHidNative.KIOHIDOptionsTypeNone);
        _ = MacHidNative.IOHIDManagerClose(_manager, MacHidNative.KIOHIDOptionsTypeNone);
        MacHidNative.CFRelease(_manager);

        Marshal.FreeHGlobal(_inputBuffer);
        if (_self.IsAllocated)
        {
            _self.Free();
        }

        _runLoopReady.Dispose();
    }

    private void RunLoop()
    {
        var mode = MacHidNative.KCFRunLoopDefaultMode;
        if (mode == 0)
        {
            // CoreFoundation is not what we expect; without a run-loop mode no
            // report can ever arrive, so report "no pad" instead of spinning.
            _runLoopReady.Set();
            return;
        }

        var runLoop = MacHidNative.CFRunLoopGetCurrent();
        Volatile.Write(ref _runLoop, runLoop);

        MacHidNative.IOHIDDeviceRegisterInputReportCallback(
            _device,
            _inputBuffer,
            InputBufferSize,
            ReportCallbackPointer,
            GCHandle.ToIntPtr(_self));
        MacHidNative.IOHIDDeviceScheduleWithRunLoop(_device, runLoop, mode);
        _runLoopReady.Set();

        while (!_disposed)
        {
            // A bounded run keeps the loop responsive to Dispose even if the
            // pad goes quiet and no source ever fires.
            var result = MacHidNative.CFRunLoopRunInMode(mode, 0.5, false);

            // kCFRunLoopRunFinished (1) means the loop had no source to wait
            // on and returned at once. Spinning on that would burn a core, so
            // back off; the reader still sees a live-but-silent transport.
            if (result == KCFRunLoopRunFinished)
            {
                Thread.Sleep(100);
            }
        }

        MacHidNative.IOHIDDeviceUnscheduleFromRunLoop(_device, runLoop, mode);
        MacHidNative.IOHIDDeviceRegisterInputReportCallback(_device, _inputBuffer, InputBufferSize, 0, 0);
    }

    private static unsafe void OnInputReport(
        nint context,
        int result,
        nint sender,
        uint type,
        uint reportId,
        byte* report,
        nint reportLength)
    {
        if (context == 0 || result != MacHidNative.KIOReturnSuccess || reportLength <= 0)
        {
            return;
        }

        if (GCHandle.FromIntPtr(context).Target is not MacDualSenseTransport transport)
        {
            return;
        }

        transport.StoreReport(reportId, new ReadOnlySpan<byte>(report, (int)reportLength));
    }

    /// <summary>
    /// Normalizes a callback report to "report id first", which is what
    /// <see cref="DualSenseProtocol"/> parses. IOKit includes the id for some
    /// descriptors and strips it for others, so the decision is made on the
    /// full report size for the id rather than by sniffing the first byte —
    /// a stick axis can legitimately hold 0x01 or 0x31.
    /// </summary>
    private void StoreReport(uint reportId, ReadOnlySpan<byte> raw)
    {
        var expectedFullSize = reportId == 0x31 ? BluetoothInputReportSize : UsbInputReportSize;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            int length;
            if (raw.Length >= expectedFullSize && raw[0] == (byte)reportId)
            {
                length = Math.Min(raw.Length, _latest.Length);
                raw[..length].CopyTo(_latest);
            }
            else
            {
                length = Math.Min(raw.Length + 1, _latest.Length);
                _latest[0] = (byte)reportId;
                raw[..(length - 1)].CopyTo(_latest.AsSpan(1));
            }

            _latestLength = length;
            _hasReport = true;
            Monitor.Pulse(_gate);
        }
    }

    private static bool TrySetDualSenseMatching(nint manager)
    {
        if (MacHidNative.KCFTypeDictionaryKeyCallBacks == 0 ||
            MacHidNative.KCFTypeArrayCallBacks == 0)
        {
            return false; // CoreFoundation is not what we expect; stay inert
        }

        var vendorKey = MacHidNative.CreateCFString("VendorID");
        var productKey = MacHidNative.CreateCFString("ProductID");
        var dictionaries = new[]
        {
            CreateMatchingDictionary(vendorKey, productKey, DualSenseProtocol.DualSenseProductId),
            CreateMatchingDictionary(vendorKey, productKey, DualSenseProtocol.DualSenseEdgeProductId),
        };

        try
        {
            if (Array.Exists(dictionaries, d => d == 0))
            {
                return false;
            }

            var handle = GCHandle.Alloc(dictionaries, GCHandleType.Pinned);
            try
            {
                var array = MacHidNative.CFArrayCreate(
                    0,
                    handle.AddrOfPinnedObject(),
                    dictionaries.Length,
                    MacHidNative.KCFTypeArrayCallBacks);
                if (array == 0)
                {
                    return false;
                }

                MacHidNative.IOHIDManagerSetDeviceMatchingMultiple(manager, array);
                MacHidNative.CFRelease(array);
                return true;
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            foreach (var dictionary in dictionaries)
            {
                if (dictionary != 0)
                {
                    MacHidNative.CFRelease(dictionary);
                }
            }

            MacHidNative.CFRelease(vendorKey);
            MacHidNative.CFRelease(productKey);
        }
    }

    private static nint CreateMatchingDictionary(nint vendorKey, nint productKey, ushort productId)
    {
        var dictionary = MacHidNative.CFDictionaryCreateMutable(
            0,
            0,
            MacHidNative.KCFTypeDictionaryKeyCallBacks,
            MacHidNative.KCFTypeDictionaryValueCallBacks);
        if (dictionary == 0)
        {
            return 0;
        }

        var vendor = MacHidNative.CreateCFNumber(DualSenseProtocol.SonyVendorId);
        var product = MacHidNative.CreateCFNumber(productId);
        try
        {
            if (vendor == 0 || product == 0)
            {
                MacHidNative.CFRelease(dictionary);
                return 0;
            }

            MacHidNative.CFDictionarySetValue(dictionary, vendorKey, vendor);
            MacHidNative.CFDictionarySetValue(dictionary, productKey, product);
            return dictionary;
        }
        finally
        {
            if (vendor != 0)
            {
                MacHidNative.CFRelease(vendor);
            }

            if (product != 0)
            {
                MacHidNative.CFRelease(product);
            }
        }
    }

    private static nint CopyFirstMatchingDevice(nint manager)
    {
        var devices = MacHidNative.IOHIDManagerCopyDevices(manager);
        if (devices == 0)
        {
            return 0;
        }

        try
        {
            var count = (int)MacHidNative.CFSetGetCount(devices);
            if (count <= 0)
            {
                return 0;
            }

            var values = new nint[count];
            var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                MacHidNative.CFSetGetValues(devices, handle.AddrOfPinnedObject());
                return values[0];
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            MacHidNative.CFRelease(devices);
        }
    }

    private static bool IsBluetooth(nint device)
    {
        var key = MacHidNative.CreateCFString("Transport");
        try
        {
            var transport = MacHidNative.ReadCFString(MacHidNative.IOHIDDeviceGetProperty(device, key));
            return transport is not null &&
                   transport.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            MacHidNative.CFRelease(key);
        }
    }

    /// <summary>
    /// Bluetooth quirk: the DualSense sends a simplified report until feature
    /// report 0x05 is requested, which switches it to the full 0x31 input
    /// report. Harmless over USB.
    /// </summary>
    private static void RequestFullReportMode(nint device)
    {
        var length = (nint)DualSenseProtocol.BluetoothEnableFeatureReportSize;
        var buffer = Marshal.AllocHGlobal(DualSenseProtocol.BluetoothEnableFeatureReportSize);
        try
        {
            Marshal.WriteByte(buffer, 0, DualSenseProtocol.BluetoothEnableFeatureReportId);
            _ = MacHidNative.IOHIDDeviceGetReport(
                device,
                MacHidNative.KIOHIDReportTypeFeature,
                DualSenseProtocol.BluetoothEnableFeatureReportId,
                buffer,
                ref length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
