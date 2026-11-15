// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Storage image destination selects as shader data (RuntimeImageSwizzle).
public sealed class RuntimeImageSwizzleTests
{
    // Every 12-bit select, permutation or not: the map names, per physical channel, the logical component a baked store puts
    // there (GetImageStoreSourceIndex with all four components enabled), or zero.
    [Fact]
    public void StoreChannelMapPlacesComponentsAsTheBakedStoreForEverySelect()
    {
        for (var select = 0u; select < 0x1000; select++)
        {
            var map = RuntimeImageSwizzle.StoreChannelMap(select);
            Assert.Equal(0u, map >> 12);
            for (var channel = 0; channel < 4; channel++)
            {
                var code = ((map >> (channel * 3)) & 7u) ^ (uint)channel;
                var expected = Gen5ShaderTranslator.GetImageStoreSourceIndex(select, 0xF, channel);
                Assert.Equal(expected < 0 ? 4u : (uint)expected, code);
            }
        }

        Assert.Equal(0u, RuntimeImageSwizzle.StoreChannelMap(DescriptorConstants.IdentityImageSwizzle));
    }

    // Slots go to the written storage images, in image order: not to sampled, unwritten, converted or indirect ones.
    [Fact]
    public void SlotsRankTheWrittenImagesTheRuntimeSelectServes()
    {
        var images = new List<ImageResource>
        {
            Storage(written: true),
            new() { ResourceClass = ImageResourceClass.Sampled },
            Storage(written: false),
            Storage(written: true, conversionFormat: GuestImageFormat.Format11x2x10Uint),
            Storage(written: true, indirectRoot: 0),
            Storage(written: true),
            Storage(written: true),
        };
        Assert.Equal([0, -1, -1, -1, -1, 1, 2], Enumerable.Range(0, images.Count).Select(index => RuntimeImageSwizzle.Slot(images, index)));
        Assert.Equal(-1, RuntimeImageSwizzle.Slot(images, images.Count));
        Assert.Equal(3u, RuntimeImageSwizzle.SlotCount(images));
    }

    [Fact]
    public void WriteStoreMapsPacksTwoMapsToADwordWithTheIdentityForNullDescriptors()
    {
        var layout = new BindingLayout { MemoryOffsetDword = 2, StorageImageSwizzleCount = 3 };
        var shaderData = Enumerable.Repeat(0xFFFF_FFFFu, (int)layout.ShaderDataDwordCount).ToArray();
        uint[] Descriptor(uint select) => [0x1000, 71u << 20, 0, (9u << 28) | select, 0, 0, 0, 0];
        List<ImageResource> images =
            [Storage(written: true), new() { ResourceClass = ImageResourceClass.Sampled }, Storage(written: true), Storage(written: true)];
        RuntimeImageSwizzle.WriteStoreMaps(shaderData, layout, images,
            [Descriptor(0xF2E), Descriptor(0x9F5), new uint[8], Descriptor(0x204)]);
        Assert.Equal(RuntimeImageSwizzle.StoreChannelMap(0xF2E), shaderData[layout.StorageImageSwizzlesDword]);
        Assert.Equal(RuntimeImageSwizzle.StoreChannelMap(0x204), shaderData[layout.StorageImageSwizzlesDword + 1]);
        Assert.Equal(layout.StorageImageSwizzlesDword + 2, layout.ShaderDataDwordCount);
        Assert.Throws<InvalidOperationException>(() => RuntimeImageSwizzle.WriteStoreMaps(shaderData, layout, images.Take(2).ToList(),
            [Descriptor(0xF2E), Descriptor(0x9F5)]));
    }

    [Theory]
    [InlineData("ImageStore", true, 1u)]
    [InlineData("ImageStore", false, 0u)]
    [InlineData("ImageLoad", true, 0u)]
    public void TheLayoutCarriesMapsWhenARuntimeSelectImageIsWritten(string opcode, bool runtimeImageSwizzles, uint expected)
    {
        var (plan, resources) = Materialize(opcode, 0x9F5, runtimeImageSwizzles);
        Assert.Equal(runtimeImageSwizzles, plan.Info.UsesRuntimeImageSwizzles);
        var layout = BindingLayout.Allocate(resources.Info, [], false, false, false);
        Assert.Equal(expected, layout.StorageImageSwizzleCount);
    }

    [Fact]
    public void CanonicalKeepsTheIdentitySelectOfRuntimeSelectImagesOnly()
    {
        var (plan, _) = Materialize("ImageStore", 0xFAC, runtimeImageSwizzles: true);
        var recorded = new ResourceSpecialization
        {
            BaseBufferCount = 0,
            Images = [new ImageSpecialization(ImageNumericClass.Float, ImageDimension.Dim2D, 1, 0, 0x9F5,
                DescriptorConstants.NoIndex, 0, 0, false)],
        };
        var canonical = RuntimeImageSwizzle.Canonical(plan, recorded);
        Assert.Equal(DescriptorConstants.IdentityImageSwizzle, Assert.Single(canonical.Images).ShaderSwizzle);
        Assert.Equal(0x9F5u, Assert.Single(recorded.Images).ShaderSwizzle);

        var converted = new ResourceSpecialization
        {
            Images = [recorded.Images[0] with { ConversionFormat = GuestImageFormat.Format11x2x10Uint }],
        };
        Assert.Same(converted, RuntimeImageSwizzle.Canonical(plan, converted));
        var (bakedPlan, _) = Materialize("ImageStore", 0xFAC, runtimeImageSwizzles: false);
        Assert.Same(recorded, RuntimeImageSwizzle.Canonical(bakedPlan, recorded));
    }

    private static ImageResource Storage(bool written, uint conversionFormat = DescriptorConstants.InvalidFormat,
        uint indirectRoot = DescriptorConstants.NoIndex) => new()
    {
        ResourceClass = ImageResourceClass.Storage,
        Written = written,
        ConversionFormat = conversionFormat,
        IndirectRoot = indirectRoot,
    };

    private static (ShaderResourcePlan Plan, SpecializedResourceInfo Resources) Materialize(string opcode, uint select,
        bool runtimeImageSwizzles)
    {
        var program = Program(Image(0, opcode, resourceRegister: 8), EndProgram(8));
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 16, runtimeImageSwizzles: runtimeImageSwizzles);
        var userData = new uint[16];
        userData[8] = 0x20;
        userData[9] = 71u << 20;
        userData[11] = (9u << 28) | select;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization, out var failure),
            failure.ToString());
        return (plan, ResourceMaterializer.ApplyTo(plan, specialization));
    }
}
