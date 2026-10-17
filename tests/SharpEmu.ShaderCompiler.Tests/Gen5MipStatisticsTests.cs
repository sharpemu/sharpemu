// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5MipStatisticsTests
{
    private const ulong ShaderAddress = 0x1_0000_C000;

    private static (ShaderCompileRequest Request, BindingLayout Layout) PixelSample(string opcode, bool recordStatistics, bool reduce)
    {
        var addressCount = opcode.Contains("SampleL", StringComparison.Ordinal) && !opcode.Contains("Lz", StringComparison.Ordinal) ? 3 : 2;
        var control = new Gen5ImageControl(0xF, 0, Enumerable.Range(0, addressCount).Select(static value => (uint)value).ToArray(), 4, 8, 16, 1,
            false, false, false, false, false);
        var program = new Gen5ShaderProgram(ShaderAddress,
        [
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mimg, opcode, [], [], [], control),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
        ]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, ResourceTestProgram.Hash, 0, 20);
        var userData = new uint[20];
        userData[8] = 0x20;
        userData[9] = 71u << 20;
        userData[11] = 9u << 28;
        userData[13] = 1u << 25;
        userData[14] = 7;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(userData), ref snapshot, ref specialization));
        specialization.Images[0] = specialization.Images[0] with { MipStatistics = recordStatistics };
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 20), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            MipStatisticsSubgroupReduction = reduce,
        };
        return (request, layout);
    }

    private static List<SpirvOp> Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        ValidateWhenAvailable(shader.Spirv);
        var opcodes = new List<SpirvOp>();
        for (var index = 20; index + 4 <= shader.Spirv.Length;)
        {
            var word = BitConverter.ToUInt32(shader.Spirv, index);
            opcodes.Add((SpirvOp)(word & 0xFFFF));
            index += (int)(word >> 16) * sizeof(uint);
        }

        return opcodes;
    }

    [Theory]
    [InlineData("ImageSample", false)]
    [InlineData("ImageSample", true)]
    [InlineData("ImageSampleL", false)]
    [InlineData("ImageSampleLz", true)]
    public void ASampledImageWithACounterRecordsItsFinestMip(string opcode, bool reduce)
    {
        var (request, layout) = PixelSample(opcode, recordStatistics: true, reduce);

        Assert.Equal(1u, layout.MipStatisticsCount);
        Assert.NotNull(layout.Find(DescriptorBindingKind.MipStatistics));
        var opcodes = Compile(request);
        Assert.Contains(SpirvOp.AtomicUMin, opcodes);
        Assert.Equal(opcode == "ImageSample", opcodes.Contains(SpirvOp.ImageQueryLod));
        Assert.Equal(reduce, opcodes.Contains(SpirvOp.GroupNonUniformUMin));
    }

    [Fact]
    public void ADrawWithoutCountersCompilesThePlainProgram()
    {
        var (request, layout) = PixelSample("ImageSample", recordStatistics: false, reduce: true);

        Assert.Equal(0u, layout.MipStatisticsCount);
        Assert.Null(layout.Find(DescriptorBindingKind.MipStatistics));
        Assert.DoesNotContain(SpirvOp.AtomicUMin, Compile(request));
    }

    internal static void ValidateWhenAvailable(byte[] code)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var executable = string.IsNullOrWhiteSpace(sdk) ? null
            : Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val");
        if (executable is null || !File.Exists(executable))
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, code);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
