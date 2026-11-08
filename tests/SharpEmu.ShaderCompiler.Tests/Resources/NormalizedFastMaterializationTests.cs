// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class NormalizedFastMaterializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertedBuffersKeepCurrentAddressesAndDoNotChangePublishedSnapshots(bool tablePhase)
    {
        var plan = ShaderResourcePlan.Extract(Program(BufferLoad(0, 0), EndProgram(8)), ShaderStage.Compute, Hash, 0, 4,
            normalizeBufferDescriptor: words => [words[0], words[1] & 0xFFFF00FF, words[2], 0]);
        Assert.True(ResourceMaterializer.CanMaterializeFast(plan));
        uint[] userData = [0x1000, 0x1234, 256, uint.MaxValue];
        var inputs = new ResourceRuntimeInputs { UserData = userData, TablePhase = tablePhase ? _ => { } : null };
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.MaterializeFast(plan, inputs, ref snapshot, ref specialization, out _));
        var first = snapshot;
        var firstSpecialization = specialization;
        Assert.Equal(new uint[] { 0x1000, 0x34, 256, 0 }, Assert.Single(first.Buffers));
        if (!tablePhase) Assert.True(ResourceMaterializer.RevalidateFast(first));

        userData[0] = 0x2000;
        if (!tablePhase) Assert.False(ResourceMaterializer.RevalidateFast(first));
        Assert.True(ResourceMaterializer.MaterializeFast(plan, inputs, ref snapshot, ref specialization, out _));
        Assert.Same(firstSpecialization, specialization);
        Assert.Equal(0x1000u, first.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[0][0]);
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref reference, ref referenceSpecialization));
        Assert.Equal(reference.Buffers[0], snapshot.Buffers[0]);
        Assert.Equal(referenceSpecialization, specialization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageConversionPrecedesValidationAndRevalidationObservesConversionChanges(bool expandDescriptor)
    {
        var format = 22u;
        uint[] Convert(ReadOnlySpan<uint> words, ShaderStage stage, ulong hash, string source)
        {
            var converted = new uint[expandDescriptor ? 12 : 8];
            converted[0] = words[0];
            converted[1] = format << 20;
            converted[3] = 0xFACu | (9u << 28);
            return converted;
        }
        var plan = ShaderResourcePlan.Extract(Program(Image(0, "ImageLoad", 0), EndProgram(8)),
            ShaderStage.Compute, Hash, 0, 8, normalizeImageDescriptor: Convert);
        Assert.True(ResourceMaterializer.CanMaterializeFast(plan));
        var inputs = Inputs([0x2000, 0, 0, 0, 0, 0, 0, 0]);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.MaterializeFast(plan, inputs, ref snapshot, ref specialization, out _));
        var first = snapshot;
        var firstSpecialization = specialization;
        Assert.Equal(0x2000u, Assert.Single(first.Images)[0]);
        if (!expandDescriptor) Assert.True(ResourceMaterializer.RevalidateFast(first));
        Assert.True(ResourceMaterializer.MaterializeFast(plan, inputs, ref snapshot, ref specialization, out _));
        Assert.Same(first.Images, snapshot.Images);
        Assert.Same(firstSpecialization, specialization);

        format = 21;
        Assert.False(ResourceMaterializer.RevalidateFast(first));
        Assert.True(ResourceMaterializer.MaterializeFast(plan, inputs, ref snapshot, ref specialization, out _));
        Assert.NotSame(firstSpecialization, specialization);
        Assert.NotSame(first.Images, snapshot.Images);
        Assert.Equal(22u << 20, first.Images[0][1]);
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref reference, ref referenceSpecialization));
        Assert.Equal(reference.Images[0], snapshot.Images[0]);
        Assert.Equal(referenceSpecialization, specialization);
    }
}
