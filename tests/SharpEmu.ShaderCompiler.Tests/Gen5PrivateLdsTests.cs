// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// Graphics stages model LDS as a per-invocation private array that reads zero where the
// invocation has not written. A written-dword bitmap provides that instead of a zero
// initializer over the whole array.
public sealed class Gen5PrivateLdsTests
{
    private const uint PrivateStorageClass = 6;
    private const uint WorkgroupStorageClass = 4;

    [Fact]
    public void VertexStageLdsIsLazilyZeroedThroughAWrittenBitmap()
    {
        var spirv = Compile(ShaderStage.Vertex);

        var module = new SpirvModuleInspector(spirv);
        var lds = module.Names.Single(pair => pair.Value == "lds").Key;
        var written = module.Names.Single(pair => pair.Value == "ldsWritten").Key;
        Assert.Equal(PrivateStorageClass, module.VariableStorageClasses[lds]);
        Assert.Equal(PrivateStorageClass, module.VariableStorageClasses[written]);
        Assert.False(HasInitializer(spirv, lds));
        Assert.True(HasInitializer(spirv, written));
        ValidateWhenAvailable(spirv);
    }

    [Fact]
    public void ComputeLdsStaysWorkgroupMemoryWithoutABitmap()
    {
        var spirv = Compile(ShaderStage.Compute);

        var module = new SpirvModuleInspector(spirv);
        var lds = module.Names.Single(pair => pair.Value == "lds").Key;
        Assert.Equal(WorkgroupStorageClass, module.VariableStorageClasses[lds]);
        Assert.DoesNotContain("ldsWritten", module.Names.Values);
    }

    private static byte[] Compile(ShaderStage stage)
    {
        var program = Program(
            Vop2(0, "VLshlrevB32", 3, Operand(2), Gen5Operand.Vector(0)),
            DataShare(4, "DsWriteB32", false, [Gen5Operand.Vector(3), Gen5Operand.Vector(0)], []),
            DataShare(12, "DsReadB32", false, [Gen5Operand.Vector(3)], [5], 4),
            EndProgram(20));
        var request = Request(program, stage);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static void ValidateWhenAvailable(byte[] code)
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
            var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // OpVariable carries an initializer operand after its storage class.
    private static bool HasInitializer(byte[] spirv, uint variable)
    {
        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = (int)(words[offset] >> 16);
            if ((SpirvOp)(words[offset] & 0xFFFF) == SpirvOp.Variable && words[offset + 2] == variable)
            {
                return wordCount > 4;
            }

            offset += Math.Max(wordCount, 1);
        }

        throw new InvalidOperationException($"Variable {variable} is not declared.");
    }
}
