// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class ShortSliceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateHeadMakesItsSuccessorJoinableWhileTheSecondHalfIsBlocked(bool distant)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(fixture.Vulkan!);
        var host = (ICommandStreamHost)presenter.Instance;
        var translation = AgcExports.CreateCommandStreamTranslation(host.Memory, host);
        presenter.SetField("_translation", translation);
        var label = presenter.Harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        presenter.Run(() =>
        {
            using var cutter = new Cutter(presenter, record: work =>
            {
                if (work is AgcExports.GlobalBarrierWork) host.EmitGlobalBarrier();
                else translation.RunDeferred(work);
            });
            var pool = presenter.GetField<object?>("_workers");
            if (pool is null) return;
            var workers = (int)pool.GetType().GetProperty("Count")!.GetValue(pool)!;
            var width = workers > 1 ? Math.Max(32, 256 / workers) : 256;
            var half = width / 2;
            var split = workers > 1 && !distant;
            var first = new AgcExports.DeferredDispatch { Sequence = 1, Cancelled = true };
            var prefixItems = distant ? half : 0;
            var last = new AgcExports.DeferredDispatch { Sequence = width + prefixItems + half + 1, Cancelled = true };
            try
            {
                cutter.Add(first);
                for (var i = 1; i < width; i++) cutter.Add(AgcExports.GlobalBarrierWork.Instance);
                var prime = Assert.Single(cutter.Cut()).Slice;
                var claimed = prime.GetType().GetField("_claimed", PresenterUnderTest.InstanceMembers)!;
                Assert.True(SpinWait.SpinUntil(() => (int)claimed.GetValue(prime)! != 0, TimeSpan.FromSeconds(5)));
                var unblock = Task.Run(() =>
                {
                    var waited = SpinWait.SpinUntil(() => ReferenceEquals(prime, presenter.GetField<object?>("_segmentWaitSlice")),
                        TimeSpan.FromSeconds(5));
                    first.Done.Set();
                    return waited;
                });
                Assert.Equal(width, cutter.Run());
                Assert.True(unblock.GetAwaiter().GetResult());

                for (var i = 0; i < prefixItems; i++) cutter.Call(static () => { });
                for (var i = 0; i < width; i++)
                {
                    if (i == half) cutter.Add(last);
                    else cutter.Add(AgcExports.GlobalBarrierWork.Instance);
                }
                cutter.Write(label, 7);
                cutter.Call(() =>
                {
                    Assert.Equal(7, presenter.Harness.Read(label, 1)[0]);
                    Assert.Equal(last.Sequence, translation.PendingWrites!.Released);
                });
                var wave = cutter.Cut();
                Assert.Equal(split ? new[] { half, width - half } : [width], wave.Select(s => s.Count));
                var ran = 0;
                if (split)
                {
                    Assert.True(wave[0].Done.Wait(TimeSpan.FromSeconds(5)));
                    Assert.False(wave[1].Done.IsSet);
                    ran = cutter.Run(1);
                    Assert.Equal(half, ran);
                }
                else Assert.False(wave[0].Done.IsSet);
                Assert.Equal(0, presenter.Harness.Read(label, 1)[0]);

                last.Done.Set();
                Assert.True(wave[^1].Done.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(width + prefixItems + 2 - ran, cutter.Run());
            }
            finally
            {
                first.Done.Set();
                last.Done.Set();
                presenter.Harness.Scheduler.Finish();
            }
        });
    }
}
