// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// Minimal Win32 HID interop used to talk to a DualSense controller
/// directly, without any external input library.
/// </summary>
internal static partial class WindowsHidNative
{
    internal const int DigcfPresent = 0x02;
    internal const int DigcfDeviceInterface = 0x10;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x1;
    internal const uint FileShareWrite = 0x2;
    internal const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [LibraryImport("hid.dll")]
    internal static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool HidD_GetFeature(SafeFileHandle hidDeviceObject, [In, Out] byte[] reportBuffer, int reportBufferLength);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW")]
    internal static partial nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint hwndParent, int flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet,
        nint deviceInfoData,
        ref Guid interfaceClassGuid,
        int memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        nint deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        nint deviceInfoData);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>
    /// Enumerates the device paths of all present HID interfaces.
    /// </summary>
    internal static List<string> EnumerateHidDevicePaths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out var hidGuid);
        var deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, 0, 0, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == -1 || deviceInfoSet == 0)
        {
            return paths;
        }

        try
        {
            var interfaceData = new SpDeviceInterfaceData
            {
                CbSize = Marshal.SizeOf<SpDeviceInterfaceData>(),
            };

            for (var index = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, 0, ref hidGuid, index, ref interfaceData); index++)
            {
                SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, 0, 0, out var requiredSize, 0);
                if (requiredSize <= 0)
                {
                    continue;
                }

                var detailBuffer = Marshal.AllocHGlobal(requiredSize);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64
                    // (DWORD + aligned WCHAR[1]); the path string follows it.
                    Marshal.WriteInt32(detailBuffer, 8);
                    if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, out _, 0) &&
                        Marshal.PtrToStringUni(detailBuffer + 4) is { Length: > 0 } path)
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return paths;
    }
}
