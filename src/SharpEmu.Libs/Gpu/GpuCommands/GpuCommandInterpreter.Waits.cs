// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Packets;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed partial class GpuCommandInterpreter
{
    private const uint RewindValidBit = 1u << 31;
    private const uint RewindKnownBits = 0x8100_0000u;
    private const uint OcclusionDepthBlockCount = 16;
    private const ulong OcclusionCounterReadyBit = 1UL << 63;

    // Polls the label once; an unmet condition suspends the submission at this packet.
    internal void WaitOnMemory(uint compareFunction, ulong address, ulong reference, ulong mask, uint waitOperation, bool is64Bit)
    {
        if (address == 0)
        {
            throw _host.Fatal($"The wait address is zero: compare={compareFunction} reference=0x{reference:X16}.");
        }

        if (!WaitOperation.IsValid(waitOperation))
        {
            throw _host.Fatal($"The wait operation is not supported: operation=0x{waitOperation:X8} address=0x{address:X16}.");
        }

        if (!WaitOperation.ShouldExecute(waitOperation, ConditionalWaitEnabled))
        {
            return;
        }

        var value = is64Bit ? ReadQword(address) : ReadDword(address);
        if (!WaitOperation.TryCompare(value, reference, mask, compareFunction, out var satisfied))
        {
            throw _host.Fatal($"The wait compare function is unknown: function={compareFunction} address=0x{address:X16}.");
        }

        if (!satisfied && TryReadOwnWrite(address, is64Bit, out var ownValue))
        {
            _ = WaitOperation.TryCompare(ownValue, reference, mask, compareFunction, out satisfied);
        }

        if (!satisfied)
        {
            if (LogGpuWaits)
            {
                Suspend(
                    $"memory bits={(is64Bit ? 64 : 32)} operation={waitOperation} compare={compareFunction} " +
                    $"target=0x{address:X16} observed=0x{value:X16} reference=0x{reference:X16} mask=0x{mask:X16}");
            }
            else
            {
                Suspend();
            }
        }
    }

    // The wait bit is PredicationZPassWaitOp: 0 (kWaitForQueryResults) stalls the command
    // processor until the results arrive, 1 (kDoNotPredicateIfQueryResultsNotReady) never
    // stalls and leaves the tagged packets unpredicated while the results are outstanding.
    internal void SetPredication(uint condition, uint operation, uint waitOperation, ulong address)
    {
        ulong value;
        switch (operation)
        {
            case 0:
                // Clearing predication reads no memory, so no GPU result can be outstanding.
                PredicateSkip = false;
                return;
            case 1:
            {
                if (address == 0)
                {
                    throw _host.Fatal("The predication address is zero.");
                }

                value = 0;
                for (var depthBlock = 0u; depthBlock < OcclusionDepthBlockCount; depthBlock++)
                {
                    var pairAddress = address + ((ulong)depthBlock * 2 * sizeof(ulong));
                    var begin = ReadQword(pairAddress);
                    var end = ReadQword(pairAddress + sizeof(ulong));
                    if ((begin & end & OcclusionCounterReadyBit) == 0)
                    {
                        if (waitOperation == 0)
                        {
                            Suspend();
                        }
                        else
                        {
                            PredicateSkip = false;
                        }

                        return;
                    }

                    value = unchecked(value + (end - begin));
                }

                break;
            }
            case 3:
            {
                if (address == 0)
                {
                    throw _host.Fatal("The predication address is zero.");
                }

                if (waitOperation != 0)
                {
                    // The results are outstanding while work is queued behind this packet, and
                    // the game asked for the packets to run unpredicated in that case.
                    PredicateSkip = false;
                    return;
                }

                _host.FlushAndWait();
                value = ReadQword(address);
                break;
            }

            default:
                throw _host.Fatal($"The predication operation is unknown: operation=0x{operation:X8} address=0x{address:X16}.");
        }

        PredicateSkip = condition switch
        {
            0 => value != 0,
            1 => value == 0,
            _ => throw _host.Fatal($"The predication condition is unknown: condition=0x{condition:X8} address=0x{address:X16}."),
        };
    }

    internal void WaitForConstantEngine()
    {
        if (ConstantEngineCount <= DrawEngineCount && !ConstantEngineComplete)
        {
            Suspend();
        }
    }

    internal void WaitForDrawEngineDifference(uint difference)
    {
        if (DrawEngineCount > ConstantEngineCount)
        {
            throw _host.Fatal($"The DE counter is ahead of the CE counter: de={DrawEngineCount} ce={ConstantEngineCount}.");
        }

        if (ConstantEngineCount - DrawEngineCount >= difference)
        {
            Suspend();
        }
    }

    internal void WaitForRewind(bool valid)
    {
        if (!valid)
        {
            Suspend();
        }
    }

    internal uint WaitRegisterMemory32Packet(in PacketContext packet, ReadOnlySpan<uint> payload) =>
        WaitRegisterMemoryPacket(packet, payload, is64Bit: false);

    internal uint WaitRegisterMemory64Packet(in PacketContext packet, ReadOnlySpan<uint> payload) =>
        WaitRegisterMemoryPacket(packet, payload, is64Bit: true);

    private uint WaitRegisterMemoryPacket(in PacketContext packet, ReadOnlySpan<uint> payload, bool is64Bit)
    {
        var valueDwords = is64Bit ? 2u : 1u;
        var payloadDwords = 4u + (valueDwords * 2u);
        var opcode = is64Bit ? PacketOpcode.WaitRegisterMemory64 : PacketOpcode.WaitRegisterMemory;
        if (packet.Opcode != opcode || packet.CustomCode != 0 || packet.Length != payloadDwords + 1)
        {
            throw _host.Fatal($"The wait packet header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var alignmentMask = is64Bit ? 0x7u : 0x3u;
        var control = payload[0];
        var address = (payload[1] & ~alignmentMask) | ((ulong)(payload[2] & 0x3FFFFu) << 32);
        var reference = ReadValue(payload, 3, is64Bit);
        var mask = ReadValue(payload, 3 + (int)valueDwords, is64Bit);
        if ((control & 0x10u) == 0)
        {
            throw _host.Fatal($"A register-space wait is not supported: control=0x{control:X8} address=0x{packet.PacketAddress:X16}.");
        }

        WaitOnMemory(control & 0x7u, address, reference, mask, WaitOperation.Decode(control, is64Bit), is64Bit);
        return payloadDwords;
    }

    private static ulong ReadValue(ReadOnlySpan<uint> payload, int index, bool is64Bit) =>
        is64Bit ? payload[index] | ((ulong)payload[index + 1] << 32) : payload[index];

    internal uint ConditionalExecutePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var payloadDwords = packet.Length - 1;
        if (payloadDwords < 4)
        {
            throw _host.Fatal($"The conditional-execute packet is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        var address = ((ulong)payload[1] << 32) | (payload[0] & 0xFFFF_FFFCu);
        var control = payload[0] & 0x3u;
        var executeCount = payload[3] & 0x3FFFu;
        if (control != 0 || payload[2] != 0 || address == 0 || payloadDwords + executeCount >= packet.Remaining)
        {
            throw _host.Fatal(
                $"The conditional-execute packet is not supported: control={control} reserved=0x{payload[2]:X8} " +
                $"address=0x{address:X16} count={executeCount} remaining={packet.Remaining}.");
        }

        return ReadDword(address) == 0 ? payloadDwords + executeCount : payloadDwords;
    }

    internal uint SetPredicationPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var payloadDwords = packet.Length - 1;
        if (payloadDwords < 2)
        {
            throw _host.Fatal($"The predication packet is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        const uint flagsMask = 0x0007_1100u;
        uint flags;
        ulong address;
        if (payloadDwords >= 3 && (payload[0] & ~flagsMask) == 0 && payload[2] <= 0xFFFFu)
        {
            flags = payload[0];
            address = ((ulong)payload[2] << 32) | (payload[1] & 0xFFFF_FFF0u);
        }
        else
        {
            flags = payload[1];
            address = (payload[0] & 0xFFFF_FFF0u) | ((ulong)(payload[1] & 0xFFu) << 32);
        }

        SetPredication((flags >> 8) & 0x1u, (flags >> 16) & 0x7u, (flags >> 12) & 0x1u, address);
        return payloadDwords;
    }

    internal uint RewindPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(2, PacketOpcode.Rewind) || (payload[0] & ~RewindKnownBits) != 0)
        {
            throw _host.Fatal($"The rewind packet is not supported: header=0x{packet.Header:X8} body=0x{payload[0]:X8} address=0x{packet.PacketAddress:X16}.");
        }

        WaitForRewind((payload[0] & RewindValidBit) != 0);
        return 1;
    }

    internal uint IncrementCeCounterPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireExactPacket(packet, PacketHeader.Make(2, PacketOpcode.IncrementCeCounter), payload[0], 1);
        ConstantEngineCount++;
        return 1;
    }

    internal uint IncrementDeCounterPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireExactPacket(packet, PacketHeader.Make(2, PacketOpcode.IncrementDeCounter), payload[0], 0);
        DrawEngineCount++;
        return 1;
    }

    internal uint WaitOnCeCounterPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireExactPacket(packet, PacketHeader.Make(2, PacketOpcode.WaitOnCeCounter), payload[0], 1);
        WaitForConstantEngine();
        return 1;
    }

    internal uint WaitOnDeCounterDiffPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(2, PacketOpcode.WaitOnDeCounterDiff))
        {
            throw _host.Fatal($"The DE counter wait header is not supported: header=0x{packet.Header:X8}.");
        }

        WaitForDrawEngineDifference(payload[0]);
        return 1;
    }

    private void RequireExactPacket(in PacketContext packet, uint header, uint body, uint expectedBody)
    {
        if (packet.Header != header || body != expectedBody)
        {
            throw _host.Fatal($"The packet is not supported: header=0x{packet.Header:X8} body=0x{body:X8} address=0x{packet.PacketAddress:X16}.");
        }
    }
}
