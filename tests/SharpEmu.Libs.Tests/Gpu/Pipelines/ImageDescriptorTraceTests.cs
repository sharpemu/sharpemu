// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class ImageDescriptorTraceTests
{
    [Fact]
    public void DisabledTrace_DoesNotInspectInputsOrAllocate()
    {
        if (ImageClearTrace.Enabled) return;
        var trace = new ImageDescriptorTrace();
        void Record() => trace.Record(null!, null!, null!);
        Record();
        Assert.Equal(0, AllocationMeasurementCollection.Measure(Record));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Record_UsesOnlyCompleteSnapshotDescriptors(bool complete)
    {
        var plan = Extract(Program(Image(0, "ImageLoad", 0, dimension: 5), EndProgram(8)), userDataCount: 8);
        var words = VolumeWords();
        var shader = new ShaderSource(new RegisteredShader(0x1000, 0, 12, 0, 0, 0, 0, 0, 0),
            0xDB080010, words, 0, ShaderStage.Compute);
        var text = Capture(() => new ImageDescriptorTrace().Record(shader, plan,
            new ResourceSnapshot { Images = [complete ? words : words[..7]], UserData = words }));
        if (ImageClearTrace.Enabled && complete)
        {
            Assert.Contains("hash=0x00000000DB080010", text);
            Assert.Contains("address=0x0000000000123400", text);
            Assert.Contains("ImageDescriptorRoots", text);
            Assert.Contains("kind=UserData", text);
            Assert.Contains("resolved=0x00001234", text);
            Assert.Contains("nodes=8 truncated=False", text);
        }
        else Assert.DoesNotContain("hash=0x00000000DB080010", text);
    }

    [Fact]
    public void ProgramCache_RecordsSnapshotsBeforePermutationReuse()
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest();
        var address = PipelineTestGuest.MemoryBase + 0x1000;
        guest.RegisterProgram(address, PipelineTestGuest.MemoryBase + 0x8000,
            [0xF0000128, 0x00000400, 0xBF810000]);
        var words = VolumeWords();
        var source = guest.Source(address, ShaderStage.Compute, words);
        var text = Capture(() =>
        {
            for (var index = 0; index < 2; index++)
            {
                words[0] = 0x1234u + (uint)index;
                var cursor = 0u;
                _ = guest.Programs.GetOrCompile(source, PipelineTestGuest.ComputeOptions(), ref cursor, out var stage);
                Assert.Equal(words[0], Assert.Single(stage.Resources.Images)[0]);
            }
        });
        Assert.Equal(1, guest.Compiler.Compilations);
        if (ImageClearTrace.Enabled)
        {
            Assert.Contains($"hash=0x{source.Hash:X16}", text);
            Assert.Contains("address=0x0000000000123400", text);
            Assert.Contains("address=0x0000000000123500", text);
        }
        else Assert.Empty(text);
    }

    private static uint[] VolumeWords() =>
        [0x1234, (uint)GuestPixelFormat.Bits8_8_8_8UNorm << 20, 0,
            (uint)GuestImageType.Color3D << 28 | 4u | 5u << 3 | 6u << 6 | 7u << 9, 0, 0, 0, 0];

    private static string Capture(Action record)
    {
        var previous = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            ImageDescriptorTrace.WriteHistory();
            output.GetStringBuilder().Clear();
            record();
            ImageDescriptorTrace.WriteHistory();
            return output.ToString();
        }
        finally { Console.SetError(previous); }
    }
}
