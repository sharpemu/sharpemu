// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Ir;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Rdna2MemoryControlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataShareSubwordAndSt64PairVariantsHaveRealSpirvLowering(bool gds)
    {
        var program = Program(
            MoveVector(0, 0, 0),
            MoveVector(4, 1, 0xA1B2C3D4),
            MoveVector(8, 2, 0x76543210),
            DataShare(12, "DsWriteB8", gds, [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], 1),
            DataShare(20, "DsWriteB16", gds, [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], 3),
            DataShare(28, "DsWriteB8D16Hi", gds, [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], 3),
            DataShare(36, "DsWriteB16D16Hi", gds, [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], 3),
            DataShare(44, "DsReadU8", gds, [Gen5Operand.Vector(0)], [3], 1),
            DataShare(52, "DsReadI16", gds, [Gen5Operand.Vector(0)], [4], 3),
            DataShare(60, "DsReadU16", gds, [Gen5Operand.Vector(0)], [5], 3),
            DataShare(68, "DsReadU16D16", gds, [Gen5Operand.Vector(0)], [6], 3),
            DataShare(76, "DsReadU16D16Hi", gds, [Gen5Operand.Vector(0)], [7], 3),
            DataShare(84, "DsRead2St64B64", gds, [Gen5Operand.Vector(0)], [8, 9, 10, 11], 37, 41),
            EndProgram(92));

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(Request(program), out var shader, out var error),
            error);

        var inspector = new Resources.SpirvModuleInspector(shader.Spirv);
        ValidateWhenAvailable(shader.Spirv);
        Assert.Contains((ushort)SpirvOp.AtomicCompareExchange, inspector.Opcodes);
        Assert.True(
            CountOpcode(shader.Spirv, SpirvOp.AtomicCompareExchange) >= 6,
            "B16 writes at byte offset three must update both affected shared dwords atomically.");
        Assert.Contains((ushort)SpirvOp.BitFieldUExtract, inspector.Opcodes);
        Assert.Contains((ushort)SpirvOp.BitFieldSExtract, inspector.Opcodes);
        Assert.Contains((ushort)SpirvOp.Select, inspector.Opcodes);
        var constants = ReadUintConstants(shader.Spirv);
        Assert.Contains(0x000000FFu, constants);
        Assert.Contains(0xFFFF0000u, constants);
        Assert.Contains(0x0000FFFFu, constants);
        Assert.Contains(37u * 512u, constants);
        Assert.Contains(41u * 512u, constants);
    }

    [Fact]
    public void PrivateDataShareB16AtByteThreeUsesTwoNormalReadModifyWrites()
    {
        var program = Program(
            MoveVector(0, 0, 0),
            MoveVector(4, 1, 0xA1B2C3D4),
            DataShare(8, "DsWriteB16", false, [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], 3),
            DataShare(16, "DsWriteB16D16Hi", false, [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], 3),
            DataShare(24, "DsReadU16D16Hi", false, [Gen5Operand.Vector(0)], [2], 3),
            EndProgram(32));

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                Request(program, ShaderStage.Vertex),
                out var shader,
                out var error),
            error);

        var inspector = new Resources.SpirvModuleInspector(shader.Spirv);
        ValidateWhenAvailable(shader.Spirv);
        Assert.DoesNotContain((ushort)SpirvOp.AtomicCompareExchange, inspector.Opcodes);
        Assert.Contains((ushort)SpirvOp.Load, inspector.Opcodes);
        Assert.Contains((ushort)SpirvOp.Store, inspector.Opcodes);
        Assert.Contains((ushort)SpirvOp.Select, inspector.Opcodes);
        Assert.Contains(0x000000FFu, ReadUintConstants(shader.Spirv));
    }

    [Fact]
    public void MemRealtimeUsesDocumentedAllOnesSentinelPair()
    {
        var program = DecodeRaw(0xF4940300u, 0xFA000000u, 0xBF810000u);
        var realtime = program.Instructions[0];
        Assert.Equal("SMemrealtime", realtime.Opcode);
        Assert.Null(realtime.Control);
        Assert.Empty(realtime.Sources);
        Assert.Equal(
            [Gen5Operand.Scalar(12), Gen5Operand.Scalar(13)],
            realtime.Destinations);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(Request(program), out var shader, out var error),
            error);
        ValidateWhenAvailable(shader.Spirv);
        Assert.Contains(uint.MaxValue, ReadUintConstants(shader.Spirv));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    public void MemRealtimeUsesOneDeviceClockReadWhenSupported(uint clockShift)
    {
        var program = DecodeRaw(0xF4940300u, 0xFA000000u, 0xBF810000u);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            ShaderDeviceClockSupported = true,
            ShaderDeviceClockShift = clockShift,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        ValidateWhenAvailable(shader.Spirv);

        var inspector = new Resources.SpirvModuleInspector(shader.Spirv);
        Assert.Contains((uint)SpirvCapability.ShaderClockKhr, inspector.Capabilities);
        Assert.Equal(1, CountOpcode(shader.Spirv, SpirvOp.ReadClockKhr));
        Assert.Contains(1u, ReadUintConstants(shader.Spirv)); // SpvScopeDevice
        if (clockShift != 0)
        {
            Assert.Contains(clockShift, ReadUintConstants(shader.Spirv));
            Assert.Contains(32u - clockShift, ReadUintConstants(shader.Spirv));
            Assert.Contains((ushort)SpirvOp.ShiftRightLogical, inspector.Opcodes);
            Assert.Contains((ushort)SpirvOp.ShiftLeftLogical, inspector.Opcodes);
            Assert.Contains((ushort)SpirvOp.BitwiseOr, inspector.Opcodes);
        }
    }

    [Fact]
    public void SubvectorLoopIsConditionalCfgAndCompilesStateTransitions()
    {
        var program = DecodeRaw(
            0xBDA60002u,
            0xBF800000u,
            0xBE26FFFEu,
            0xBF810000u);
        var begin = program.Instructions[0];
        var end = program.Instructions[2];
        Assert.Equal("SSubvectorLoopBegin", begin.Opcode);
        Assert.Equal("SSubvectorLoopEnd", end.Opcode);
        Assert.Equal([Gen5Operand.Scalar(38)], begin.Destinations);
        Assert.Equal([Gen5Operand.Scalar(38)], end.Destinations);

        Assert.True(Gen5IrBranchResolver.Instance.IsConditional(begin));
        Assert.True(Gen5IrBranchResolver.Instance.TryGetBranchTarget(begin, out var beginTarget));
        Assert.Equal(12u, beginTarget);
        Assert.True(Gen5IrBranchResolver.Instance.TryGetBranchTarget(end, out var endTarget));
        Assert.Equal(4u, endTarget);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(Request(program), out var shader, out var error),
            error);
        var inspector = new Resources.SpirvModuleInspector(shader.Spirv);
        ValidateWhenAvailable(shader.Spirv);
        Assert.Contains((ushort)SpirvOp.Select, inspector.Opcodes);
        Assert.Contains((ushort)SpirvOp.Switch, inspector.Opcodes);
    }

    private static HashSet<uint> ReadUintConstants(byte[] spirv)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var values = new HashSet<uint>();
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = (int)(words[offset] >> 16);
            var opcode = (SpirvOp)(words[offset] & 0xFFFF);
            if (opcode == SpirvOp.Constant && wordCount == 4)
            {
                values.Add(words[offset + 3]);
            }
            offset += Math.Max(wordCount, 1);
        }
        return values;
    }

    private static int CountOpcode(byte[] spirv, SpirvOp expected)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var count = 0;
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = (int)(words[offset] >> 16);
            var opcode = (SpirvOp)(words[offset] & 0xFFFF);
            if (opcode == expected)
            {
                count++;
            }
            offset += Math.Max(wordCount, 1);
        }
        return count;
    }

    private static Gen5ShaderProgram DecodeRaw(params uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                new CpuContext(new InstructionMemory(bytes), Generation.Gen5),
                0x1000,
                out var program,
                out var error),
            error);
        return program;
    }

    private static void ValidateWhenAvailable(byte[] spirv)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (string.IsNullOrWhiteSpace(sdk))
        {
            return;
        }

        var executable = Path.Combine(
            sdk,
            OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val");
        if (!File.Exists(executable))
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, spirv);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
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

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
