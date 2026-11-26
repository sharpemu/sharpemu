// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// A scalar buffer load does not read the dwords past its V#'s records (RDNA2 ISA, scalar memory bounds checking): they read
// as zero, so a descriptor fetched past the end of its table is a null descriptor, as on the GPU.
public sealed class ScalarBufferBoundsTests
{
    private const ulong TableBase = 0x1000;
    private const ulong DescriptorBase = 0x1100;
    private const int IndexOffset = 0x24;
    private const uint DescriptorRecords = 4 * 32;

    // The index is a dword of the table s[8:11]; the image descriptor is entry `index` (32 bytes each) of the descriptor
    // array s[0:3], sampled with the sampler s[24:27].
    private static Gen5ShaderProgram IndexedImageProgram() => Program(
        ScalarBufferLoad(0, 8, destination: 12, immediateOffset: IndexOffset),
        Sop2(8, "SLshlB32", 13, Gen5Operand.Scalar(12), Operand(5)),
        ScalarBufferLoad(16, 0, destination: 16, count: 8, dynamicOffsetRegister: 13),
        MoveScalar(24, 24, 0),
        MoveScalar(32, 25, 0),
        MoveScalar(40, 26, 0),
        MoveScalar(48, 27, 0),
        Image(56, "ImageSampleLz", 16, 24, dmask: 1),
        EndProgram(64));

    private static (ResourceSnapshot Snapshot, bool Materialized) Materialize(uint index)
    {
        var plan = Extract(IndexedImageProgram(), userDataCount: 16);
        var userData = new uint[16];
        new uint[] { (uint)DescriptorBase, 0, DescriptorRecords, 0 }.CopyTo(userData, 0);
        new uint[] { (uint)TableBase, 0, 0x100, 0 }.CopyTo(userData, 8);
        var memory = ResourceTrackerTests.LinearMemory();
        memory.At(TableBase + IndexOffset) = index;
        for (uint entry = 0; entry < DescriptorRecords / 32; entry++)
        {
            var descriptor = ResourceTrackerTests.ImageDescriptor();
            descriptor[0] = 0x20 + entry;
            ResourceTrackerTests.WriteImage(memory, DescriptorBase + entry * 32, descriptor);
        }

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var materialized = ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref snapshot,
            ref specialization, out _);
        return (snapshot, materialized);
    }

    [Fact]
    public void DescriptorInsideTheBufferIsRead()
    {
        var (snapshot, materialized) = Materialize(2);
        Assert.True(materialized);
        Assert.Equal(0x22u, Assert.Single(snapshot.Images)[0]);
    }

    // An index of 0x0376131F puts its descriptor 1.86 GB past the descriptor array. The GPU reads it as zero.
    [Theory]
    [InlineData(4u)]
    [InlineData(0x0376131Fu)]
    [InlineData(0x03FFFFFFu)]
    public void DescriptorPastTheBufferReadsAsNull(uint index)
    {
        var (snapshot, materialized) = Materialize(index);
        Assert.True(materialized);
        Assert.All(Assert.Single(snapshot.Images), word => Assert.Equal(0u, word));
    }

    // The bounds checks each dword: a descriptor that starts inside the buffer and ends past it keeps the dwords inside.
    [Fact]
    public void ResolvedAddressesFollowTheRecords()
    {
        const ulong records = 0x40;
        Assert.Equal(RuntimeValueEvaluator.RawAddress.Read,
            RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarBufferWord, 0x2000, 0, records, 0, 0x3C, out var last));
        Assert.Equal(0x203Cul, last);
        Assert.Equal(RuntimeValueEvaluator.RawAddress.Zero,
            RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarBufferWord, 0x2000, 0, records, 0, 0x40, out _));
        Assert.Equal(RuntimeValueEvaluator.RawAddress.Zero,
            RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarBufferWord, 0x2000, 0, records, 0x20, 0x6EC263E0, out _));
        // A negative immediate offset is not a valid scalar buffer access: it still fails.
        Assert.Equal(RuntimeValueEvaluator.RawAddress.Failed,
            RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarBufferWord, 0x2000, 0, records, -4, 0, out _));
    }
}
