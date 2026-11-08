// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.GnmMode.Shaders;

internal static class GcnImageCopy
{
    private static readonly ConcurrentDictionary<string, byte> ReportedCopies = new();
    // Gnmx's one-dword-per-thread copy, with EXEC masked to the destination record count.
    private static readonly uint[] CopyCode =
    [
        0xBEEB03FF, 7, 0x8F00860C, 0x4A000000, 0xE0002000, 0x80010100,
        0x7D88000A, 0xBE80246A, 0xBF8C0F70, 0xE0102000, 0x80020100, 0xBF810000,
    ];
    private static readonly uint[] CopyCodeFromS0 =
    [
        0xBEEB03FF, 9, 0x936AC008, 0xD2BA0000, 0x0401006A, 0xE0002000, 0x80000100,
        0xD1A80000, 0x00020006, 0xBF8C0F70, 0xE0102000, 0x80010100, 0xBF810000,
    ];

    public static bool TryResolve(ICpuMemory memory, RegisteredShader shader, ReadOnlySpan<uint> userData,
        ComputeInputInfo input, Gen5ComputeSystemRegisters system, uint x, uint y, uint z,
        Func<ulong, ulong?>? allocate, out ImageRequest source, out ImageRequest destination)
    {
        source = destination = default;
        var sourceRegister = shader.CodeSizeBytes == CopyCode.Length * sizeof(uint) ? 4 : 0;
        var expectedCode = sourceRegister == 4 ? CopyCode : CopyCodeFromS0;
        if (shader.CodeSizeBytes != expectedCode.Length * sizeof(uint) || userData.Length < sourceRegister + 8)
            return false;
        Span<uint> code = stackalloc uint[expectedCode.Length];
        if (!memory.TryRead(shader.CodeAddress, MemoryMarshal.AsBytes(code)) || !code.SequenceEqual(expectedCode))
            return false;
        if (GnmDiagnostics.Log && ReportedCopies.Count < 64)
        {
            var details = $"shader=0x{shader.CodeAddress:X} user={sourceRegister + 8} local={input.ThreadsX}x{input.ThreadsY}x{input.ThreadsZ} " +
                $"groups={x}x{y}x{z} threadDimensions={input.DispatchThreadDimensions} system={system} " +
                $"from={string.Join(',', userData.Slice(sourceRegister, 4).ToArray().Select(word => word.ToString("X8")))} " +
                $"to={string.Join(',', userData.Slice(sourceRegister + 4, 4).ToArray().Select(word => word.ToString("X8")))}";
            if (ReportedCopies.TryAdd(details, 0))
                Console.Error.WriteLine($"[GNMMODE][TRACE] Surface copy candidate {details}.");
        }
        if (system.WorkGroupXRegister != sourceRegister + 8 ||
            system.WorkGroupYRegister is not null || system.WorkGroupZRegister is not null ||
            system.ThreadGroupSizeRegister is not null || input.ThreadsX != 64 || input.ThreadsY != 1 || input.ThreadsZ != 1 ||
            y != 1 || z != 1)
            return false;
        var from = BufferDescriptorWords.From(GcnBufferDescriptor.Normalize(userData.Slice(sourceRegister, 4)));
        var to = BufferDescriptorWords.From(GcnBufferDescriptor.Normalize(userData.Slice(sourceRegister + 4, 4)));
        var threads = input.DispatchThreadDimensions ? x : (ulong)x * 64;
        if (!IsWordBuffer(from) || !IsWordBuffer(to) || from.RecordCount != to.RecordCount ||
            threads != from.RecordCount || from.Address == to.Address)
            return false;

        // The GCN allocation and the shared host tile layout need not have the same
        // footprint. A full surface copy must preserve texels, not copy a prefix of host tiles.
        var size = (ulong)from.RecordCount * 4;
        if (from.Address > ulong.MaxValue - size || to.Address > ulong.MaxValue - size ||
            from.Address < to.Address + size && to.Address < from.Address + size)
            return false;
        return GcnImageDescriptor.TryResolveColorCopy(from.Address, to.Address, size, out source, out destination) ||
            GcnImageDescriptor.TryResolveDepthCopy(from.Address, to.Address, size, allocate, out source, out destination);
    }

    private static bool IsWordBuffer(BufferDescriptorWords buffer) =>
        buffer.Address != 0 && (buffer.Address & 255) == 0 && buffer.Stride == 4 &&
        !buffer.SwizzleEnabled && !buffer.AddThreadId && buffer.Type == 0 && buffer.OutOfBounds == 0 &&
        buffer.Format == BufferDescriptorWords.Format32UInt && buffer.DestinationSelectX == 4;
}
