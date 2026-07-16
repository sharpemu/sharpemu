// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// Read-only guest memory holding hand-assembled instruction words for the decoder.
// Copied (not shared) from the ShaderDump tool on purpose: shader-codegen test projects
// stay self-contained so each backend's suite can serve as a standalone model.
internal sealed class FakeGuestMemory : ICpuMemory
{
    private readonly List<(ulong Base, byte[] Data)> _regions = [];

    public void AddRegion(ulong baseAddress, uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        _regions.Add((baseAddress, bytes));
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        foreach (var (baseAddress, data) in _regions)
        {
            if (virtualAddress >= baseAddress &&
                virtualAddress + (ulong)destination.Length <= baseAddress + (ulong)data.Length)
            {
                data.AsSpan(
                    (int)(virtualAddress - baseAddress),
                    destination.Length).CopyTo(destination);
                return true;
            }
        }

        return false;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
}
