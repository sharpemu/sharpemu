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
    public void RetainedInterleavedInputsSkipGuestReadsAndPoolRentals()
    {
        var memory = new ReadableCpuMemory();
        var pending = CreatePendingVertexInputs();
        var bytes = Enumerable.Repeat((byte)0x7b, 44).ToArray();
        var retained = new[]
        {
            pending[0] with { Data = bytes, DataLength = 44 },
            pending[1] with { BaseAddress = GuestAddress, OffsetBytes = 12, Data = bytes, DataLength = 44 },
        };
        var pool = new TrackingArrayPool();
        WithPool(pool, () =>
        {
            Assert.True(Gen5ShaderScalarEvaluator.TryCaptureVertexInputData(
                new CpuContext(memory, Generation.Gen5), pending, out var captured, out var error, retained, out var reused), error);
            Assert.True(reused);
            Assert.Same(retained[0], captured[0]);
            Assert.Same(retained[1], captured[1]);
            Assert.Equal(0x7b, captured[0].Data[0]);
        });
        Assert.Equal(0, memory.ReadCount);
        Assert.Equal(0, pool.RentCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RetainedInputMismatchFallsBackToGuestCapture(int mismatch)
    {
        var memory = new ReadableCpuMemory();
        var pending = CreatePendingVertexInputs();
        var bytes = new byte[44];
        var retained = new[]
        {
            pending[0] with { Data = bytes, DataLength = 44 },
            pending[1] with { BaseAddress = GuestAddress, OffsetBytes = 12, Data = bytes, DataLength = 44 },
        };
        retained[1] = mismatch switch
        {
            0 => retained[1] with { Pc = 99 },
            1 => retained[1] with { OffsetBytes = 16 },
            2 => retained[1] with { DataLength = 32 },
            3 => retained[1] with { DataPooled = true },
            _ => retained[1] with { DataFormat = 0 },
        };
        var pool = new TrackingArrayPool();
        WithPool(pool, () =>
        {
            Assert.True(Gen5ShaderScalarEvaluator.TryCaptureVertexInputData(
                new CpuContext(memory, Generation.Gen5), pending, out var captured, out var error, retained, out var reused), error);
            Assert.False(reused);
            Assert.NotSame(bytes, captured[0].Data);
            Assert.Equal(0, captured[0].Data[0]);
            Assert.Same(captured[0].Data, captured[1].Data);
            foreach (var binding in captured)
                if (binding.DataPooled) pool.Return(binding.Data);
        });
        Assert.Equal(1, memory.ReadCount);
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.OutstandingCount);
    }

    private static Gen5VertexInputBinding[] CreatePendingVertexInputs() =>
    [
        new(0x40, 0, 2, 11, 7, GuestAddress, 20, 0, [], 32, false),
        new(0x44, 1, 2, 11, 7, GuestAddress + 12, 20, 0, [], 32, false),
    ];

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
        public int ReadCount { get; private set; }
        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            ReadCount++;
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
