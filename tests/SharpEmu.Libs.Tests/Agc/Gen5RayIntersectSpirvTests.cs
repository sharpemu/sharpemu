// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5RayIntersectSpirvTests
{
    private static readonly Lazy<byte[]> NumericalModule = new(() => Compile(true, false));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NodeBranchesRequireDescriptorAndAddressChecks(bool widePointer, bool halfDirections)
    {
        var spirv = Compile(widePointer, halfDirections);
        var inspector = new SpirvModuleInspector(spirv);
        var descriptorCheck = Assert.Single(inspector.Names, pair => pair.Value == "rayDescriptorTypeValid").Key;
        var addressCheck = Assert.Single(inspector.Names, pair => pair.Value == "rayNodeAddressValid").Key;
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var conjunctions = new Dictionary<uint, (uint Left, uint Right)>();
        var conditions = new List<uint>();
        for (var offset = 5; offset < words.Length;)
        {
            var count = checked((int)(words[offset] >> 16));
            Assert.True(count > 0);
            var opcode = (SpirvOp)(words[offset] & 0xFFFF);
            if (opcode == SpirvOp.LogicalAnd)
                conjunctions.Add(words[offset + 2], (words[offset + 3], words[offset + 4]));
            if (opcode == SpirvOp.BranchConditional)
                conditions.Add(words[offset + 1]);
            offset += count;
        }

        bool Requires(uint condition, uint check) => condition == check ||
            conjunctions.TryGetValue(condition, out var inputs) &&
            (Requires(inputs.Left, check) || Requires(inputs.Right, check));

        // The triangle and both box formats must reject invalid addresses before loading.
        Assert.Equal(3, conditions.Count(condition =>
            Requires(condition, descriptorCheck) && Requires(condition, addressCheck)));
        ValidateWithInstalledSdk(spirv);
    }

    private static byte[] Compile(bool widePointer, bool halfDirections)
    {
        const ulong address = 0x1_0000_0000;
        var memory = new FakeCpuMemory(address, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, address,
            [widePointer ? 0xF19C9F01u : 0xF1989F01u, halfDirections ? 0x40010C00u : 0x00010C00u]);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, address, out var program, out var error), error);
        var request = ResourceTestProgram.Request(program, userDataCount: 16);
        Assert.True(request.Resources.Info.UsesDeviceAddresses);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request,
            out var shader, out error), error);

        return shader.Spirv;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    public void FanTypesReturnHitDistanceAndAdjustedId(uint type, int dominantAxis)
    {
        var evaluator = Triangle(type, dominantAxis, 2f);
        Assert.Equal(1u, evaluator.Read("rayTriangleOutput3"));
        Assert.Equal(100u + type, evaluator.Read("rayTriangleOutput2"));
        Assert.Equal(2f, evaluator.ReadFloat("rayTriangleOutput0") / evaluator.ReadFloat("rayTriangleOutput1"));
    }

    [Theory]
    [InlineData(-2f)]
    [InlineData(float.NaN)]
    public void TriangleRejectsBehindRayAndNan(float depth)
    {
        var evaluator = Triangle(0, 2, depth);
        Assert.Equal(0u, evaluator.Read("rayTriangleOutput3"));
        Assert.Equal(float.PositiveInfinity, evaluator.ReadFloat("rayTriangleOutput0"));
        Assert.Equal(1f, evaluator.ReadFloat("rayTriangleOutput1"));
    }

    [Fact]
    public void TriangleRejectsZeroDirection()
    {
        var evaluator = Triangle(0, 2, 2f);
        evaluator.SetFloat("rayDirection2", 0f);
        Assert.Equal(0u, evaluator.Read("rayTriangleOutput3"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TriangleAcceptsEitherWinding(bool reversed)
    {
        var evaluator = Triangle(0, 2, 2f);
        if (reversed)
        {
            evaluator.SetFloat("rayTriangleVertex0Axis0", 1f);
            evaluator.SetFloat("rayTriangleVertex1Axis0", -1f);
        }
        Assert.Equal(1u, evaluator.Read("rayTriangleOutput3"));
        Assert.Equal(2f, evaluator.ReadFloat("rayTriangleOutput0") / evaluator.ReadFloat("rayTriangleOutput1"));
    }

    [Fact]
    public void SharedFanEdgeBelongsToExactlyOneTriangle()
    {
        var evaluator = Triangle(0, 2, 2f);
        float[][] vertices = [[-1f, -1f, 2f], [1f, -1f, 2f], [-1f, 1f, 2f], [1f, 1f, 2f]];
        for (var vertex = 0; vertex < 4; vertex++)
            for (var axis = 0; axis < 3; axis++)
                evaluator.SetFloat($"rayTriangleVertex{vertex}Axis{axis}", vertices[vertex][axis]);
        var firstHit = evaluator.Read("rayTriangleOutput3");
        evaluator.Set("rayTriangleType", 1);
        Assert.Equal(1u, firstHit + evaluator.Read("rayTriangleOutput3"));
    }

    [Fact]
    public void CollapsedTriangleMisses()
    {
        var evaluator = Triangle(0, 2, 2f);
        evaluator.SetFloat("rayTriangleVertex2Axis0", 1f);
        evaluator.SetFloat("rayTriangleVertex2Axis1", -1f);
        Assert.Equal(0u, evaluator.Read("rayTriangleOutput3"));
    }

    [Fact]
    public void BarycentricModeReturnsUnnormalizedWeights()
    {
        var evaluator = Triangle(0, 2, 2f);
        evaluator.Set("rayTriangleBarycentrics", 1);
        evaluator.Set("rayTriangleId", 1 | (2 << 2));
        var denominator = evaluator.ReadFloat("rayTriangleOutput1");
        Assert.Equal(0.25f, evaluator.ReadFloat("rayTriangleOutput2") / denominator);
        Assert.Equal(0.5f, evaluator.ReadFloat("rayTriangleOutput3") / denominator);
    }

    private static RayExpressionEvaluator Triangle(uint type, int axis, float depth)
    {
        var evaluator = new RayExpressionEvaluator(NumericalModule.Value);
        evaluator.Set("rayTriangleType", type);
        evaluator.Set("rayTriangleBarycentrics", 0);
        evaluator.Set("rayTriangleId", 100);
        int[][] triangles = [[0, 1, 2], [1, 3, 2], [2, 3, 4], [2, 4, 0]];
        float[][] positions = [[-1f, -1f, depth], [1f, -1f, depth], [0f, 1f, depth]];
        for (var component = 0; component < 3; component++)
        {
            evaluator.SetFloat($"rayOrigin{component}", 0f);
            evaluator.SetFloat($"rayDirection{component}", component == axis ? 1f : 0f);
            for (var vertex = 0; vertex < 5; vertex++)
                evaluator.SetFloat($"rayTriangleVertex{vertex}Axis{component}", 100f);
        }
        for (var component = 0; component < 3; component++)
        {
            for (var vertex = 0; vertex < 3; vertex++)
                evaluator.SetFloat($"rayTriangleVertex{triangles[type][vertex]}Axis{(component + axis + 1) % 3}", positions[vertex][component]);
        }
        return evaluator;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoxExtentIsExclusiveAndSortingKeepsEqualDepths(bool half)
    {
        var evaluator = Boxes(half);
        var label = half ? "rayBox16" : "rayBox32";
        evaluator.SetFloat("rayExtent", 1f);
        Assert.Equal(uint.MaxValue, evaluator.Read($"{label}Output0"));
        evaluator.SetFloat("rayExtent", 2f);
        for (var child = 0; child < 4; child++)
            Assert.Equal((uint)child + 8, evaluator.Read($"{label}Output{child}"));
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    public void BoxGrowthAddsOneUlpWithoutGrowingExtent(uint growth, bool hit)
    {
        var evaluator = Boxes(false);
        evaluator.Set("rayBoxGrow", growth);
        evaluator.SetFloat("rayExtent", 4f);
        evaluator.SetFloat("rayBox32Child0Min0", MathF.BitIncrement(1.5f));
        evaluator.SetFloat("rayBox32Child0Max1", 1.5f);
        evaluator.Set("rayBoxSort", 0);
        Assert.Equal(hit ? 8u : uint.MaxValue, evaluator.Read("rayBox32Output0"));
        evaluator.SetFloat("rayExtent", MathF.BitIncrement(1.5f));
        Assert.Equal(uint.MaxValue, evaluator.Read("rayBox32Output0"));
    }

    [Fact]
    public void BoxSortingUsesEntryDistanceAndCanBeDisabled()
    {
        var evaluator = Boxes(false);
        evaluator.SetFloat("rayExtent", 10f);
        float[] entries = [4f, 2f, 3f, 1f];
        for (var child = 0; child < 4; child++)
            for (var axis = 0; axis < 3; axis++)
            {
                evaluator.SetFloat($"rayBox32Child{child}Min{axis}", entries[child]);
                evaluator.SetFloat($"rayBox32Child{child}Max{axis}", entries[child] + 1f);
            }
        Assert.Equal(new uint[] { 11, 9, 10, 8 }, Enumerable.Range(0, 4).Select(child => evaluator.Read($"rayBox32Output{child}")));
        evaluator.Set("rayBoxSort", 0);
        Assert.Equal(new uint[] { 8, 9, 10, 11 }, Enumerable.Range(0, 4).Select(child => evaluator.Read($"rayBox32Output{child}")));
    }

    [Theory]
    [InlineData(-2f, -1f)]
    [InlineData(float.NaN, 2f)]
    public void BoxRejectsBehindRayAndNan(float minimum, float maximum)
    {
        var evaluator = Boxes(false);
        evaluator.SetFloat("rayExtent", 10f);
        evaluator.Set("rayBoxSort", 0);
        evaluator.SetFloat("rayBox32Child0Min0", minimum);
        evaluator.SetFloat("rayBox32Child0Max0", maximum);
        Assert.Equal(uint.MaxValue, evaluator.Read("rayBox32Output0"));
    }

    private static RayExpressionEvaluator Boxes(bool half)
    {
        var evaluator = new RayExpressionEvaluator(NumericalModule.Value);
        var label = half ? "rayBox16" : "rayBox32";
        evaluator.Set("rayBoxGrow", 0);
        evaluator.Set("rayBoxSort", 1);
        for (var axis = 0; axis < 3; axis++)
        {
            evaluator.SetFloat($"rayOrigin{axis}", 0f);
            evaluator.SetFloat($"rayInverseDirection{axis}", 1f);
            for (var child = 0; child < 4; child++)
            {
                evaluator.Set($"{label}Child{child}", (uint)child + 8);
                evaluator.SetFloat($"{label}Child{child}Min{axis}", 1f);
                evaluator.SetFloat($"{label}Child{child}Max{axis}", 2f);
            }
        }
        return evaluator;
    }

    private static void ValidateWithInstalledSdk(byte[] spirv)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (sdk is null) return;
        var validator = Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val");
        if (!File.Exists(validator)) return;
        var path = Path.Combine(Path.GetTempPath(), $"sharpemu-ray-{Guid.NewGuid():N}.spv");
        try
        {
            File.WriteAllBytes(path, spirv);
            using var process = Process.Start(new ProcessStartInfo(validator)
            {
                ArgumentList = { "--target-env", "vulkan1.2", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })!;
            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
