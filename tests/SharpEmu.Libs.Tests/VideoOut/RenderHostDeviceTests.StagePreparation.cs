// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Fact]
    public void StagePreparationsPromoteReusedEntriesAndInvalidateExpiredEntries()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 0xB6,
            new ShaderResourceInfo { Images = [SampledImage] });
        var words = new uint[8];
        void Bind(ulong address) => BindOneImage(presenter, program, words, address);
        void Rotate() => presenter.SetField("_memoAgedAt", Environment.TickCount64 - 30_000);
        Bind(0x1000);
        var set = presenter.GetField<IDictionary>("_stagePreparations")[program]!;
        var last = set.GetType().GetField("Last")!;
        var expired = last.GetValue(set)!;
        Bind(0x2000);
        var promoted = last.GetValue(set)!;
        Bind(0x3000);
        Rotate();
        Bind(0x2000);
        Assert.Same(promoted, last.GetValue(set));
        var valid = promoted.GetType().GetField("Valid")!;
        Assert.True((bool)valid.GetValue(expired)!);
        Rotate();
        Bind(0x2000);
        Assert.False((bool)valid.GetValue(expired)!);
        Assert.True((bool)valid.GetValue(promoted)!);
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void StagePreparation_RecapturedBindingsSurviveSubmissionRecycling()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.LoadRenderingCommands();
        var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 0xB6,
            new ShaderResourceInfo { Images = [SampledImage] });
        var words = new uint[8];
        var first = BindOneImage(presenter, program, words, 0x1000);
        Assert.Equal(first, BindOneImage(presenter, program, words, 0x1000));

        var set = presenter.GetField<IDictionary>("_stagePreparations")[program]!;
        var memo = set.GetType().GetField("Last")!.GetValue(set)!;
        var texture = ((Array)memo.GetType().GetField("Textures")!.GetValue(memo)!).GetValue(0)!;
        presenter.Run(() =>
        {
            // Feedback invalidates a memo; completing its submission then resets its binding.
            memo.GetType().GetMethod("Invalidate")!.Invoke(memo, null);
            presenter.InvokeMethod("ReturnTextureResource", texture);
        });

        Assert.Equal(first, BindOneImage(presenter, program, words, 0x1000));
        Assert.Equal(first, BindOneImage(presenter, program, words, 0x1000));

        // An older submission can still return this same binding after the memo was recaptured.
        presenter.Run(() => presenter.InvokeMethod("ReturnTextureResource", texture));
        var proven = PresenterUnderTest.RecordingHostType.GetField("_stageMemoProven", BindingFlags.Static | BindingFlags.NonPublic)!;
        var before = (long)proven.GetValue(null)!;
        Assert.Equal(first, BindOneImage(presenter, program, words, 0x1000));
        Assert.Equal(before + 1, (long)proven.GetValue(null)!);
        Assert.Empty(fatal.Messages);
        presenter.Harness.Shutdown();
    }

    // A stage that reads the bindless heap keeps its preparation like any other stage, and a reuse carries the heap views
    // this draw named into the shader data.
    [Fact]
    public void StagePreparation_ABindlessHeapStageIsReusedWithItsHeapViews()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.LoadRenderingCommands();
        // Stand-ins for the heap set (the test device has no descriptor indexing) and a feedback copy in flight, so preparing
        // the heap only reads its views.
        presenter.SetField("_heapSet", new Silk.NET.Vulkan.DescriptorSet(1));
        presenter.SetField("_heapFeedbackCopyPending", 1);
        foreach (var name in new[] { "_heapChangedImages", "_heapRegionEntries", "_callRecordEntries" })
        {
            presenter.SetField(name, Activator.CreateInstance(PresenterUnderTest.PresenterType.GetField(name, HostMembers)!.FieldType)!);
        }

        presenter.SetField("_heapImageViews", new SharpEmu.Libs.VideoOut.BindlessHeapViewTable(BindlessHeap.CallRecordFirstIndex));
        try
        {
            var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 0xB7,
                new ShaderResourceInfo { Images = [SampledImage], UsesBindlessHeap = true, HeapViewCount = 1 });
            var layout = program.Bindings!;
            var snapshot = new ResourceSnapshot
            {
                Images = [new uint[8]],
                Heaps = [new BindlessHeapDescriptor(false, 0x4000_0000, 64, BindlessHeap.ImageRecordBytes)],
            };
            (bool Memoized, uint[] Views) Prepare() => presenter.Run(() =>
            {
                presenter.RenderHost.ResetBindings();
                using var preparation = presenter.RenderHost.BeginPreparation();
                var prepared = presenter.RenderHost.PrepareBindings(new ShaderStageResources(program, snapshot, 0x1000));
                presenter.RenderHost.BindResources(prepared);
                var shaderData = (uint[])prepared.GetType().GetProperty("ShaderData", HostMembers)!.GetValue(prepared)!;
                return ((bool)prepared.GetType().GetProperty("Memoized", HostMembers)!.GetValue(prepared)!,
                    shaderData.AsSpan((int)layout.HeapViewsDword, (int)HeapView.DwordCount).ToArray());
            });

            var first = Prepare();
            var second = Prepare();
            Assert.False(first.Memoized);
            Assert.NotEqual([HeapView.None.Word0, HeapView.None.Word1], first.Views);
            Assert.True(second.Memoized);
            Assert.Equal(first.Views, second.Views);
            Assert.Empty(fatal.Messages);
        }
        finally
        {
            presenter.SetField("_heapSet", default(Silk.NET.Vulkan.DescriptorSet));
            presenter.SetField("_heapFeedbackCopyPending", 0);
        }

        presenter.Harness.Shutdown();
    }
}
