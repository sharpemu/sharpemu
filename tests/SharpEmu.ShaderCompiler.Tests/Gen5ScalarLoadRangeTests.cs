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
            new Gen5GlobalMemoryControl(1, 0, 0, 0, 0, Glc: false, Slc: false));
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
        Action<Gen5GlobalMemoryBinding> checkBinding)
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
            if (!CanRead(address, (ulong)destination.Length))
            {
                return false;
            }

            destination.Clear();
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
