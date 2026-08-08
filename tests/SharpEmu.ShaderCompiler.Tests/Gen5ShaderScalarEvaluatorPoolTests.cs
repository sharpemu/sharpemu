// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[CollectionDefinition(PoolStateCollection.Name, DisableParallelization = true)]
public sealed class PoolStateCollection
{
    public const string Name = "Gen5ShaderScalarEvaluatorPoolState";
}

[Collection(PoolStateCollection.Name)]
public sealed class Gen5ShaderScalarEvaluatorPoolTests
{
    private const ulong GuestAddress = 0x1_0000_0000;

    [Fact]
    public void FailedEvaluationReturnsPreviouslyCapturedGlobalMemory()
    {
        var globalLoad = CreateGlobalLoad();
        var invalidBufferLoad = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Mubuf,
            "BufferLoadDword",
            [],
            [],
            [],
            new Gen5BufferMemoryControl(
                1,
                0,
                0,
                254,
                0,
                IndexEnabled: false,
                OffsetEnabled: false,
                Glc: false,
                Slc: false));
        var state = CreateState([globalLoad, invalidBufferLoad]);
        var pool = new TrackingArrayPool();

        WithPool(pool, () =>
        {
            Assert.False(Gen5ShaderScalarEvaluator.TryEvaluate(
                new CpuContext(new ReadableCpuMemory(), Generation.Gen5),
                state,
                out _,
                out var error));
            Assert.Contains("buffer-resource-register-range", error);
        });

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Fact]
    public void ImageOnlyResolutionReturnsUnusedGlobalMemory()
    {
        var state = CreateState([
            CreateGlobalLoad(),
            new Gen5ShaderInstruction(
                8,
                Gen5ShaderEncoding.Sopp,
                "SEndpgm",
                [],
                [],
                [],
                null),
        ]);
        var pool = new TrackingArrayPool();

        WithPool(pool, () =>
        {
            Assert.True(Gen5ShaderScalarEvaluator.TryResolveImageBindings(
                new CpuContext(new ReadableCpuMemory(), Generation.Gen5),
                state,
                out var bindings,
                out var error), error);
            Assert.Empty(bindings);
        });

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    private static Gen5ShaderInstruction CreateGlobalLoad() => new(
        0,
        Gen5ShaderEncoding.Flat,
        "GlobalLoadDword",
        [],
        [],
        [],
        new Gen5GlobalMemoryControl(
            1,
            0,
            0,
            0,
            0,
            Glc: false,
            Slc: false));

    private static Gen5ShaderState CreateState(
        IReadOnlyList<Gen5ShaderInstruction> instructions) => new(
            new Gen5ShaderProgram(0, instructions),
            [
                unchecked((uint)GuestAddress),
                unchecked((uint)(GuestAddress >> 32)),
                0,
                0,
                0xEA10_0010,
                0x0004_0011,
                0,
                0x0000_5204,
            ],
            null);

    private static void WithPool(TrackingArrayPool pool, Action action)
    {
        var previous = Gen5ShaderScalarEvaluator.GlobalMemoryPool;
        try
        {
            Gen5ShaderScalarEvaluator.GlobalMemoryPool = pool;
            action();
        }
        finally
        {
            Gen5ShaderScalarEvaluator.GlobalMemoryPool = previous;
        }
    }

    private sealed class ReadableCpuMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress != GuestAddress)
            {
                return false;
            }

            destination.Clear();
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _outstanding = new(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);

        public int RentCount { get; private set; }

        public int ReturnCount { get; private set; }

        public int OutstandingCount => _outstanding.Count;

        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[minimumLength];
            RentCount++;
            Assert.True(_outstanding.Add(array));
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ReturnCount++;
            Assert.True(_outstanding.Remove(array));
        }
    }
}
