// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed partial class ShaderPipelineCache
{
    private readonly HashSet<(ulong Address, uint Stages, bool AutoDraw)> _rejectedDumpKeys = [];
    private readonly string _rejectedDumpSession = Guid.NewGuid().ToString("N");

    public void DumpRejectedGraphics(RegisterBanks banks, bool autoDraw)
    {
        var address = banks.Shader.Vertex.ExportAddress;
        if (!CompiledShaderDump.ShouldWrite(address, null)) return;
        lock (_gate)
        {
            if (_rejectedDumpKeys.Count >= 64 ||
                !_rejectedDumpKeys.Add((address, banks.Context.ShaderStages, autoDraw))) return;
            try
            {
                var path = CompiledShaderDump.GetBasePath("rejected-graphics", address, 0) +
                    $".{_rejectedDumpSession}.{_rejectedDumpKeys.Count:D3}";
                var ranges = _registry.ReadDiagnosticCode(address);
                var files = new List<object>();
                for (var index = 0; index < ranges.Length; index++)
                {
                    var range = ranges[index];
                    var file = $"{path}.part-{index}.bin";
                    File.WriteAllBytes(file, range.Code);
                    files.Add(new { Address = $"0x{range.Address:X16}", Size = range.Code.Length, File = Path.GetFileName(file) });
                }
                var evidence = new
                {
                    Reason = "unsupported-geometry-stage",
                    AutoDraw = autoDraw,
                    banks.Context.ShaderStages,
                    Vertex = banks.Shader.Vertex,
                    Pixel = banks.Shader.Pixel,
                    banks.Context.ShaderInterface,
                    banks.UserConfig.GeometryEngineControl,
                    banks.UserConfig.PrimitiveType,
                    banks.Context.ColorTargets,
                    ShaderParts = files,
                };
                File.WriteAllText(path + ".json", JsonSerializer.Serialize(evidence,
                    new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
                Console.Error.WriteLine($"[SHADER][INFO] Rejected graphics dump: {path}.json");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or OverflowException or NotSupportedException or JsonException)
            {
                Console.Error.WriteLine($"[SHADER][WARN] Cannot dump the rejected graphics shader: {exception.Message}");
            }
        }
    }
}
