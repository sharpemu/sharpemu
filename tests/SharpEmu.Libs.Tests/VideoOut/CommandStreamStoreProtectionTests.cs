// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class CommandStreamStoreProtectionTests : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly Type CommandThreadType = typeof(VulkanVideoPresenter).GetNestedType("CommandThread", BindingFlags.NonPublic)!;
    private readonly HeadlessVulkan? _vulkan;

    public CommandStreamStoreProtectionTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    // An end-of-pipe label tells the guest that a GPU copy in front of it is done. Its pages must block CPU reads by the
    // time the label lands, or the guest reads the bytes from before the copy.
    [Fact]
    public void ACommandStreamStoreLandsAfterEarlierGpuCopiesBlockCpuReads()
    {
        if (_vulkan is null || !OperatingSystem.IsWindows())
            return;

        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        harness.Cache.DefersGpuWriteProtection = true;
        var address = harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        harness.Worker.Run(() =>
        {
            harness.Cache.ObtainBuffer(address + 0x4000, 0x1000, isWritten: true);
            harness.Cache.CopyBuffer(address, address + 0x4000, 0x1000, dstGds: false, srcGds: false);
        });
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

        var label = address + 0x8000;
        LandStore(presenter, label);

        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(address));
        Assert.Equal(1, harness.Read(label, 1)[0]);
    }

    // The same holds for a shader write: a guest that sees the label reads the shader's output next, and must not get the
    // bytes from before the write.
    [Fact]
    public void ACommandStreamStoreLandsAfterEarlierShaderWritesBlockCpuReads()
    {
        if (_vulkan is null || !OperatingSystem.IsWindows())
            return;

        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        harness.Cache.DefersGpuWriteProtection = true;
        var address = harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x1000, isWritten: true));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

        var label = address + 0x8000;
        LandStore(presenter, label);

        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(address));
        Assert.Equal(1, harness.Read(label, 1)[0]);
    }

    private static void LandStore(PresenterUnderTest presenter, ulong label)
    {
        var harness = presenter.Harness;
        var thread = Activator.CreateInstance(CommandThreadType, PresenterUnderTest.InstanceMembers, null,
            [presenter.Instance, null, harness.Memory, null, null], null)!;
        CommandThreadType.GetMethod("PostOwnedWrite")!.Invoke(thread, [label, new byte[] { 1, 0, 0, 0 }, false]);
        var stores = (IEnumerable)CommandThreadType.GetField("_pendingWrites", PresenterUnderTest.InstanceMembers)!.GetValue(thread)!;
        var store = Assert.Single(stores.Cast<object>());
        harness.Worker.Run(() => CommandThreadType.GetMethod("Execute", PresenterUnderTest.InstanceMembers)!.Invoke(thread, [store]));
    }
}
