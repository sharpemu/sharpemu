// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection(PoolStateCollection.Name)]
public sealed class Gen5ScalarLoadRangeTests
{
    private const ulong GuestAddress = 0x1_0000_0000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentLoadsUseOneReadOrRetainIndividualReadFallback(bool rejectCombinedRead)
    {
        var memory = new RangeMemory { RejectCombinedRead = rejectCombinedRead, UseAddressValues = true };
        Evaluate(false, memory, [Load(0, offsetRegister: null)], binding =>
        {
            Assert.Equal(16UL, binding.Size);
            Assert.Equal(rejectCombinedRead ? 5 : 1, memory.ReadCalls);
        }, evaluation => Assert.Equal(new uint[] { 1, 2, 3, 4 }, evaluation.ScalarRegisters.Skip(4).Take(4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedBoundsDoNotDependOnRuntimePointerOrOffset(bool bounded)
    {
        var program = new Gen5ShaderProgram(0,
            [MaskOffset(0, 0x1F0), Load(8, offsetRegister: bounded ? 106u : 2u)]);
        var firstState = new Gen5ShaderState(program, [0u, 1u, 16u], null);
        var secondState = new Gen5ShaderState(program, [4096u, 2u, 0xFFFFu], null);
        Assert.Equal(bounded, Gen5ShaderScalarEvaluator.TryGetScalarLoadBindingExtent(firstState, 0, out var firstExtent));
        Assert.Equal(bounded, Gen5ShaderScalarEvaluator.TryGetScalarLoadBindingExtent(secondState, 0, out var secondExtent));
        Assert.Equal(bounded ? 512UL : 0UL, firstExtent);
        Assert.Equal(firstExtent, secondExtent);
        Assert.False(Gen5ShaderScalarEvaluator.TryGetScalarLoadBindingExtent(firstState, 8, out _));
    }

    [Fact]
    public void NewProgramAtTheSameAddressGetsItsOwnBound()
    {
        var firstState = new Gen5ShaderState(new Gen5ShaderProgram(0,
            [Load(0, offsetRegister: null, immediateOffset: 64)]), [], null);
        var secondState = new Gen5ShaderState(new Gen5ShaderProgram(0,
            [Load(0, offsetRegister: null, immediateOffset: 128)]), [], null);
        Assert.True(Gen5ShaderScalarEvaluator.TryGetScalarLoadBindingExtent(firstState, 0, out var firstExtent));
        Assert.True(Gen5ShaderScalarEvaluator.TryGetScalarLoadBindingExtent(secondState, 0, out var secondExtent));
        Assert.Equal(80UL, firstExtent);
        Assert.Equal(144UL, secondExtent);
    }

    [Fact]
    public void CachedBoundsSupportConcurrentReadersWithoutHitAllocations()
    {
        var state = new Gen5ShaderState(new Gen5ShaderProgram(0,
            [Load(0, offsetRegister: null, immediateOffset: 64)]), [], null);
        Parallel.For(0, 64, _ =>
        {
            Assert.True(Gen5ShaderScalarEvaluator.TryGetScalarLoadBindingExtent(state, 0, out var extent));
            Assert.Equal(80UL, extent);
        });
        for (var warmup = 0; warmup < 256; warmup++)
            Gen5ShaderScalarEvaluator.TryGetScalarLoadBindingExtent(state, 0, out _);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var correct = true;
        for (var lookup = 0; lookup < 1024; lookup++)
            correct &= Gen5ShaderScalarEvaluator.TryGetScalarLoadBindingExtent(state, 0, out var extent) && extent == 80;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(correct);
        Assert.Equal(0, allocatedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaskedLoadsUseTheirFullRuntimeBound(bool snapshot)
    {
        var memory = new RangeMemory();
        Evaluate(snapshot, memory,
            [MaskOffset(0, 0x1F0), Load(8), MaskOffset(16, 0x1F0), Load(24)],
            binding =>
            {
                Assert.Equal(512UL, binding.Size);
                Assert.Equal(new uint[] { 8, 24 }, binding.InstructionPcs);
                Assert.Equal(512UL, memory.LargestRequest);
                memory.AccessibleBytes = 512;
                Assert.True(memory.CanRead(binding.BaseAddress, binding.Size));
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterLoadContributesToTheBindingBound(bool snapshot)
    {
        Evaluate(snapshot, new RangeMemory(),
            [MaskOffset(0, 0x1F0), Load(8), MaskOffset(16, 0x3F0), Load(24, immediateOffset: 16)],
            binding => Assert.Equal(1040UL, binding.Size));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownLaterOffsetKeepsTheConservativeExtent(bool snapshot)
    {
        Evaluate(snapshot, new RangeMemory(),
            [MaskOffset(0, 0x1F0), Load(8), Load(16, offsetRegister: 2)],
            binding => Assert.Equal(256UL * 1024, binding.Size));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeImmediateKeepsTheConservativeExtent(bool snapshot)
    {
        Evaluate(snapshot, new RangeMemory(),
            [MaskOffset(0, 0x1F0), Load(8, immediateOffset: -4)],
            binding => Assert.Equal(256UL * 1024, binding.Size));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmediateLoadsIncludeAllComponents(bool snapshot)
    {
        Evaluate(snapshot, new RangeMemory(),
            [Load(0, offsetRegister: null, immediateOffset: 64)],
            binding => Assert.Equal(80UL, binding.Size));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedGlobalLoadKeepsTheConservativeExtent(bool snapshot)
    {
        var globalLoad = new Gen5ShaderInstruction(
            16, Gen5ShaderEncoding.Flat, "GlobalLoadDword", [], [], [],
            new Gen5GlobalMemoryControl(1, 0, 0, 0, 0, 0, Glc: false, Slc: false));
        Evaluate(snapshot, new RangeMemory(),
            [MaskOffset(0, 0x1F0), Load(8), globalLoad],
            binding => Assert.Equal(256UL * 1024, binding.Size));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacedOffsetDoesNotReuseAnEarlierMask(bool snapshot)
    {
        var replaceOffset = new Gen5ShaderInstruction(
            16, Gen5ShaderEncoding.Sop1, "SMovB32", [0u],
            [Gen5Operand.Scalar(2)], [Gen5Operand.Scalar(106)], null);
        Evaluate(snapshot, new RangeMemory(),
            [MaskOffset(0, 0x1F0), Load(8), replaceOffset, Load(20)],
            binding => Assert.Equal(256UL * 1024, binding.Size));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergedOffsetDoesNotUseOneBranchMask(bool snapshot)
    {
        var branch = new Gen5ShaderInstruction(
            16, Gen5ShaderEncoding.Sopp, "SCbranchScc0", [4u], [], [], null);
        var skipAlternative = new Gen5ShaderInstruction(
            28, Gen5ShaderEncoding.Sopp, "SBranch", [3u], [], [], null);
        Evaluate(snapshot, new RangeMemory(),
            [MaskOffset(0, 0x1F0), Load(8), branch, MaskOffset(20, 0x3F0),
             skipAlternative, MaskOffset(36, 0x7F0), Load(44)],
            binding => Assert.Equal(256UL * 1024, binding.Size));
    }

    private static Gen5ShaderInstruction MaskOffset(uint programCounter, uint mask) => new(
        programCounter, Gen5ShaderEncoding.Sop2, "SAndB32", [0u, mask],
        [Gen5Operand.Scalar(2), Gen5Operand.Source(255, mask)],
        [Gen5Operand.Scalar(106)], null);

    private static Gen5ShaderInstruction Load(
        uint programCounter,
        uint? offsetRegister = 106,
        int immediateOffset = 0) => new(
            programCounter, Gen5ShaderEncoding.Smem, "SLoadDwordx4", [0u, 0u],
            [Gen5Operand.Scalar(0), Gen5Operand.Source(128)],
            [Gen5Operand.Scalar(4), Gen5Operand.Scalar(5), Gen5Operand.Scalar(6), Gen5Operand.Scalar(7)],
            new Gen5ScalarMemoryControl(4, immediateOffset, offsetRegister));

    private static void Evaluate(
        bool snapshot,
        RangeMemory memory,
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        Action<Gen5GlobalMemoryBinding> checkBinding,
        Action<Gen5ShaderEvaluation>? checkEvaluation = null)
    {
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, instructions),
            [unchecked((uint)GuestAddress), (uint)(GuestAddress >> 32), 16u], null);
        var previousSnapshot = Gen5ShaderScalarEvaluator.SnapshotGlobalMemory;
        Gen5ShaderEvaluation? evaluation = null;
        try
        {
            Gen5ShaderScalarEvaluator.SnapshotGlobalMemory = snapshot;
            Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(
                new CpuContext(memory, Generation.Gen5), state, out evaluation, out var error), error);
            checkBinding(Assert.Single(evaluation.GlobalMemoryBindings));
            checkEvaluation?.Invoke(evaluation);
        }
        finally
        {
            Gen5ShaderScalarEvaluator.SnapshotGlobalMemory = previousSnapshot;
            if (evaluation is not null)
            {
                foreach (var binding in evaluation.GlobalMemoryBindings)
                {
                    if (binding.DataPooled)
                    {
                        Gen5ShaderScalarEvaluator.GlobalMemoryPool.Return(binding.Data);
                    }
                }
            }
        }
    }

    private sealed class RangeMemory : ICpuMemory
    {
        public bool RejectCombinedRead { get; init; }
        public bool UseAddressValues { get; init; }
        public int ReadCalls { get; private set; }
        public ulong AccessibleBytes { get; set; } = 256 * 1024;
        public ulong LargestRequest { get; private set; }

        public bool CanRead(ulong address, ulong length)
        {
            LargestRequest = Math.Max(LargestRequest, length);
            return address >= GuestAddress && length <= AccessibleBytes &&
                address - GuestAddress <= AccessibleBytes - length;
        }

        public bool TryRead(ulong address, Span<byte> destination)
        {
            ReadCalls++;
            if (RejectCombinedRead && destination.Length > sizeof(uint)) return false;
            if (!CanRead(address, (ulong)destination.Length))
            {
                return false;
            }

            destination.Clear();
            if (UseAddressValues)
            {
                for (var offset = 0; offset + sizeof(uint) <= destination.Length; offset += sizeof(uint))
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..],
                        (uint)((address - GuestAddress + (ulong)offset) / sizeof(uint) + 1));
                }
            }
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
