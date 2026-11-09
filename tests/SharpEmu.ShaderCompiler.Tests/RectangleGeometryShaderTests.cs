// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class RectangleGeometryShaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PositionAndParameterExpansionPassesSpirvValidation(bool layered)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        module.SetLogicalGlsl450MemoryModel();
        var vector = module.TypeVector(module.TypeFloat(32), 4);
        var pointer = module.TypePointer(SpirvStorageClass.Output, vector);
        var position = module.AddGlobalVariable(pointer, SpirvStorageClass.Output);
        var parameter = module.AddGlobalVariable(pointer, SpirvStorageClass.Output);
        module.AddDecoration(position, SpirvDecoration.BuiltIn, 0);
        module.AddDecoration(parameter, SpirvDecoration.Location, 0);
        var interfaces = new List<uint> { position, parameter };
        if (layered)
        {
            module.AddExtension("SPV_EXT_shader_viewport_index_layer");
            module.AddCapability(SpirvCapability.ShaderViewportIndexLayerExt);
            var integerPointer = module.TypePointer(SpirvStorageClass.Output, module.TypeInt(32, true));
            foreach (var builtIn in new[] { 9u, 10u })
            {
                var output = module.AddGlobalVariable(integerPointer, SpirvStorageClass.Output);
                module.AddDecoration(output, SpirvDecoration.BuiltIn, builtIn);
                interfaces.Add(output);
            }
        }
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", interfaces);
        module.AddLabel();
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        var original = module.Build();
        var geometry = RectangleGeometryShader.Create(original, null, out var vertexCode);
        Assert.NotEmpty(geometry);
        Assert.Equal(!layered, original.SequenceEqual(vertexCode));
        Validate(vertexCode);
        Validate(geometry);
    }

    private static void Validate(byte[] code)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var validator = sdk is null ? null : Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val");
        if (validator is null || !File.Exists(validator)) return;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, code);
            var start = new ProcessStartInfo(validator) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var message = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30000), "SPIR-V validation timed out.");
            Assert.True(process.ExitCode == 0, message);
        }
        finally { File.Delete(path); }
    }
}
