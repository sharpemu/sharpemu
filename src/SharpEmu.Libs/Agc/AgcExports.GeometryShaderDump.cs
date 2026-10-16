// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private static readonly ConcurrentDictionary<(ulong Front, ulong Back), byte> _dumpedGeometryShaders = new();

    private static void DumpFusedGeometryShader(
        CpuContext context,
        ulong frontCodeAddress,
        ulong frontHeaderAddress,
        ulong backCodeAddress,
        ulong backHeaderAddress)
    {
        if (!CompiledShaderDump.ShouldWrite(frontCodeAddress, null) ||
            !_dumpedGeometryShaders.TryAdd((frontCodeAddress, backCodeAddress), 0))
        {
            return;
        }

        if (!TryReadGeometryShaderPart(context, frontCodeAddress, frontHeaderAddress, out var frontCode, out var frontHeader) ||
            !TryReadGeometryShaderPart(context, backCodeAddress, backHeaderAddress, out var backCode, out var backHeader))
        {
            Console.Error.WriteLine($"[GPU][WARN] Cannot dump the fused geometry shader: front=0x{frontCodeAddress:X16} back=0x{backCodeAddress:X16}.");
            return;
        }

        try
        {
            var basePath = GetGeometryShaderDumpBasePath(frontCodeAddress, backCodeAddress);
            File.WriteAllBytes($"{basePath}.front.bin", frontCode);
            File.WriteAllBytes($"{basePath}.back.bin", backCode);
            File.WriteAllBytes($"{basePath}.front.header.bin", frontHeader);
            File.WriteAllBytes($"{basePath}.back.header.bin", backHeader);
            File.WriteAllLines($"{basePath}.txt",
            [
                $"front=0x{frontCodeAddress:X16} size=0x{frontCode.Length:X}",
                $"back=0x{backCodeAddress:X16} size=0x{backCode.Length:X}",
                Gen5ShaderTranslator.TryDecodeProgram(context, frontCodeAddress, out var program, out var error)
                    ? string.Join(Environment.NewLine, program.Instructions.Select(static instruction =>
                        $"0x{instruction.Pc:X4} {string.Join('_', instruction.Words.Select(static word => $"{word:X8}"))} {instruction.Opcode} {instruction.Control}"))
                    : $"decode-error={error}",
            ]);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[GPU][WARN] Cannot write the fused geometry shader dump: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"[GPU][WARN] Cannot write the fused geometry shader dump: {exception.Message}");
        }
    }

    internal static string GetGeometryShaderDumpBasePath(ulong frontCodeAddress, ulong backCodeAddress) =>
        $"{CompiledShaderDump.GetBasePath("geometry", frontCodeAddress, 0)}.back-{backCodeAddress:X16}";

    private static bool TryReadGeometryShaderPart(
        CpuContext context,
        ulong codeAddress,
        ulong headerAddress,
        out byte[] code,
        out byte[] header)
    {
        code = [];
        header = new byte[ShaderStructBytes];
        if (!context.Memory.TryRead(headerAddress, header) ||
            !context.TryReadUInt32(headerAddress + ShaderSizeOffset, out var codeSize) ||
            codeSize == 0 || codeSize > MaximumDeclaredShaderSizeBytes)
        {
            return false;
        }

        code = new byte[codeSize];
        return context.Memory.TryRead(codeAddress, code);
    }
}
