// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using PlanImageResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Tests.VideoOut;

// The image binding memo of a descriptor that names no memory: the placeholder it keeps, and its switch.
public sealed unsafe partial class RenderHostDeviceTests
{
    private const BindingFlags HostMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly ImageResource SampledImage = new()
    {
        ResourceClass = PlanImageResourceClass.Sampled,
        NumericClass = ImageNumericClass.Float,
        Dimension = ImageDimension.Dim2D,
        Read = true,
    };

    private static long HostCounter(PresenterUnderTest presenter, string name) =>
        (long)presenter.Recording.GetType().GetProperty(name, HostMembers)!.GetValue(presenter.Recording)!;

    // Prepares and binds one compute stage with one image and returns the image it bound. Each shader base misses the
    // stage preparation of the others, so the image is resolved again through its binding memo.
    private static ResourceSlotIdentifier BindOneImage(PresenterUnderTest presenter, ShaderProgramInfo program, uint[] words, ulong shaderBase) =>
        presenter.Run(() =>
        {
            presenter.RenderHost.ResetBindings();
            using var preparation = presenter.RenderHost.BeginPreparation();
            var prepared = presenter.RenderHost.PrepareBindings(
                new ShaderStageResources(program, new ResourceSnapshot { Images = [words] }, shaderBase));
            presenter.RenderHost.BindResources(prepared);
            var textures = (Array)prepared.GetType().GetProperty("Textures", HostMembers)!.GetValue(prepared)!;
            var texture = textures.GetValue(0)!;
            return (ResourceSlotIdentifier)texture.GetType().GetField("ImageIdentifier", HostMembers)!.GetValue(texture)!;
        });

    // The image cache looks a placeholder up after completing the publications over its empty range.
    private static Func<int> CountNullLookups(PresenterUnderTest presenter)
    {
        var lookups = 0;
        var publications = presenter.Harness.Cache.CompleteGuestPublications!;
        presenter.Harness.Cache.CompleteGuestPublications = (address, size, access) =>
        {
            lookups += address == 0 && size == 0 ? 1 : 0;
            publications(address, size, access);
        };
        return () => lookups;
    }

    private static object[] KeptNullBindings(PresenterUnderTest presenter) =>
        presenter.GetField<IDictionary>("_imageBindingMemos").Values.Cast<object>()
            .Where(memo => (bool)memo.GetType().GetField("NullImage")!.GetValue(memo)!).ToArray();

    [Theory]
    [InlineData("On", 1)]
    [InlineData("Verify", 3)]
    [InlineData("Off", 3)]
    public void NullImageMemo_ADescriptorThatNamesNoMemoryKeepsItsPlaceholder(string mode, int lookups)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.LoadRenderingCommands();
        presenter.SetField("NullImageMemo", Enum.Parse<VulkanVideoPresenter.NullImageMemoMode>(mode));
        var nullLookups = CountNullLookups(presenter);
        var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 0xB3, new ShaderResourceInfo { Images = [SampledImage] });
        var words = new uint[8];

        var first = BindOneImage(presenter, program, words, 0x1000);
        var second = BindOneImage(presenter, program, words, 0x2000);
        var third = BindOneImage(presenter, program, words, 0x3000);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(lookups, nullLookups());
        Assert.Equal(mode == "On" ? 2 : 0, HostCounter(presenter, "NullImageHits"));
        Assert.Equal(mode == "Verify" ? 2 : 0, HostCounter(presenter, "NullImageVerifications"));
        Assert.Equal(0, HostCounter(presenter, "NullImageMismatches"));
        var kept = KeptNullBindings(presenter);
        if (mode == "Off")
        {
            Assert.Empty(kept);
        }
        else
        {
            var memo = Assert.Single(kept);
            Assert.Equal(first, (ResourceSlotIdentifier)memo.GetType().GetField("Found")!.GetValue(memo)!);
            Assert.False((bool)memo.GetType().GetField("CompletesLookup")!.GetValue(memo)!);
        }

        Assert.Empty(fatal.Messages);
        presenter.Harness.Shutdown();
    }

    // Images created and removed elsewhere leave a kept placeholder valid: it is still served after the lookup generation
    // moved.
    [Fact]
    public void NullImageMemo_AKeptPlaceholderOutlivesImagesCreatedElsewhere()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.LoadRenderingCommands();
        presenter.SetField("NullImageMemo", VulkanVideoPresenter.NullImageMemoMode.On);
        var nullLookups = CountNullLookups(presenter);
        var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 0xB4, new ShaderResourceInfo { Images = [SampledImage] });
        var words = new uint[8];

        var first = BindOneImage(presenter, program, words, 0x1000);
        var generation = presenter.Run(() => presenter.Harness.Images.LookupGeneration);
        var address = presenter.Harness.MapBacked(0x10000, ImageCacheTestSupport.ReadWrite);
        var request = ImageCacheTestSupport.Color32(address, 16);
        _ = presenter.Harness.Find(ref request);
        Assert.NotEqual(generation, presenter.Run(() => presenter.Harness.Images.LookupGeneration));

        var second = BindOneImage(presenter, program, words, 0x2000);
        Assert.Equal(first, second);
        Assert.Equal(1, nullLookups());
        Assert.Equal(1, HostCounter(presenter, "NullImageHits"));
        Assert.Empty(fatal.Messages);
        presenter.Harness.Shutdown();
    }

    // A kept placeholder that differs from the lookup's image is counted in verify mode, and the lookup's image is bound.
    [Fact]
    public void NullImageMemo_VerifyCountsAPlaceholderTheLookupDoesNotGive()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.LoadRenderingCommands();
        presenter.SetField("NullImageMemo", VulkanVideoPresenter.NullImageMemoMode.Verify);
        var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 0xB5, new ShaderResourceInfo { Images = [SampledImage] });
        var words = new uint[8];

        var first = BindOneImage(presenter, program, words, 0x1000);
        presenter.Run(() =>
        {
            var memos = presenter.GetField<IDictionary>("_imageBindingMemos");
            var key = memos.Keys.Cast<object>().Single();
            var memo = memos[key]!;
            memo.GetType().GetField("Found")!.SetValue(memo, new ResourceSlotIdentifier(first.Index + 1000, first.Generation));
            memos[key] = memo;
        });

        var second = BindOneImage(presenter, program, words, 0x2000);
        Assert.Equal(first, second);
        Assert.Equal(1, HostCounter(presenter, "NullImageVerifications"));
        Assert.Equal(1, HostCounter(presenter, "NullImageMismatches"));
        presenter.Harness.Shutdown();
    }
}
