// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.HLE.Host.Mac;

/// <summary>
/// Minimal IOKit/CoreFoundation interop used to talk to a DualSense
/// controller directly, without any external input library. macOS exposes no
/// raw HID device node, so devices are matched and opened through
/// IOHIDManager and input reports arrive on a CFRunLoop callback.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MacHidNative
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal const int KIOReturnSuccess = 0;
    internal const uint KIOHIDOptionsTypeNone = 0;
    internal const uint KIOHIDReportTypeInput = 0;
    internal const uint KIOHIDReportTypeOutput = 1;
    internal const uint KIOHIDReportTypeFeature = 2;

    private const uint KCFStringEncodingUtf8 = 0x08000100;
    private const int KCFNumberIntType = 9;

    /// <summary>
    /// IOHIDReportCallback: void (*)(void* context, IOReturn result,
    /// void* sender, IOHIDReportType type, uint32_t reportID,
    /// uint8_t* report, CFIndex reportLength).
    /// </summary>
    internal unsafe delegate void IOHIDReportCallback(
        nint context,
        int result,
        nint sender,
        uint type,
        uint reportId,
        byte* report,
        nint reportLength);

    // ---- CoreFoundation ----

    [LibraryImport(CoreFoundation)]
    internal static partial void CFRelease(nint cf);

    [LibraryImport(CoreFoundation)]
    internal static partial nint CFStringCreateWithCString(nint allocator, nint cString, uint encoding);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CFStringGetCString(nint theString, nint buffer, nint bufferSize, uint encoding);

    [LibraryImport(CoreFoundation)]
    internal static partial nint CFNumberCreate(nint allocator, nint theType, nint valuePtr);

    [LibraryImport(CoreFoundation)]
    internal static partial nint CFDictionaryCreateMutable(
        nint allocator,
        nint capacity,
        nint keyCallBacks,
        nint valueCallBacks);

    [LibraryImport(CoreFoundation)]
    internal static partial void CFDictionarySetValue(nint theDict, nint key, nint value);

    [LibraryImport(CoreFoundation)]
    internal static partial nint CFArrayCreate(nint allocator, nint values, nint numValues, nint callBacks);

    [LibraryImport(CoreFoundation)]
    internal static partial nint CFSetGetCount(nint theSet);

    [LibraryImport(CoreFoundation)]
    internal static partial void CFSetGetValues(nint theSet, nint values);

    [LibraryImport(CoreFoundation)]
    internal static partial nint CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation)]
    internal static partial void CFRunLoopStop(nint runLoop);

    [LibraryImport(CoreFoundation)]
    internal static partial int CFRunLoopRunInMode(nint mode, double seconds, [MarshalAs(UnmanagedType.Bool)] bool returnAfterSourceHandled);

    // ---- IOKit HID ----

    [LibraryImport(IOKit)]
    internal static partial nint IOHIDManagerCreate(nint allocator, uint options);

    [LibraryImport(IOKit)]
    internal static partial void IOHIDManagerSetDeviceMatchingMultiple(nint manager, nint multiple);

    [LibraryImport(IOKit)]
    internal static partial int IOHIDManagerOpen(nint manager, uint options);

    [LibraryImport(IOKit)]
    internal static partial int IOHIDManagerClose(nint manager, uint options);

    [LibraryImport(IOKit)]
    internal static partial nint IOHIDManagerCopyDevices(nint manager);

    [LibraryImport(IOKit)]
    internal static partial int IOHIDDeviceOpen(nint device, uint options);

    [LibraryImport(IOKit)]
    internal static partial int IOHIDDeviceClose(nint device, uint options);

    [LibraryImport(IOKit)]
    internal static partial nint IOHIDDeviceGetProperty(nint device, nint key);

    [LibraryImport(IOKit)]
    internal static partial int IOHIDDeviceSetReport(nint device, uint reportType, nint reportId, nint report, nint reportLength);

    [LibraryImport(IOKit)]
    internal static partial int IOHIDDeviceGetReport(nint device, uint reportType, nint reportId, nint report, ref nint reportLength);

    [LibraryImport(IOKit)]
    internal static partial void IOHIDDeviceRegisterInputReportCallback(
        nint device,
        nint report,
        nint reportLength,
        nint callback,
        nint context);

    [LibraryImport(IOKit)]
    internal static partial void IOHIDDeviceScheduleWithRunLoop(nint device, nint runLoop, nint runLoopMode);

    [LibraryImport(IOKit)]
    internal static partial void IOHIDDeviceUnscheduleFromRunLoop(nint device, nint runLoop, nint runLoopMode);

    // ---- CoreFoundation globals ----
    //
    // These are exported data symbols, not functions, so they are resolved by
    // address. The callback-table symbols are passed by address; the run-loop
    // mode is a CFStringRef variable and must be dereferenced.

    private static readonly Lazy<nint> CoreFoundationHandle = new(() =>
        NativeLibrary.Load(CoreFoundation));

    private static nint GetSymbol(string name) =>
        NativeLibrary.TryGetExport(CoreFoundationHandle.Value, name, out var address) ? address : 0;

    internal static nint KCFTypeDictionaryKeyCallBacks { get; } = GetSymbol("kCFTypeDictionaryKeyCallBacks");

    internal static nint KCFTypeDictionaryValueCallBacks { get; } = GetSymbol("kCFTypeDictionaryValueCallBacks");

    internal static nint KCFTypeArrayCallBacks { get; } = GetSymbol("kCFTypeArrayCallBacks");

    internal static unsafe nint KCFRunLoopDefaultMode
    {
        get
        {
            var address = GetSymbol("kCFRunLoopDefaultMode");
            return address == 0 ? 0 : *(nint*)address;
        }
    }

    /// <summary>Creates a CFString from a managed string; the caller releases it.</summary>
    internal static nint CreateCFString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return CFStringCreateWithCString(0, utf8, KCFStringEncodingUtf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>Reads a CFString property into a managed string, or null.</summary>
    internal static string? ReadCFString(nint cfString)
    {
        if (cfString == 0)
        {
            return null;
        }

        const int bufferSize = 256;
        var buffer = Marshal.AllocCoTaskMem(bufferSize);
        try
        {
            return CFStringGetCString(cfString, buffer, bufferSize, KCFStringEncodingUtf8)
                ? Marshal.PtrToStringUTF8(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>Creates a CFNumber holding a 32-bit int; the caller releases it.</summary>
    internal static unsafe nint CreateCFNumber(int value)
    {
        return CFNumberCreate(0, KCFNumberIntType, (nint)(&value));
    }
}
