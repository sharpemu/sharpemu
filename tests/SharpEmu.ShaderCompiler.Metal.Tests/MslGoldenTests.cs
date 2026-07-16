// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Pins the emitted MSL for the synthetic fixtures. Codegen changes show up as a
/// readable text diff instead of a runtime mystery. Regenerate with
/// SHARPEMU_UPDATE_GOLDENS=1 (writes into the source tree) after intentional
/// emitter changes, then review the diff like any other code change.
/// </summary>
public sealed class MslGoldenTests
{
    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in Gen5ComputeFixtures.All)
        {
            data.Add(fixture.Name);
        }

        return data;
    }

    [Fact]
    public void PixelShaderMatchesGolden()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow();
        AssertMatchesGolden("pixel", shader.Source);
    }

    [Fact]
    public void VertexShaderMatchesGolden()
    {
        var shader = Gen5ComputeFixtures.CompileVertexOrThrow(requiredVertexOutputCount: 1);
        AssertMatchesGolden("vertex", shader.Source);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void EmittedMslMatchesGolden(string name)
    {
        Gen5ComputeFixture? fixture = null;
        foreach (var candidate in Gen5ComputeFixtures.All)
        {
            if (candidate.Name == name)
            {
                fixture = candidate;
                break;
            }
        }

        Assert.NotNull(fixture);
        var shader = Gen5ComputeFixtures.CompileOrThrow(fixture);
        AssertMatchesGolden(name, shader.Source);
    }

    private static void AssertMatchesGolden(string name, string source)
    {
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Goldens", $"{name}.msl");

        if (Environment.GetEnvironmentVariable("SHARPEMU_UPDATE_GOLDENS") == "1")
        {
            var sourcePath = Path.Combine(
                FindSourceDirectory(),
                "Goldens",
                $"{name}.msl");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, source);
            return;
        }

        Assert.True(File.Exists(goldenPath), $"missing golden {goldenPath}; run with SHARPEMU_UPDATE_GOLDENS=1 to create it");
        var expected = File.ReadAllText(goldenPath).ReplaceLineEndings();
        Assert.Equal(expected, source.ReplaceLineEndings());
    }

    private static string FindSourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        // Binaries land in artifacts/bin (outside the project directory), so
        // walking up from the test binary never finds the csproj; the compiler
        // records this file's source path instead.
        return Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("test project directory not found");
    }
}
