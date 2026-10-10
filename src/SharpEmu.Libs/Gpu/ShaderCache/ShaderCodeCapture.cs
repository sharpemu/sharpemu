// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal sealed class RecordingCpuMemory(ICpuMemory inner) : ICpuMemory, ICpuMemoryWrapper
{
    private readonly List<(ulong Address, byte[] Bytes)> _reads = [];

    public ICpuMemory Inner => inner;

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        if (!inner.TryRead(virtualAddress, destination))
        {
            return false;
        }

        _reads.Add((virtualAddress, destination.ToArray()));
        return true;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;

    public CodeRange[] TakeRanges()
    {
        _reads.Sort(static (left, right) => left.Address.CompareTo(right.Address));
        var ranges = new List<CodeRange>();
        ulong start = 0;
        var bytes = new List<byte>();
        foreach (var (address, data) in _reads)
        {
            var end = start + (ulong)bytes.Count;
            if (bytes.Count != 0 && address <= end)
            {
                var overlap = (int)(end - address);
                if (overlap < data.Length)
                {
                    bytes.AddRange(data.AsSpan(overlap).ToArray());
                }

                continue;
            }

            if (bytes.Count != 0)
            {
                ranges.Add(new CodeRange(start, bytes.ToArray()));
            }

            start = address;
            bytes.Clear();
            bytes.AddRange(data);
        }

        if (bytes.Count != 0)
        {
            ranges.Add(new CodeRange(start, bytes.ToArray()));
        }

        _reads.Clear();
        return ranges.ToArray();
    }
}

internal sealed class ReplayCpuMemory(IReadOnlyList<CodeRange> ranges) : ICpuMemory
{
    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        foreach (var range in ranges)
        {
            if (virtualAddress < range.Address)
            {
                continue;
            }

            var offset = virtualAddress - range.Address;
            if (offset + (ulong)destination.Length <= (ulong)range.Bytes.Length)
            {
                range.Bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
                return true;
            }
        }

        return false;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
}

internal readonly record struct CodeRange(ulong Address, byte[] Bytes);

internal readonly record struct FusedCodeParts(ulong EntryHeaderAddress, ulong ContinuationAddress, ulong ContinuationHeaderAddress);

internal readonly record struct ShaderCodeKey(ulong Hash, uint CodeSize, ulong Address);

internal sealed class ShaderCodeCapture
{
    public required ulong Hash { get; init; }
    public required uint CodeSize { get; init; }
    public required ulong Address { get; init; }
    public required Generation Generation { get; init; }
    public FusedCodeParts? Fused { get; init; }
    public required CodeRange[] Ranges { get; init; }

    public ShaderCodeKey Key => new(Hash, CodeSize, Address);

    public CpuContext CreateContext()
    {
        var context = new CpuContext(new ReplayCpuMemory(Ranges), Generation);
        if (Fused is { } fused)
        {
            Gen5ShaderTranslator.RegisterFusedProgram(
                context, Address, fused.EntryHeaderAddress, fused.ContinuationAddress, fused.ContinuationHeaderAddress);
        }

        return context;
    }
}
