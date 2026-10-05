// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Gpu.Images;

public sealed class MipStatistics
{
    public const int SlotCount = 256;
    public const uint FirstCounterOffset = 64;
    public const int ReportBytes = (int)FirstCounterOffset + SlotCount * CounterBytes;
    public const uint NoMipSampled = 15;

    private const int CounterBytes = 2 * sizeof(uint);
    private const uint FinestMip = 0;
    private const uint SamplesPerBinding = 0x10000;
    private const uint MaxSamples = 0xFFFFFF;

    public static MipStatistics Shared { get; } = new();

    private readonly uint[] _bindingsBySlot = new uint[SlotCount];

    public void NoteSampled(in TextureDescriptorWords texture)
    {
        if (texture.MipStatsEnabled && !texture.IsNull)
        {
            _bindingsBySlot[texture.MipStatsSlot]++;
        }
    }

    public bool TryWriteReport(Span<byte> report)
    {
        if (report.Length < ReportBytes)
        {
            return false;
        }

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var samples = (uint)Math.Min((ulong)_bindingsBySlot[slot] * SamplesPerBinding, MaxSamples);
            var finestMipSampled = samples == 0 ? NoMipSampled : FinestMip;
            var clampedSamplesAndSlot = samples | ((uint)slot << 24);
            var unclampedSamplesAndFinestMip = samples | (finestMipSampled << 24);
            var counter = report.Slice((int)FirstCounterOffset + slot * CounterBytes, CounterBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(counter, clampedSamplesAndSlot);
            BinaryPrimitives.WriteUInt32LittleEndian(counter[sizeof(uint)..], unclampedSamplesAndFinestMip);
        }

        Array.Clear(_bindingsBySlot);
        return true;
    }
}
