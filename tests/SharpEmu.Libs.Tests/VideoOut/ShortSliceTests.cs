// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class ShortSliceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private sealed record Marker(int Position, bool Dispatch = false) : AgcExports.DeferredWork
    {
        public override bool IsDispatch => Dispatch;
    }

    // Use the normal cutter and consumer. Holding the worker queue makes the steal and
    // the set of slices already dispatched at each stream position deterministic.
    private sealed class Cutter : IDisposable
    {
        private static readonly Type Owner = typeof(VulkanVideoPresenter);
        private static readonly Type WorkerType = Owner.GetNestedType("RenderWorkerPool", BindingFlags.NonPublic)!;
        private static readonly Type ThreadType = Owner.GetNestedType("CommandThread", BindingFlags.NonPublic)!;
        private const BindingFlags Members = PresenterUnderTest.InstanceMembers;
        private readonly PresenterUnderTest _presenter;
        private readonly SemaphoreSlim? _available;
        private readonly object _thread;
        private readonly Func<AgcExports.DeferredWork, bool, long> _enqueue;
        private readonly Action<Action, string> _post;
        private readonly Func<long, int, Action, int> _run;
        internal readonly List<int> Executed = [];

        internal Cutter(PresenterUnderTest presenter, int workers = 4, Action<AgcExports.DeferredWork>? record = null)
        {
            _presenter = presenter;
            if (record is null)
            {
                var pool = Activator.CreateInstance(WorkerType, presenter.Instance, 0)!;
                WorkerType.GetField("_threads", Members)!.SetValue(pool, new Thread[workers]);
                _available = (SemaphoreSlim)WorkerType.GetField("_available", Members)!.GetValue(pool)!;
                presenter.SetField("_workers", pool);
                record = work => Executed.Add(work is Marker marker ? marker.Position : -1);
            }
            else presenter.InvokeMethod("CreateRenderWorkers");
            _thread = Activator.CreateInstance(ThreadType, [presenter.Instance, null, presenter.Harness.Memory, record, null])!;
            _enqueue = ThreadType.GetMethod("EnqueueDeferred")!.CreateDelegate<Func<AgcExports.DeferredWork, bool, long>>(_thread);
            _post = ThreadType.GetMethod("Post")!.CreateDelegate<Action<Action, string>>(_thread);
            _run = ThreadType.GetMethod("RunBatch")!.CreateDelegate<Func<long, int, Action, int>>(_thread);
        }

        internal void Add(int position, bool dispatch = false) => _enqueue(new Marker(position, dispatch), !dispatch);
        internal void Add(AgcExports.DeferredWork work) => _enqueue(work, work is not AgcExports.GlobalBarrierWork && !work.IsDispatch);
        internal void Call(Action action) => _post(action, "test");
        internal void Write(ulong address, byte value) => ThreadType.GetMethod("PostOwnedWrite")!.Invoke(_thread, [address, new[] { value }, false]);
        internal int Run() => _run(long.MaxValue, int.MaxValue, static () => { });
        internal List<(int Count, ManualResetEventSlim Done)> Cut()
        {
            ThreadType.GetMethod("FillUnits", Members)!.Invoke(_thread, null);
            var units = (IList)ThreadType.GetField("_units", Members)!.GetValue(_thread)!;
            var result = new List<(int, ManualResetEventSlim)>();
            foreach (var unit in units)
            {
                if (unit!.GetType().GetField("Slice")!.GetValue(unit) is not { } slice) continue;
                var type = slice.GetType();
                result.Add((((IList)type.GetField("Items")!.GetValue(slice)!).Count,
                    (ManualResetEventSlim)type.GetField("Done")!.GetValue(slice)!));
            }
            return result;
        }

        public void Dispose()
        {
            if (_available is null) _presenter.InvokeMethod("StopRenderWorkers");
            else
            {
                _presenter.SetField("_workers", null!);
                _available.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(2, 0, 0, true, true)]
    [InlineData(1, 2, 0, true, true)]
    [InlineData(0, 4, 0, true, true)]
    [InlineData(0, 3, 4, true, false)]
    [InlineData(0, 0, 7, true, false)]
    [InlineData(2, 0, 0, false, false)]
    public void ShortGroupsNeedUsefulWorkAndAHeadStart(int draws, int dispatches, int barriers, bool earlier, bool eligible)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var presenter = new PresenterUnderTest(fixture.Vulkan!);
        presenter.Run(() =>
        {
            using var cutter = new Cutter(presenter);
            if (earlier) cutter.Call(() => cutter.Executed.Add(-2));
            for (var i = 0; i < draws + dispatches; i++) cutter.Add(i, dispatch: i >= draws);
            for (var i = 0; i < barriers; i++) cutter.Add(AgcExports.GlobalBarrierWork.Instance);
            cutter.Call(() => cutter.Executed.Add(-3));
            var count = draws + dispatches + barriers;
            Assert.Equal(eligible ? new[] { count } : [], cutter.Cut().Select(slice => slice.Count));
            Assert.Equal(count + (earlier ? 2 : 1), cutter.Run());
            List<int> expected = earlier ? [-2] : [];
            expected.AddRange(Enumerable.Range(0, draws + dispatches));
            expected.AddRange(Enumerable.Repeat(-1, barriers));
            expected.Add(-3);
            Assert.Equal(expected, cutter.Executed);
        });
    }

    [Fact]
    public void ClaimedShortGroupJoinsBetweenTheTwoGuestStores()
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
            var inline = 0;
            using var cutter = new Cutter(presenter, record: work =>
            {
                inline++;
                if (work is AgcExports.GlobalBarrierWork) host.EmitGlobalBarrier();
                else translation.RunDeferred(work);
            });
            var enabled = presenter.GetField<object?>("_workers") is not null;
            var type = typeof(AgcExports.CommandStreamTranslation).GetNestedType("ResolvedDrawWork", BindingFlags.NonPublic)!;
            cutter.Write(label, 1);
            cutter.Call(() =>
            {
                Assert.Equal(1, presenter.Harness.Read(label, 1)[0]);
                Assert.True(translation.PendingWrites!.Released < 3);
            });
            cutter.Add((AgcExports.DeferredWork)Activator.CreateInstance(type, new ResolvedDraw { Sequence = 3 })!);
            cutter.Add(AgcExports.GlobalBarrierWork.Instance);
            cutter.Add((AgcExports.DeferredWork)Activator.CreateInstance(type, new ResolvedDraw { Sequence = 5 })!);
            cutter.Call(() =>
            {
                Assert.Equal(1, presenter.Harness.Read(label, 1)[0]);
                Assert.Equal(5L, translation.PendingWrites!.Released);
            });
            cutter.Write(label, 2);
            var slices = cutter.Cut();
            if (enabled)
            {
                var slice = Assert.Single(slices);
                Assert.Equal(3, slice.Count);
                Assert.True(slice.Done.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(0, presenter.Harness.Read(label, 1)[0]);
            }
            else Assert.Empty(slices);
            Assert.Equal(7, cutter.Run());
            Assert.Equal(enabled ? 0 : 3, inline);
            Assert.Equal(2, presenter.Harness.Read(label, 1)[0]);
            presenter.Harness.Scheduler.Finish();
        });
    }

}
