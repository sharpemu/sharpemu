// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// A formatted untyped load decodes the descriptor format at run time. Dword-aligned 32-bit
// raw-bit formats take a word-per-component branch; every other format the byte-wise decode.
public sealed class BufferFormatLoadTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void FormattedLoadBranchesBetweenTheWordAndByteDecodes(uint dwords)
    {
        var request = Request(Program(BufferLoad(0, 4, dwords: dwords, formatted: true), EndProgram(8)));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        // The word branch and the byte-wise branch join at one selection merge.
        Assert.Contains((ushort)SpirvOp.SelectionMerge, module.Opcodes);
        Assert.Contains((ushort)SpirvOp.BitFieldUExtract, module.Opcodes);
        ValidateWhenAvailable(shader.Spirv);
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
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
