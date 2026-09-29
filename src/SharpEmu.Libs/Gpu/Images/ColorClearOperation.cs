// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

internal readonly record struct ColorClearOperation(
    uint ImageLayer, ulong MetadataAddress, ulong MetadataSize, byte MetadataCode, ClearColorValue Color);

internal struct UniformMetadataScan(byte expectedCode, ulong expectedBytes)
{
    private ulong _remainingBytes = expectedBytes;
    private bool _rejected = expectedBytes == 0;

    internal bool Complete => !_rejected && _remainingBytes == 0;

    internal bool Accept(ReadOnlySpan<byte> bytes)
    {
        if (_rejected || bytes.IsEmpty || (ulong)bytes.Length > _remainingBytes ||
            !NativeColorClear.IsUniform(bytes, expectedCode))
        {
            _rejected = true;
            return false;
        }

        _remainingBytes -= (ulong)bytes.Length;
        return true;
    }
}
