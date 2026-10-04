// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The binary information block a compiled shader can carry after its first two words.
public readonly record struct ShaderBinaryInfo(uint Hash0, uint Hash1)
{
    public const uint MarkerWord = 0xBEEB03FF;
    public const int ByteSize = 28;

    public ulong DeclaredHash => ((ulong)Hash1 << 32) | Hash0;
}

// A program is identified by its content hash, never by its address.
public static class ShaderIdentity
{
    // The declared hash from the marker block, or zero when the code carries none.
    public static bool TryReadDeclaredHash(ICpuMemory memory, ulong codeAddress, out ulong declaredHash)
    {
        declaredHash = 0;
        Span<byte> head = stackalloc byte[2 * sizeof(uint)];
        if (!memory.TryRead(codeAddress, head))
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != ShaderBinaryInfo.MarkerWord)
        {
            return true;
        }

        var infoAddress = codeAddress + ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) + 1) * 2 * sizeof(uint);
        Span<byte> info = stackalloc byte[ShaderBinaryInfo.ByteSize];
        if (!memory.TryRead(infoAddress, info))
        {
            return false;
        }

        declaredHash = new ShaderBinaryInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(info[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(info[20..])).DeclaredHash;
        return true;
    }

    // A single code object keeps its declared-or-content identity. Fused programs
    // combine the ordered identity of every half, so the entry's declared hash
    // cannot hide a changed continuation.
    public static ulong Compute(ICpuMemory memory, ulong codeAddress, ReadOnlySpan<(ulong Address, uint SizeBytes)> ranges, string label)
    {
        if (ranges.Length == 0)
        {
            return ComputeCodeObject(memory, codeAddress, 0, label);
        }

        if (ranges.Length == 1)
        {
            return ComputeCodeObject(memory, ranges[0].Address, ranges[0].SizeBytes, label);
        }

        var combined = new XxHash3();
        Span<byte> encodedHash = stackalloc byte[sizeof(ulong)];
        foreach (var (address, sizeBytes) in ranges)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                encodedHash,
                ComputeCodeObject(memory, address, sizeBytes, label));
            combined.Append(encodedHash);
        }

        return combined.GetCurrentHashAsUInt64();
    }

    // Registered shaders already expose their one or two ranges as scalar fields. Avoid
    // allocating CodeRanges on every draw while still reading (and, without a declared hash,
    // hashing) every code object so self-modifying shaders remain detectable.
    public static ulong Compute(ICpuMemory memory, RegisteredShader shader, string label)
    {
        if (!shader.IsFused)
        {
            return ComputeCodeObject(memory, shader.CodeAddress, shader.CodeSizeBytes, label);
        }

        var combined = new XxHash3();
        Span<byte> encodedHash = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            encodedHash,
            ComputeCodeObject(memory, shader.CodeAddress, shader.CodeSizeBytes, label));
        combined.Append(encodedHash);
        BinaryPrimitives.WriteUInt64LittleEndian(
            encodedHash,
            ComputeCodeObject(memory, shader.ContinuationAddress, shader.ContinuationSizeBytes, label));
        combined.Append(encodedHash);
        return combined.GetCurrentHashAsUInt64();
    }

    private static ulong ComputeCodeObject(
        ICpuMemory memory,
        ulong address,
        uint sizeBytes,
        string label)
    {
        if (!TryReadDeclaredHash(memory, address, out var declaredHash))
        {
            throw Scheduling.SubmissionScheduler.Fatal(
                $"The shader code is unreadable: label={label} shader=0x{address:X16}.");
        }

        if (declaredHash != 0)
        {
            return declaredHash;
        }

        var length = checked((int)sizeBytes);
        var code = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var contents = code.AsSpan(0, length);
            if (!memory.TryRead(address, contents))
            {
                throw Scheduling.SubmissionScheduler.Fatal(
                    $"The shader code is unreadable: label={label} shader=0x{address:X16} size=0x{sizeBytes:X8}.");
            }

            return XxHash3.HashToUInt64(contents);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(code);
        }
    }

}
