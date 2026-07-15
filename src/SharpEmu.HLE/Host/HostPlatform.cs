// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host.Posix;
using SharpEmu.HLE.Host.Windows;

namespace SharpEmu.HLE.Host;

/// <summary>
/// Process-wide access point for the host platform backend. Static HLE export
/// classes (which cannot receive constructor injection) resolve host primitives
/// through <see cref="Current"/>; injectable components should instead accept an
/// <see cref="IHostPlatform"/> and merely default to this.
/// </summary>
public static class HostPlatform
{
    private static readonly Lazy<IHostPlatform> Instance = new(Create);

    public static IHostPlatform Current => Instance.Value;

    private static IHostPlatform Create()
    {
        // The Windows backend executes guest x86-64 natively and emits x86-64
        // stubs, so a native ARM64 process must be rejected here rather than
        // crash undefined later (x64 processes under emulation report X64).
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return new WindowsHostPlatform();
        }

        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return new PosixHostPlatform();
        }

        throw new PlatformNotSupportedException(
            "SharpEmu native guest execution requires an x86-64 process on Windows, Linux, or macOS. " +
            "On Apple Silicon, use the osx-x64 build under Rosetta 2.");
    }
}
