// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class SliceRecordingTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public SliceRecordingTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void UnresolvedDrawsReachTheJoinerInOrderWithoutAllocatingRecordingBuffers()
    {
        var presenter = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.PresenterType);
        var translation = (AgcExports.CommandStreamTranslation)RuntimeHelpers.GetUninitializedObject(typeof(AgcExports.CommandStreamTranslation));
        PresenterUnderTest.PresenterType.GetField("_translation", PresenterUnderTest.InstanceMembers)!.SetValue(presenter, translation);
        var host = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
        SetHostField(host, "_presenter", presenter);
        SetHostField(host, "_bufferCache", RuntimeHelpers.GetUninitializedObject(typeof(GuestBufferCache)));
        SetHostField(host, "_arena", new SliceArena(null!, 1));
        var slice = Activator.CreateInstance(SliceType, nonPublic: true)!;
        var items = (IList)SliceType.GetField("Items")!.GetValue(slice)!;
        var itemType = items.GetType().GetGenericArguments()[0];
        for (var index = 0; index < 32; index++)
        {
            var work = new AgcExports.DeferredDraw { SerialReason = "pending write read" };
            work.Done.Set();
            var item = Activator.CreateInstance(itemType, nonPublic: true)!;
            itemType.GetField("Work")!.SetValue(item, work);
            items.Add(item);
        }

        PresenterUnderTest.RecordingHostType.GetMethod("RecordSlice", PresenterUnderTest.InstanceMembers)!.Invoke(host, [slice]);

        var segments = SliceType.GetField("Segments")!.GetValue(slice)!;
        var dequeue = segments.GetType().GetMethod("TryDequeue")!;
        for (var index = 0; index < items.Count; index++)
        {
            object?[] result = [null];
            Assert.True((bool)dequeue.Invoke(segments, result)!);
            var segment = result[0]!;
            Assert.Equal(index, SegmentType.GetField("First")!.GetValue(segment));
            Assert.Equal(0, SegmentType.GetField("Count")!.GetValue(segment));
            Assert.Equal("unresolved draw", SegmentType.GetField("SerialReason")!.GetValue(segment));
            Assert.Null(SegmentType.GetField("Buffer")!.GetValue(segment));
            Assert.False((bool)PresenterUnderTest.PresenterType.GetMethod("JoinSegment", PresenterUnderTest.InstanceMembers)!.Invoke(presenter, [slice, segment])!);
        }

        Assert.Equal(items.Count, SliceType.GetField("Resume")!.GetValue(slice));
        Assert.Null(PresenterUnderTest.RecordingHostType.GetField("_workerBuffer", PresenterUnderTest.InstanceMembers)!.GetValue(host));
        Assert.Empty((IEnumerable)SliceType.GetField("Chunks")!.GetValue(slice)!);
        Assert.Empty((IEnumerable)segments);
    }

    [Fact]
    public void DeferredReadinessPropagatesWorkerFailures()
    {
        var translation = (AgcExports.CommandStreamTranslation)RuntimeHelpers.GetUninitializedObject(typeof(AgcExports.CommandStreamTranslation));
        var failure = new InvalidOperationException("worker failed");
        var work = new AgcExports.DeferredDraw { Failure = failure };
        work.Done.Set();

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => translation.CanRecordDeferred(work)).InnerException);
    }

    [Fact]
    public void GnmImagePreparationRefusesARecordingWorkerBeforeTouchingGpuStateWhileATextureAwaitsItsDetile()
    {
        const ulong Source = 0x1_0000_0000, Scratch = 0x1_0000_8000;
        var host = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
        SetHostField(host, "_worker", true);
        var frontend = new GcnShaderFrontend(new FakeCpuMemory(Source, 0x10000), _ => Scratch);
        uint[] words = [(uint)(Source >> 8), (4u << 20) | (7u << 26), 7u | (7u << 14),
            (9u << 28) | (13u << 20) | 0xFAC, 7u << 13, 0, 0, 0];
        var reserved = frontend.NormalizeImageForResolution(words, ShaderStage.Pixel, 123, "test-reserve", true);
        Assert.False(frontend.IsTexturePrepared(reserved, ordered: false, out var source, out var sourceBytes));
        Assert.Equal(Source, source);
        Assert.NotEqual(0UL, sourceBytes);
        Assert.True(frontend.IsTexturePrepared(new uint[8], ordered: false, out _, out var nullBytes));
        Assert.Equal(0UL, nullBytes);
        var program = new ShaderProgramInfo
        {
            Frontend = frontend,
            Resources = new SpecializedResourceInfo(),
            Bindings = new BindingLayout(),
        };
        var stage = new ShaderStageResources(program, new ResourceSnapshot { Images = [reserved] });

        var exception = Assert.Throws<SerialRequiredException>(() => ((IRenderHost)host).PrepareBindings(stage));

        Assert.Equal("frontend image preparation", exception.Message);
        Assert.True(exception.Resumable);
    }

    [Fact]
    public void SliceHazards_FirstAccessNeedsNoBarrierAndRepeatedShaderWritesDo()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var image = harness.CreateImage(CachedImageTests.Color2D(16, 16));
        var hazards = new SliceHazards();

        var first = hazards.Transition(image, AccessFlags.ShaderWriteBit, PipelineStageFlags.ComputeShaderBit, out var firstStages);
        Assert.Empty(first);
        Assert.Equal(PipelineStageFlags.None, firstStages);

        var repeated = hazards.Transition(image, AccessFlags.ShaderWriteBit, PipelineStageFlags.ComputeShaderBit, out var repeatedStages);
        var barrier = Assert.Single(repeated);
        Assert.Equal((ImageLayout.General, ImageLayout.General), (barrier.OldLayout, barrier.NewLayout));
        Assert.Equal(image.Backing.Handle.Handle, barrier.Image.Handle);
        Assert.Equal(PipelineStageFlags.ComputeShaderBit, repeatedStages);

        var read = hazards.Transition(image, AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit, out _);
        Assert.Single(read);
        var sameRead = hazards.Transition(image, AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit, out _);
        Assert.Empty(sameRead);

        hazards.Reset();
        Assert.Empty(hazards.Transition(image, AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit, out _));
    }

    [Fact]
    public void SliceHazards_ReacquiringAKeptAttachmentNeedsNoBarrierButAnotherAccessorDoes()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var image = harness.CreateImage(CachedImageTests.Color2D(16, 16));
        var hazards = new SliceHazards();
        const AccessFlags Color = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;

        Assert.Empty(hazards.Transition(image, Color, PipelineStageFlags.ColorAttachmentOutputBit, out _));
        Assert.Empty(hazards.Transition(image, Color, PipelineStageFlags.ColorAttachmentOutputBit, out _));
        Assert.Empty(hazards.Transition(image, Color, PipelineStageFlags.ColorAttachmentOutputBit, out _));

        var sampled = Assert.Single(hazards.Transition(image, AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit, out var sampledStages));
        Assert.Equal(PipelineStageFlags.ColorAttachmentOutputBit, sampledStages);
        Assert.Equal(AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit, sampled.SrcAccessMask);

        Assert.Single(hazards.Transition(image, Color, PipelineStageFlags.ColorAttachmentOutputBit, out _));
        Assert.Single(hazards.Transition(image, AccessFlags.ShaderWriteBit | Color, PipelineStageFlags.FragmentShaderBit, out _));
        Assert.Single(hazards.Transition(image, AccessFlags.ShaderWriteBit | Color, PipelineStageFlags.FragmentShaderBit, out _));
    }

    [Fact]
    public void SliceHazards_ReadsAfterReadsMergeUntilTheNextWrite()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var image = harness.CreateImage(CachedImageTests.Color2D(16, 16));
        var hazards = new SliceHazards();

        Assert.Empty(hazards.Transition(image, AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit, out _));
        Assert.Empty(hazards.Transition(image, AccessFlags.TransferReadBit, PipelineStageFlags.TransferBit, out _));
        Assert.Empty(hazards.Transition(image, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit, PipelineStageFlags.ComputeShaderBit, out _));

        var write = Assert.Single(hazards.Transition(image, AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit, out var writeStages));
        Assert.Equal(PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit | PipelineStageFlags.ComputeShaderBit, writeStages);
        Assert.Equal(AccessFlags2.ShaderReadBit | AccessFlags2.TransferReadBit, write.SrcAccessMask);
        Assert.Single(hazards.Transition(image, AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit, out _));
    }

    [Fact]
    public void SliceHazards_RepeatedGuestStorageWritesNeedNoBarrier()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var image = harness.CreateImage(CachedImageTests.Color2D(16, 16));
        var hazards = new SliceHazards();
        const AccessFlags Storage = ImageAccessState.GuestStorageAccess;

        Assert.Empty(hazards.Transition(image, Storage, PipelineStageFlags.ComputeShaderBit, out _, guest: true));
        Assert.Empty(hazards.Transition(image, Storage, PipelineStageFlags.FragmentShaderBit, out _, guest: true));
        Assert.Single(hazards.Transition(image, AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit, out var readStages));
        Assert.Equal(PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit, readStages);

        Assert.Single(hazards.Transition(image, Storage, PipelineStageFlags.ComputeShaderBit, out _));
        Assert.Single(hazards.Transition(image, Storage, PipelineStageFlags.ComputeShaderBit, out _, guest: true));
    }

    [Fact]
    public void SliceHazards_StagesFollowTheAccess()
    {
        Assert.Equal(PipelineStageFlags.TransferBit, SliceHazards.StagesOf(AccessFlags.TransferWriteBit));
        Assert.Equal(PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit, SliceHazards.StagesOf(AccessFlags.ShaderReadBit));
        Assert.Equal(PipelineStageFlags.TransferBit | PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
            SliceHazards.StagesOf(AccessFlags.TransferReadBit | AccessFlags.ShaderReadBit));
    }

    [Fact]
    public void SliceArena_PacksAlignedReservationsAndHandsItsChunksOver()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        harness.Run(() =>
        {
            using var pool = new SliceArenaPool(harness.Device, harness.Scheduler);
            var arena = new SliceArena(pool, harness.Device.NonCoherentAtomSize);

            Assert.True(arena.TryReserve(100, 256, out var first));
            Assert.True(arena.TryReserve(100, 256, out var second));
            Assert.Same(first.Chunk, second.Chunk);
            Assert.Equal(0UL, first.Offset);
            Assert.Equal(0UL, second.Offset % 256);
            Assert.True(second.Offset >= 100);
            Assert.Equal(100, first.Destination.Length);

            Assert.True(arena.TryUpload(new byte[] { 1, 2, 3, 4 }, 16, out var buffer, out var offset));
            Assert.Equal(first.Buffer.Handle, buffer.Handle);
            Assert.Equal(0UL, offset % 16);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, first.Chunk.Buffer.Mapped.Slice((int)offset, 4).ToArray());

            Assert.True(arena.TryReserve(SliceArenaPool.ChunkBytes, 256, out var large));
            Assert.NotSame(first.Chunk, large.Chunk);
            Assert.Equal(SliceArenaPool.ChunkBytes, large.Size);

            var chunks = arena.TakeChunks();
            Assert.Equal(2, chunks.Count);
            Assert.Empty(arena.TakeChunks());
            Assert.False(arena.TryReserve(0, 1, out _));

            var allocated = pool.ChunkCount;
            foreach (var chunk in chunks)
            {
                pool.Return(chunk);
            }

            Assert.True(arena.TryReserve(8, 1, out var again));
            Assert.Equal(0UL, again.Offset);
            Assert.True(pool.ChunkCount <= allocated);
            arena.TakeChunks().ForEach(pool.Return);
        });
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void BufferSnapshot_FindsTheContainingBufferByBinarySearch()
    {
        var snapshot = new BufferSnapshot(
            [0x1000, 0x5000, 0x9000],
            [0x3000, 0x6000, 0xA000],
            [null!, null!, null!],
            [new ResourceSlotIdentifier(1, 1), new ResourceSlotIdentifier(2, 1), new ResourceSlotIdentifier(3, 1)],
            7);

        Assert.True(snapshot.TryFind(0x1000, 0x2000, out _, out var first));
        Assert.Equal(new ResourceSlotIdentifier(1, 1), first);
        Assert.True(snapshot.TryFind(0x5800, 0x100, out _, out var second));
        Assert.Equal(new ResourceSlotIdentifier(2, 1), second);
        Assert.True(snapshot.TryFind(0x9FFF, 1, out _, out var third));
        Assert.Equal(new ResourceSlotIdentifier(3, 1), third);
        Assert.False(snapshot.TryFind(0x2FFF, 2, out _, out _));
        Assert.False(snapshot.TryFind(0x4000, 0x100, out _, out _));
        Assert.False(snapshot.TryFind(0x0100, 0x100, out _, out _));
        Assert.False(BufferSnapshot.Empty.TryFind(0x1000, 1, out _, out _));
        Assert.Equal(7, snapshot.Serial);
    }

    [Fact]
    public void SpinReadWriteLock_ReadersShareWritersExcludeAndTheWriterMayReadAgain()
    {
        var gate = new SpinReadWriteLock();
        using (gate.Read())
        using (gate.Read())
        {
            Assert.False(gate.IsWriteHeldByCurrentThread);
        }

        using (gate.Hold())
        {
            Assert.True(gate.IsWriteHeldByCurrentThread);
            using (gate.Read())
            {
                Assert.True(gate.IsWriteHeldByCurrentThread);
            }

            var entered = 0;
            var reader = new Thread(() =>
            {
                using (gate.Read())
                {
                    Volatile.Write(ref entered, 1);
                }
            });
            reader.Start();
            Assert.False(SpinWait.SpinUntil(() => Volatile.Read(ref entered) != 0, 50));
        }

        using (gate.Hold())
        {
        }
    }

    [Fact]
    public void CompletionActionsParkWhileSlicesAreCutAndReleaseWhenTheyJoin()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        var ran = new List<string>();

        var generation = scheduler.BeginSliceCut();
        scheduler.QueueCompletionAction(() => ran.Add("parked"));
        scheduler.Flush();
        scheduler.RunCompletedOperations();
        Assert.Empty(ran);

        scheduler.NoteSliceJoined(generation);
        scheduler.RunCompletedOperations();
        Assert.Empty(ran);
        scheduler.Flush();
        scheduler.RunCompletedOperations();
        Assert.Equal(["parked"], ran);

        scheduler.QueueCompletionAction(() => ran.Add("direct"));
        scheduler.Flush();
        scheduler.RunCompletedOperations();
        Assert.Equal(["parked", "direct"], ran);
    }

    [Fact]
    public void JoinSkipsBufferValidationOnlyWhileTheRegistrationsMatchTheSegmentSnapshot()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ImageCacheTestSupport.ReadWrite);
        var page = GuestBufferCache.CachingPageSize;
        var skipped = SliceCounter("_validateBuffersSkipped");
        presenter.Run(() =>
        {
            var first = harness.Cache.ObtainBuffer(address, 4, isWritten: true);
            var snapshot = harness.Cache.RefreshSnapshot();
            var (slice, segment) = NewSegment(presenter, snapshot.Serial, new BufferSliceUse(address, 4, false, false, false, first.Buffer));

            var before = skipped.Total;
            Assert.True(ValidateSegment(presenter, slice, segment));
            Assert.Equal(before + 1, skipped.Total);

            _ = harness.Cache.ObtainBuffer(address + 2 * page, 4, isWritten: true);
            Assert.NotEqual(snapshot.Serial, harness.Cache.RegistrationSerial);
            Assert.True(ValidateSegment(presenter, slice, segment));
            Assert.Equal(before + 1, skipped.Total);

            _ = harness.Cache.FindBuffer(address, 2 * page + 4);
            Assert.False(ValidateSegment(presenter, slice, segment));
            Assert.Equal(before + 1, skipped.Total);
        });
        harness.Shutdown();
    }

    [Fact]
    public void JoinSkipsDeferredValidationOnlyWhenEveryDrawReadUnderTheCurrentVersion()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ImageCacheTestSupport.ReadWrite);
        var skipped = SliceCounter("_validateDeferredSkipped");
        presenter.Run(() =>
        {
            var first = harness.Cache.ObtainBuffer(address, 4, isWritten: true);
            var (slice, segment) = NewSegment(presenter, harness.Cache.RefreshSnapshot().Serial, new BufferSliceUse(address, 4, false, false, false, first.Buffer));
            var requests = SegmentType.GetField("Requests")!.GetValue(segment)!;
            var requestsType = requests.GetType();

            var before = skipped.Total;
            Assert.True(ValidateSegment(presenter, slice, segment));
            Assert.Equal(before + 1, skipped.Total);

            requestsType.GetField("ReadsVersion")!.SetValue(requests, (long?)harness.Cache.GpuModifiedVersion);
            Assert.True(ValidateSegment(presenter, slice, segment));
            Assert.Equal(before + 2, skipped.Total);

            requestsType.GetField("ReadsVersion")!.SetValue(requests, (long?)(harness.Cache.GpuModifiedVersion - 1));
            Assert.True(ValidateSegment(presenter, slice, segment));
            Assert.Equal(before + 2, skipped.Total);

            requestsType.GetField("ReadsVersion")!.SetValue(requests, (long?)harness.Cache.GpuModifiedVersion);
            requestsType.GetField("MixedReadsVersions")!.SetValue(requests, true);
            Assert.True(ValidateSegment(presenter, slice, segment));
            Assert.Equal(before + 2, skipped.Total);
        });
        harness.Shutdown();
    }

    [Fact]
    public void WorkerBufferUseCacheNeverHandsOutABufferAMergeRetired()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ImageCacheTestSupport.ReadWrite);
        var page = GuestBufferCache.CachingPageSize;
        var host = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
        SetHostField(host, "_worker", true);
        SetHostField(host, "_bufferCache", harness.Cache);
        SetHostField(host, "_presenter", presenter.Instance);
        var segment = Activator.CreateInstance(SegmentType, nonPublic: true)!;
        SetHostField(host, "_segment", segment);
        var requests = SegmentType.GetField("Requests")!.GetValue(segment)!;
        var uses = (List<BufferSliceUse>)requests.GetType().GetField("Buffers")!.GetValue(requests)!;
        var hits = HostCounter("_sliceUseHits");
        var stale = HostCounter("_sliceUseStale");
        presenter.Run(() =>
        {
            var first = harness.Cache.ObtainBuffer(address, 4, isWritten: true).Buffer;
            SetHostField(host, "_buffers", harness.Cache.RefreshSnapshot());
            Assert.Same(first, ObtainForSlice(host, address));
            var hitsBefore = hits.Total;
            Assert.Same(first, ObtainForSlice(host, address));
            Assert.Equal(hitsBefore + 1, hits.Total);

            _ = harness.Cache.ObtainBuffer(address + 2 * page, 4, isWritten: true);
            _ = harness.Cache.FindBuffer(address, 2 * page + 4);
            var merged = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, 4));
            Assert.NotSame(first, merged);
            SetHostField(host, "_buffers", harness.Cache.RefreshSnapshot());

            var staleBefore = stale.Total;
            Assert.Same(merged, ObtainForSlice(host, address));
            Assert.Equal(staleBefore + 1, stale.Total);
            Assert.Same(merged, uses[^1].Buffer);
            Assert.Same(merged, ObtainForSlice(host, address));
            Assert.Equal(hitsBefore + 2, hits.Total);
        });
        harness.Shutdown();
    }

    [Fact]
    public void ReadyPresentationYieldsQueuedWorkButShutdownStillDrainsIt()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var threadType = typeof(VulkanVideoPresenter).GetNestedType("CommandThread", BindingFlags.NonPublic)!;
        var thread = Activator.CreateInstance(threadType,
            presenter.Instance, presenter.GetField<SharpEmu.Libs.Gpu.GpuCommands.CommandStreamQueue>("_commandStream"),
            presenter.Harness.Memory, (Action<AgcExports.DeferredWork>)(_ => { }),
            (Func<SharpEmu.Libs.Gpu.GpuCommands.SliceResult>)(() => SharpEmu.Libs.Gpu.GpuCommands.SliceResult.NoWork))!;
        presenter.SetField("_commandThread", thread);
        presenter.SetField("_betweenCommandItems", (Action)(() => { }));
        var ran = 0;
        var post = threadType.GetMethod("Post")!;
        for (var index = 0; index < 513; index++)
            post.Invoke(thread, [(Action)(() => ran++), "test"]);

        var latest = typeof(VulkanVideoPresenter).GetField("_latestPresentation", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = latest.GetValue(null);
        var presentationType = typeof(VulkanVideoPresenter).GetNestedType("Presentation", BindingFlags.NonPublic)!;
        try
        {
            latest.SetValue(null, Activator.CreateInstance(presentationType,
                new byte[4], 1u, 1u, 1L, SharpEmu.ShaderCompiler.GuestDrawKind.None, false, 0UL, 0L, false, 0UL, 0UL, null));
            presenter.Run(() =>
            {
                presenter.InvokeMethod("RunCommandStreamSlices", long.MaxValue, true);
                Assert.Equal(0, ran);
                presenter.InvokeMethod("RunCommandStreamSlices", long.MaxValue, false);
                Assert.Equal(513, ran);
            });
        }
        finally
        {
            latest.SetValue(null, original);
            presenter.SetField("_commandThread", null!);
        }
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void WorkerReadValidation_ReusesScratchAndDetectsChangesPastTheStackBuffer()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ImageCacheTestSupport.ReadWrite);
        var bytes = new byte[4097];
        bytes[^1] = 42;
        Assert.True(harness.Memory.TryWrite(address, bytes));
        var reads = new SharpEmu.Libs.Gpu.GpuCommands.ResolveReadLog { Publications = -1 };
        reads.Add(address, bytes);
        var host = (SharpEmu.Libs.Gpu.GpuCommands.ICommandStreamHost)presenter.Instance;
        presenter.Run(() =>
        {
            Assert.True(host.ValidateWorkerReads(reads));
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 100; index++)
                Assert.True(host.ValidateWorkerReads(reads));
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            bytes[^1]++;
            Assert.True(harness.Memory.TryWrite(address, bytes));
            Assert.False(host.ValidateWorkerReads(reads));
            bytes[^1]--;
            Assert.True(harness.Memory.TryWrite(address, bytes));
            Assert.True(host.ValidateWorkerReads(reads));
        });
        harness.Shutdown();
    }

    [Fact]
    public void SegmentImageUsesAndKeptAttachmentsCompareRequestsByValue()
    {
        var image = new ResourceSlotIdentifier(1, 1);
        var request = TracedColorTarget("descriptor");
        var equal = TracedColorTarget(new string("descriptor".AsSpan()));
        var cleared = TracedColorTarget("descriptor");
        cleared.Description.Metadata.DccClearWord = 1;

        var segment = Activator.CreateInstance(SegmentType, nonPublic: true)!;
        var requests = SegmentType.GetField("Requests")!.GetValue(segment)!;
        var addImage = requests.GetType().GetMethod("AddImage")!;
        foreach (var use in new[] { request, equal, cleared, cleared, request, equal, cleared })
        {
            addImage.Invoke(requests, [new ImageSliceUse(image, null!, use)]);
        }

        Assert.Equal(2, ((List<ImageSliceUse>)requests.GetType().GetField("Images")!.GetValue(requests)!).Count);

        var host = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
        SetHostField(host, "_attachments", new List<(ResourceSlotIdentifier, ImageRequest, ImageView, CachedImage)> { (image, request, default, null!) });
        Assert.True(KeptAttachment(host, image, equal));
        Assert.False(KeptAttachment(host, image, cleared));
        Assert.False(KeptAttachment(host, new ResourceSlotIdentifier(2, 1), equal));
    }

    private static ImageRequest TracedColorTarget(string descriptor) =>
        new(CachedImageTests.Color2D(16, 16), ImageViewDescription.Default, ImageRole.ColorTarget) { TraceTextureDescriptor = descriptor };

    private static bool KeptAttachment(object host, ResourceSlotIdentifier image, ImageRequest request) =>
        (bool)PresenterUnderTest.RecordingHostType.GetMethod("TryKeptAttachment", PresenterUnderTest.InstanceMembers)!
            .Invoke(host, [image, request, null, null])!;

    private static void SetHostField(object host, string name, object value) =>
        PresenterUnderTest.RecordingHostType.GetField(name, PresenterUnderTest.InstanceMembers)!.SetValue(host, value);

    private static ThreadCounter HostCounter(string name) =>
        (ThreadCounter)PresenterUnderTest.RecordingHostType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static GpuBuffer ObtainForSlice(object host, ulong address)
    {
        var obtained = (ValueTuple<GpuBuffer, ulong>)PresenterUnderTest.RecordingHostType.GetMethod("ObtainBufferForSlice", PresenterUnderTest.InstanceMembers)!
            .Invoke(host, [address, 4UL, true, false, false])!;
        Assert.Equal(obtained.Item1.Offset(address), obtained.Item2);
        return obtained.Item1;
    }

    private static readonly Type SliceType = typeof(VulkanVideoPresenter).GetNestedType("RenderSlice", BindingFlags.NonPublic)!;
    private static readonly Type SegmentType = typeof(VulkanVideoPresenter).GetNestedType("SliceSegment", BindingFlags.NonPublic)!;

    private static ThreadCounter SliceCounter(string name) =>
        (ThreadCounter)typeof(VulkanVideoPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static (object Slice, object Segment) NewSegment(PresenterUnderTest presenter, long bufferSerial, BufferSliceUse use)
    {
        var slice = Activator.CreateInstance(SliceType, nonPublic: true)!;
        var segment = Activator.CreateInstance(SegmentType, nonPublic: true)!;
        var requests = SegmentType.GetField("Requests")!.GetValue(segment)!;
        var requestsType = requests.GetType();
        requestsType.GetField("BufferSerial")!.SetValue(requests, bufferSerial);
        requestsType.GetField("LookupGeneration")!.SetValue(requests, presenter.Harness.Images.LookupGeneration);
        requestsType.GetField("MetadataGeneration")!.SetValue(requests, presenter.Harness.Images.MetadataGeneration);
        ((List<BufferSliceUse>)requestsType.GetField("Buffers")!.GetValue(requests)!).Add(use);
        return (slice, segment);
    }

    private static bool ValidateSegment(PresenterUnderTest presenter, object slice, object segment) =>
        (bool)PresenterUnderTest.PresenterType.GetMethod("ValidateSegment", PresenterUnderTest.InstanceMembers)!.Invoke(presenter.Instance, [slice, segment])!;
}
