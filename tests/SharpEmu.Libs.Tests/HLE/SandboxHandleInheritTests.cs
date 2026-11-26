// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpEmu.HLE.Host.Windows;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

[SupportedOSPlatform("windows")]
public sealed class SandboxHandleInheritTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [Fact]
    public void OutputPipe_IsInheritableOnlyInsideTheScope()
    {
        Assert.True(WindowsAppContainerSandbox.TryCreateOutputPipe(out var read, out var write));
        try
        {
            Assert.False(WindowsAppContainerSandbox.IsInheritable(read));
            Assert.False(WindowsAppContainerSandbox.IsInheritable(write));
            using (WindowsAppContainerSandbox.InheritScope(write))
            {
                Assert.True(WindowsAppContainerSandbox.IsInheritable(write));
                Assert.False(WindowsAppContainerSandbox.IsInheritable(read));
            }

            Assert.False(WindowsAppContainerSandbox.IsInheritable(write));
        }
        finally
        {
            CloseHandle(read);
            CloseHandle(write);
        }
    }

    [Fact]
    public void GamepadBridgeSection_IsInheritableOnlyInsideTheScope()
    {
        using var section = GamepadBridgeSection.Create();

        Assert.False(WindowsAppContainerSandbox.IsInheritable(section.Handle));
        using (WindowsAppContainerSandbox.InheritScope(section.Handle))
        {
            Assert.True(WindowsAppContainerSandbox.IsInheritable(section.Handle));
        }

        Assert.False(WindowsAppContainerSandbox.IsInheritable(section.Handle));
    }

    [Fact]
    public void OpenedFile_IsInheritableOnlyInsideTheScope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpemu-inherit-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "x");
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            var raw = handle.DangerousGetHandle();

            Assert.False(WindowsAppContainerSandbox.IsInheritable(raw));
            using (WindowsAppContainerSandbox.InheritScope(raw))
            {
                Assert.True(WindowsAppContainerSandbox.IsInheritable(raw));
            }

            Assert.False(WindowsAppContainerSandbox.IsInheritable(raw));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Scope_FailsAndRestoresTheOthersWhenAHandleIsInvalid()
    {
        Assert.True(WindowsAppContainerSandbox.TryCreateOutputPipe(out var read, out var write));
        try
        {
            Assert.Throws<InvalidOperationException>(() => WindowsAppContainerSandbox.InheritScope(write, 0x7FFF0));
            Assert.False(WindowsAppContainerSandbox.IsInheritable(write));
        }
        finally
        {
            CloseHandle(read);
            CloseHandle(write);
        }
    }
}
