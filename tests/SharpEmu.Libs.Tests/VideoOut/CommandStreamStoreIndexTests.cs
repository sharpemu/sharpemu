// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class CommandStreamStoreIndexTests : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly Type CommandThreadType = typeof(VulkanVideoPresenter).GetNestedType("CommandThread", BindingFlags.NonPublic)!;
    private readonly HeadlessVulkan? _vulkan;

    public CommandStreamStoreIndexTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    internal static object NewThread(PresenterUnderTest presenter) =>
        Activator.CreateInstance(CommandThreadType, PresenterUnderTest.InstanceMembers, null,
            [presenter.Instance, null, presenter.Harness.Memory, null, null], null)!;

    internal static object[] Stores(object thread) =>
        ((IEnumerable)CommandThreadType.GetField("_pendingWrites", PresenterUnderTest.InstanceMembers)!.GetValue(thread)!).Cast<object>().ToArray();

    internal static void Land(PresenterUnderTest presenter, object thread)
    {
        var execute = CommandThreadType.GetMethod("Execute", PresenterUnderTest.InstanceMembers)!;
        foreach (var store in Stores(thread))
        {
            presenter.Harness.Worker.Run(() => execute.Invoke(thread, [store]));
        }
    }

    // A small store before a large one and a small store after it on one page: the overlay applies the three in order, and
    // the index is empty once they land.
    [Fact]
    public void TheOverlayAppliesSmallAndLargeStoresOnOnePageInOrder()
    {
        if (_vulkan is null || !OperatingSystem.IsWindows())
            return;

        using var presenter = new PresenterUnderTest(_vulkan);
        var address = presenter.Harness.MapBacked(0x40000, GuestPageProtection.Read | GuestPageProtection.Write);
        var thread = NewThread(presenter);
        var post = CommandThreadType.GetMethod("PostOwnedWrite")!;
        var large = new byte[20 * 0x1000];
        Array.Fill(large, (byte)2);
        post.Invoke(thread, [address + 0x100, new byte[] { 1, 1, 1, 1 }, false]);
        post.Invoke(thread, [address, large, false]);
        post.Invoke(thread, [address + 0x104, new byte[] { 3, 3, 3, 3 }, false]);

        var expected = new byte[] { 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3 };
        var read = (byte[])CommandThreadType.GetMethod("ReadOverlayForTest", PresenterUnderTest.InstanceMembers)!
            .Invoke(thread, [address + 0xFC, expected.Length])!;
        Assert.Equal(expected, read);

        Land(presenter, thread);
        var index = (PendingStorePages)CommandThreadType.GetField("_storePages", PresenterUnderTest.InstanceMembers)!.GetValue(thread)!;
        Assert.True(index.IsEmpty);
        Assert.Empty(Stores(thread));
        Assert.Equal(expected, presenter.Harness.Read(address + 0xFC, expected.Length));
    }
}
