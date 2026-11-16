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

    // An end-of-pipe label tells the guest that the GPU writes in front of it, a copy's or a shader's, are done. Their
    // pages must block CPU reads by the time the label lands, or the guest reads the bytes from before the writes.
    [Fact]
    public void ACommandStreamStoreLandsAfterEarlierGpuWritesBlockCpuReads()
    {
        if (_vulkan is null || !OperatingSystem.IsWindows())
            return;

        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        harness.Cache.DefersGpuWriteProtection = true;
        var address = harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x1000, isWritten: true));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

        var thread = Activator.CreateInstance(CommandThreadType, PresenterUnderTest.InstanceMembers, null,
            [presenter.Instance, null, harness.Memory, null, null], null)!;
        var label = address + 0x8000;
        CommandThreadType.GetMethod("PostOwnedWrite")!.Invoke(thread, [label, new byte[] { 1, 0, 0, 0 }, false]);
        var stores = (IEnumerable)CommandThreadType.GetField("_pendingWrites", PresenterUnderTest.InstanceMembers)!.GetValue(thread)!;
        var store = Assert.Single(stores.Cast<object>());
        harness.Worker.Run(() => CommandThreadType.GetMethod("Execute", PresenterUnderTest.InstanceMembers)!.Invoke(thread, [store]));

        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(address));
        Assert.Equal(1, harness.Read(label, 1)[0]);
    }
}
