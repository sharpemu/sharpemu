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
}
