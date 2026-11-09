// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Images;

public sealed class MipStatistics
{
    public const int SlotCount = 256;
    public const uint FirstCounterOffset = 64;
    public const int ReportBytes = (int)FirstCounterOffset + SlotCount * CounterBytes;
    public const uint NoMipSampled = 15;
    public const uint CounterWords = 257;
    public const ulong CounterBufferBytes = 2 * CounterWords * sizeof(uint);
    public const uint NoCounterField = 0x8000;
    public const uint ReportWithoutCounters = 1;

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

    public void NoteSampled(in TextureDescriptorWords texture, uint statistics)
    {
        if (statistics == ResourceSnapshot.NoMipStatistics)
        {
            NoteSampled(texture);
        }
        else if ((statistics & ResourceSnapshot.MipStatisticsEnabled) != 0 && !texture.IsNull)
        {
            _bindingsBySlot[statistics & ResourceSnapshot.MipStatisticsSlotWordMask]++;
        }
    }

    public static uint ImageField(ReadOnlySpan<uint> texture, uint statistics)
    {
        if (statistics == ResourceSnapshot.NoMipStatistics)
        {
            return ImageField(texture);
        }

        if ((statistics & ResourceSnapshot.MipStatisticsEnabled) == 0)
        {
            return NoCounterField;
        }

        var baseLevel = (texture[3] >> 12) & 0xF;
        var minLod = (texture[1] >> 8) & 0xFFF;
        return (statistics & ResourceSnapshot.MipStatisticsSlotWordMask) | (baseLevel << 8) | (minLod << 16);
    }

    public static uint ImageField(ReadOnlySpan<uint> texture)
    {
        if (((texture[5] >> 25) & 1) == 0)
        {
            return NoCounterField;
        }

        var counter = texture[6] & 0xFF;
        var baseLevel = (texture[3] >> 12) & 0xF;
        var minLod = (texture[1] >> 8) & 0xFFF;
        return counter | (baseLevel << 8) | (minLod << 16);
    }

    public static void PackReport(ReadOnlySpan<uint> words, Span<byte> report)
    {
        report.Clear();
        if (report.Length >= sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(report, FirstCounterOffset);
        }

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var start = (int)FirstCounterOffset + slot * CounterBytes;
            if (start + CounterBytes > report.Length)
            {
                break;
            }

            var finest = words[slot];
            var count = Math.Min(words[(int)CounterWords + slot], MaxSamples);
            var level = finest == uint.MaxValue ? NoMipSampled : Math.Min(finest, 14u);
            var counter = report.Slice(start, CounterBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(counter, count | ((uint)slot << 24));
            BinaryPrimitives.WriteUInt32LittleEndian(counter[sizeof(uint)..], level << 24);
        }
    }

    public void WriteReport(Span<byte> report)
    {
        var hasCounters = TryWriteReport(report);
        if (report.Length >= sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(report, hasCounters ? FirstCounterOffset : ReportWithoutCounters);
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
