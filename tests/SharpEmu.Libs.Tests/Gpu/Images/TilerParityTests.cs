// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// GPU output parity between the compiled reference modules and the assembled Sharp modules.
[Collection(SchedulingStateCollection.Name)]
public sealed class TilerParityTests : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly string[] BlockCopyBlobs =
    [
        "gpu_tiler_shaders/gpu_tiler_standard256.spv",
        "gpu_tiler_shaders/gpu_tiler_standard4.spv",
        "gpu_tiler_shaders/gpu_tiler_standard4_3d.spv",
        "gpu_tiler_shaders/gpu_tiler_standard64.spv",
        "gpu_tiler_shaders/gpu_tiler_standard64_3d.spv",
        "gpu_tiler_shaders/gpu_tiler_prt.spv",
        "gpu_tiler_shaders/gpu_tiler_prt_3d.spv",
        "gpu_tiler_shaders/gpu_tiler_render_target.spv",
        "gpu_tiler_shaders/gpu_tiler_depth.spv",
    ];

    private readonly HeadlessVulkan? _vulkan;

    public TilerParityTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    private static byte[] ArgumentBytes(in TileTransferArguments arguments)
    {
        var bytes = new byte[TileTransferArguments.Size];
        MemoryMarshal.Write(bytes, in arguments);
        return bytes;
    }

    [Fact]
    public void BlockCopies_ProduceTheSameBytesAsTheReferenceModules()
    {
        if (!GatePrerequisites.Ready(_vulkan, referenceSpirv: true)) return;
        var blobs = new byte[]?[BlockCopyBlobs.Length];
        for (var index = 0; index < blobs.Length; index++)
        {
            blobs[index] = ReferenceShaders.TryLoad(BlockCopyBlobs[index]);
            if (blobs[index] is null)
            {
                return;
            }
        }

        using var harness = new ImageTestHarness(_vulkan);
        using var runner = new TilerComputeRunner(harness);
        var failures = new List<string>();
        var variants = 0;
        foreach (var tilerCase in TilerCases.All())
        {
            var transfer = tilerCase.Transfer;
            var reference = blobs[(int)transfer.Kind]!;
            foreach (var toTiled in new[] { false, true })
            {
                var referencePipeline = runner.CreatePipeline(reference, [transfer.BytesPerElement, toTiled ? 1u : 0u]);
                var sharpPipeline = runner.CreatePipeline(TilerShaders.CreateBlockCopy((TilerBlockShape)transfer.Kind, transfer.BytesPerElement, toTiled), ReadOnlySpan<uint>.Empty);
                var input = harness.Upload(toTiled ? tilerCase.Linear : tilerCase.Tiled.Length != 0 ? tilerCase.Tiled : ImageTestHarness.Pattern((int)transfer.TiledSize, 99), Math.Max(transfer.LinearSize, transfer.TiledSize));
                var outputSize = toTiled ? transfer.TiledSize : transfer.LinearSize;
                var referenceOutput = harness.CreateDeviceLocalBuffer(outputSize);
                var sharpOutput = harness.CreateDeviceLocalBuffer(outputSize);
                var arguments = harness.Upload(ArgumentBytes(tilerCase.Arguments(0, 0)), 256);
                var groupsX = (transfer.Width + 7) / 8;
                var groupsY = (transfer.Height + 7) / 8;
                harness.Run(() =>
                {
                    referenceOutput.Fill(0, referenceOutput.Size, 0);
                    sharpOutput.Fill(0, sharpOutput.Size, 0);
                    runner.Dispatch(referencePipeline, input, referenceOutput, arguments, 0, default, groupsX, groupsY, transfer.Depth);
                    runner.Dispatch(sharpPipeline, input, sharpOutput, arguments, 0, default, groupsX, groupsY, transfer.Depth);
                });
                var expected = harness.ReadBack(referenceOutput.Handle, 0, outputSize);
                var actual = harness.ReadBack(sharpOutput.Handle, 0, outputSize);
                if (!expected.AsSpan().SequenceEqual(actual))
                {
                    failures.Add($"{tilerCase.Name} tile={toTiled}");
                }

                if (toTiled && tilerCase.Tiled.Length != 0 && !expected.AsSpan().SequenceEqual(tilerCase.Tiled))
                {
                    failures.Add($"{tilerCase.Name}: the CPU twin differs from the reference module");
                }

                variants++;
            }
        }

        Assert.True(variants >= 2 * 88, $"variants={variants}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepthConversions_ProduceTheSameBytesAsTheReferenceModules(bool useFloat32Depth)
    {
        if (!GatePrerequisites.Ready(_vulkan, referenceSpirv: true)) return;
        var widen = ReferenceShaders.TryLoad("gpu_tiler_shaders/gpu_tiler_promote_d16.spv");
        var narrow = ReferenceShaders.TryLoad("gpu_tiler_shaders/gpu_tiler_demote_d16.spv");
        if (widen is null || narrow is null)
        {
            return;
        }

        using var harness = new ImageTestHarness(_vulkan);
        using var runner = new TilerComputeRunner(harness);
        const uint width = 37;
        const uint height = 11;
        const uint sourceStride = 80;
        const uint targetStride = 160;
        var wideInput = new byte[height * targetStride];
        var random = new System.Random(7);
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = useFloat32Depth
                    ? BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 2.0 - 0.5))
                    : (uint)random.Next() & 0x00ffffffu | ((uint)random.Next(0, 4) << 24);
                BitConverter.TryWriteBytes(wideInput.AsSpan(row * (int)targetStride + x * 4), value);
            }
        }

        var narrowInput = ImageTestHarness.Pattern((int)(height * sourceStride), 3);
        var specialization = new[] { useFloat32Depth ? 1u : 0u };
        var referenceWiden = runner.CreatePipeline(widen, specialization);
        var sharpWiden = runner.CreatePipeline(TilerShaders.CreateDepthWiden(useFloat32Depth), ReadOnlySpan<uint>.Empty);
        var referenceNarrow = runner.CreatePipeline(narrow, specialization);
        var sharpNarrow = runner.CreatePipeline(TilerShaders.CreateDepthNarrow(useFloat32Depth), ReadOnlySpan<uint>.Empty);

        var narrowBuffer = harness.Upload(narrowInput);
        var wideBuffer = harness.Upload(wideInput);
        var outputs = new[] { harness.CreateDeviceLocalBuffer(height * targetStride), harness.CreateDeviceLocalBuffer(height * targetStride), harness.CreateDeviceLocalBuffer(height * sourceStride), harness.CreateDeviceLocalBuffer(height * sourceStride) };
        var unused = harness.Upload(new byte[64]);
        var widenPush = new TileTransferArguments { Width = width, Height = height, PitchBytes = sourceStride, SliceBytes = targetStride };
        var narrowPush = new TileTransferArguments { Width = width, Height = height, PitchBytes = targetStride, SliceBytes = sourceStride };
        harness.Run(() =>
        {
            foreach (var output in outputs)
            {
                output.Fill(0, output.Size, 0x5A5A5A5A);
            }

            runner.Dispatch(referenceWiden, narrowBuffer, outputs[0], unused, 0, widenPush, 1, height, 1);
            runner.Dispatch(sharpWiden, narrowBuffer, outputs[1], unused, 0, widenPush, 1, height, 1);
            runner.Dispatch(referenceNarrow, wideBuffer, outputs[2], unused, 0, narrowPush, 1, height, 1);
            runner.Dispatch(sharpNarrow, wideBuffer, outputs[3], unused, 0, narrowPush, 1, height, 1);
        });
        Assert.Equal(harness.ReadBack(outputs[0].Handle, 0, outputs[0].Size), harness.ReadBack(outputs[1].Handle, 0, outputs[1].Size));
        Assert.Equal(harness.ReadBack(outputs[2].Handle, 0, outputs[2].Size), harness.ReadBack(outputs[3].Handle, 0, outputs[3].Size));
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void ChannelSwap_ProducesTheSameBytesAsTheReferenceModule()
    {
        if (!GatePrerequisites.Ready(_vulkan, referenceSpirv: true)) return;
        var reference = ReferenceShaders.TryLoad("gpu_tiler_shaders/gpu_tiler_swap_bgra16.spv");
        if (reference is null)
        {
            return;
        }

        using var harness = new ImageTestHarness(_vulkan);
        using var runner = new TilerComputeRunner(harness);
        var referencePipeline = runner.CreatePipeline(reference, ReadOnlySpan<uint>.Empty);
        var sharpPipeline = runner.CreatePipeline(TilerShaders.CreateBgra16Swap(), ReadOnlySpan<uint>.Empty);
        var input = harness.Upload(ImageTestHarness.Pattern(8 * 131, 23));
        var referenceOutput = harness.CreateDeviceLocalBuffer(input.Size);
        var sharpOutput = harness.CreateDeviceLocalBuffer(input.Size);
        var unused = harness.Upload(new byte[64]);
        var push = new TileTransferArguments { Width = 131 };
        harness.Run(() =>
        {
            runner.Dispatch(referencePipeline, input, referenceOutput, unused, 0, push, (131 + 63) / 64, 1, 1);
            runner.Dispatch(sharpPipeline, input, sharpOutput, unused, 0, push, (131 + 63) / 64, 1, 1);
        });
        Assert.Equal(harness.ReadBack(referenceOutput.Handle, 0, input.Size), harness.ReadBack(sharpOutput.Handle, 0, input.Size));
        harness.AssertNoValidationMessages();
    }
}
