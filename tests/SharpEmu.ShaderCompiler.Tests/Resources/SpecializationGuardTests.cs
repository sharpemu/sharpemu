// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class SpecializationGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryBufferBitMatchesFullSpecialization(bool formatted)
    {
        var plan = Extract(Program(BufferLoad(0, 0, formatted: formatted), EndProgram(8)), userDataCount: 4);
        uint[] original = [0x8000, 16u << 16, 64, 0xFAC];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(original), ref snapshot, ref specialization));
        var guard = Assert.IsType<ResourceMaterializer.SpecializationGuard>(plan.SpecializationGuard);
        for (var word = 0; word < 4; word++)
        for (var bit = 0; bit < 32; bit++)
        {
            var changed = original.ToArray();
            changed[word] ^= 1u << bit;
            plan.SpecializationGuard = guard;
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(changed), ref snapshot, ref specialization));
            var actual = snapshot;
            var actualSpecialization = specialization;
            plan.SpecializationGuard = null;
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(changed), ref snapshot, ref specialization));
            Assert.Equal(specialization, actualSpecialization);
            Assert.Equal(snapshot.Buffers.SelectMany(words => words), actual.Buffers.SelectMany(words => words));
            Assert.Equal(snapshot.FlattenedResourceTable, actual.FlattenedResourceTable);
        }
    }

    [Fact]
    public void ImageAndSamplerChangesRecheckPayloadsAndAliases()
    {
        var plan = Extract(Program(Image(0, "ImageSample", 0, 16), Image(8, "ImageSample", 8, 16), EndProgram(16)), userDataCount: 20);
        var image = ResourceTrackerTests.ImageDescriptor();
        uint[] original = [.. image, .. image, 0, 0, 0, 0];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(original), ref snapshot, ref specialization));
        var guard = Assert.IsType<ResourceMaterializer.SpecializationGuard>(plan.SpecializationGuard);
        var oldWords = snapshot.Images.SelectMany(words => words).ToArray();
        var previous = snapshot;
        foreach (var word in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 16, 17, 18, 19 })
        {
            var changed = original.ToArray();
            changed[word] ^= 1;
            plan.SpecializationGuard = guard;
            var actualSuccess = ResourceMaterializer.Materialize(plan, Inputs(changed), ref snapshot, ref specialization);
            var actual = snapshot;
            var actualSpecialization = specialization;
            plan.SpecializationGuard = null;
            var expectedSuccess = ResourceMaterializer.Materialize(plan, Inputs(changed), ref snapshot, ref specialization);
            Assert.Equal(expectedSuccess, actualSuccess);
            if (!expectedSuccess) continue;
            Assert.Equal(specialization, actualSpecialization);
            Assert.Equal(snapshot.Images.SelectMany(words => words), actual.Images.SelectMany(words => words));
            Assert.Equal(snapshot.Samplers.SelectMany(words => words), actual.Samplers.SelectMany(words => words));
        }
        Assert.Equal(oldWords, previous.Images.SelectMany(words => words));
    }

    [Fact]
    public void ConcurrentMaterializationsDoNotShareMutableOutputs()
    {
        var plan = Extract(Program(BufferLoad(0, 0), EndProgram(8)), userDataCount: 4);
        Parallel.For(0, 128, index =>
        {
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            uint[] words = [(uint)(0x8000 + index * 4096), (uint)(index % 4) << 16, 64, 0];
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(words), ref snapshot, ref specialization));
            Assert.Equal(words, Assert.Single(snapshot.Buffers));
            Assert.Equal((uint)(index % 4), Assert.Single(specialization.Buffers).PackedStride);
            specialization.Buffers.Clear();
        });
    }
}
