// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5BufferFallbackPoolingTests
{
    private const ulong ShaderAddress = 0x2_0000_0000;
    private const ulong UnmappedBufferAddress = 0x4_0000_0000;
    private const int BufferSize = 1024 * 1024;

    [Fact]
    public void RepeatedUnmappedBufferFallbacksReuseZeroedTransferStorage()
    {
        var state = CreateBufferLoadState();
        var ctx = new CpuContext(
            new FakeCpuMemory(0x1_0000_0000, 0x1000),
            Generation.Gen5);

        var warmup = Evaluate(ctx, state);
        var warmupBinding = Assert.Single(warmup.GlobalMemoryBindings);
        Assert.True(warmupBinding.DataPooled);
        Assert.False(warmupBinding.WriteBackToGuest);
        Assert.Equal(BufferSize, warmupBinding.DataLength);
        warmupBinding.Data.AsSpan(0, warmupBinding.DataLength).Fill(0xA5);
        Gen5ShaderScalarEvaluator.GlobalMemoryPool.Return(warmupBinding.Data);

        const int iterations = 12;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var evaluation = Evaluate(ctx, state);
            var binding = Assert.Single(evaluation.GlobalMemoryBindings);
            Assert.True(binding.DataPooled);
            Assert.False(binding.WriteBackToGuest);
            Assert.Equal(BufferSize, binding.DataLength);
            Assert.True(
                binding.Data.AsSpan(0, binding.DataLength).IndexOfAnyExcept((byte)0) < 0,
                "Synthetic fallback storage must be cleared after it is reused.");
            Gen5ShaderScalarEvaluator.GlobalMemoryPool.Return(binding.Data);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(
            allocated < BufferSize,
            $"Repeated fallbacks allocated {allocated:N0} bytes after pool warmup.");
    }

    private static Gen5ShaderEvaluation Evaluate(
        CpuContext ctx,
        Gen5ShaderState state)
    {
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        return evaluation;
    }

    private static Gen5ShaderState CreateBufferLoadState()
    {
        var load = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mubuf,
            "BufferLoadDword",
            [],
            [],
            [],
            new Gen5BufferMemoryControl(
                1,
                0,
                0,
                0,
                0,
                IndexEnabled: false,
                OffsetEnabled: false,
                Glc: false,
                Slc: false));
        var end = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        return new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [load, end]),
            [
                unchecked((uint)UnmappedBufferAddress),
                (uint)(UnmappedBufferAddress >> 32),
                BufferSize,
                0,
            ],
            null);
    }
}
