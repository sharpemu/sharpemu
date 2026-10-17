// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Packets;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed partial class GpuCommandInterpreter
{
    private const uint MarkerMagic = 0x6875_0000u;
    private const uint UserDataMarkerBufferResource = 1;
    private const uint UserDataMarkerRegion = 2;

    internal uint EventWritePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var eventIndex = (payload[0] >> 8) & 0x7u;
        var eventType = payload[0] & 0x3Fu;
        ulong eventAddress = 0;
        if (eventType == 0x39)
        {
            if (packet.Length != 4)
            {
                throw _host.Fatal($"The occlusion event packet length is not supported: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
            }

            eventAddress = Address(payload[1], payload[2]);
        }

        RaiseEvent(eventType, eventIndex, eventAddress);
        return packet.Length - 1;
    }

    internal uint EventWriteEndOfPipePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(6, PacketOpcode.EventWriteEndOfPipe));
        var control = payload[0];
        var destination = payload[1] | ((ulong)(payload[2] & 0xFFFFu) << 32);
        WriteEndOfPipe(
            is64Bit: true,
            cachePolicy: (control >> 25) & 0x3u,
            eventWriteDestination: ((control >> 23) & 0x10u) | ((payload[2] >> 16) & 0x01u),
            eopEventType: control & 0x3Fu,
            cacheAction: (control >> 12) & 0x3Fu,
            eventIndex: (control >> 8) & 0x7u,
            eventWriteSource: (payload[2] >> 29) & 0x7u,
            destination,
            value: Address(payload[3], payload[4]),
            interruptSelector: (payload[2] >> 24) & 0x7u,
            interruptContextId: 0);
        return 5;
    }

    internal uint EventWriteEndOfShaderPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        // The builder sets header bit 1, so the whole header is compared.
        RequireHeader(packet, 0xC003_4802u);
        var control = payload[0];
        var destination = payload[1] | ((ulong)(payload[2] & 0xFFFFu) << 32);
        WriteEndOfPipe(
            is64Bit: false,
            cachePolicy: (control >> 25) & 0x3u,
            eventWriteDestination: 0,
            eopEventType: control & 0x3Fu,
            cacheAction: (control >> 12) & 0x3Fu,
            eventIndex: (control >> 8) & 0x7u,
            eventWriteSource: (payload[2] >> 29) & 0x7u,
            destination,
            value: payload[3],
            interruptSelector: (payload[2] >> 24) & 0x7u,
            interruptContextId: 0);
        return 4;
    }

    // Native release-memory body: event control, then destination and selectors.
    internal uint ReleaseMemoryNativePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(8, PacketOpcode.ReleaseMemory));
        ReleaseMemoryFromNativeFields(payload);
        return 7;
    }

    private void ReleaseMemoryFromNativeFields(ReadOnlySpan<uint> payload)
    {
        var eventControl = payload[0];
        var control = payload[1];
        ReleaseMemory(
            cachePolicy: (eventControl >> 25) & 0x3u,
            eopEventType: eventControl & 0x3Fu,
            eventIndex: (eventControl >> 8) & 0x7u,
            gcrControl: (eventControl >> 12) & 0xFFFu,
            releaseDestination: (control >> 16) & 0x3u,
            dataSelection: (control >> 29) & 0x7u,
            interruptSelector: (control >> 24) & 0x7u,
            destination: Address(payload[2], payload[3]),
            value: Address(payload[4], payload[5]),
            interruptContextId: payload[6] & 0x07FF_FFFFu);
    }

    // The library builder packs the wrapped form as action | policy << 8, then
    // gcr | selection << 16 | interrupt << 24; the event index follows the action.
    internal uint ReleaseMemoryWrappedPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(8, PacketOpcode.Nop, PacketCustomCode.ReleaseMemory));
        var action = payload[0] & 0x3Fu;
        var control = payload[1];
        var gcrControl = control & 0xFFFFu;
        var dataSelection = (control >> 16) & 0xFFu;
        var interruptSelector = (control >> 24) & 0xFFu;
        var destination = Address(payload[2], payload[3]);
        var data = Address(payload[4], payload[5]);
        var contextId = payload[6] & 0x07FF_FFFFu;
        var eventIndex = action >= 0x2F ? 6u : 5u;

        // Selectors 5 and 6 raise only when the label already reached the data.
        if (interruptSelector is 5 or 6)
        {
            var conditionReadable = destination != 0;
            var conditionValue = 0UL;
            if (conditionReadable)
            {
                conditionValue = interruptSelector == 5 ? ReadDword(destination) : ReadQword(destination);
            }

            var decision = InterruptDecision.Evaluate(interruptSelector, IsComputeQueue, dataSelection, conditionReadable, conditionValue, data);
            ReleaseMemory(0, action, eventIndex, gcrControl, 0, 0, decision.RaisesInterrupt ? 4u : 0u, 0, 0, contextId);
            return 7;
        }

        ReleaseMemory(0, action, eventIndex, gcrControl, 0, dataSelection, interruptSelector, destination, data, contextId);
        return 7;
    }

    internal uint AcquireMemoryPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header == PacketHeader.Make(8, PacketOpcode.Nop, PacketCustomCode.AcquireMemory))
        {
            return 7;
        }

        if (packet.Header == PacketHeader.Make(7, PacketOpcode.AcquireMemory))
        {
            return 6;
        }

        throw _host.Fatal($"The acquire-memory header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
    }

    internal uint NopPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var customCode = packet.CustomCode;
        if (customCode == PacketCustomCode.Zero)
        {
            if ((payload[0] & 0xFFFF_0000u) == MarkerMagic)
            {
                return MarkerPacket(packet, payload);
            }

            return packet.Length - 1;
        }

        var handler = PacketDispatchTable.CustomCodes[customCode];
        if (handler is null)
        {
            throw _host.Fatal($"The custom packet code is unknown: offset=0x{packet.Offset:X5} code=0x{customCode:X2} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        return handler(this, in packet, payload);
    }

    private uint MarkerPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var markerId = payload[0] & 0xFFFu;
        switch (markerId)
        {
            case 0x0:
                break;
            case 0x4:
                UserDataMarker = UserDataMarkerBufferResource;
                break;
            case 0xD:
                UserDataMarker = UserDataMarkerRegion;
                break;
            case 0x777:
                SubmitFlip();
                break;
            case 0x778:
                SubmitFlipWithLabel(Address(payload[1], payload[2]), payload[3]);
                break;
            case 0x781:
                SubmitFlipWithInterrupt(payload[4], payload[5], Address(payload[1], payload[2]), payload[3]);
                break;
            default:
                // A marker id this interpreter has no side effect for (profiling/debug annotations such as
                // 0x6EC and 0x80C in Demon's Souls). The packet carries no work, so it is consumed; the id is
                // reported once so a marker that later turns out to matter is still visible.
                NoteUnknownMarker(markerId, packet);
                break;
        }

        return packet.Length - 1;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _reportedUnknownMarkers = new();

    private static void NoteUnknownMarker(uint markerId, in PacketContext packet)
    {
        if (_reportedUnknownMarkers.TryAdd(markerId, true))
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] command_stream.marker_ignored marker=0x{markerId:X} offset=0x{packet.Offset:X5} address=0x{packet.PacketAddress:X16}");
        }
    }

    internal uint PushMarkerPacket(in PacketContext packet, ReadOnlySpan<uint> payload) => packet.Length - 1;

    internal uint PopMarkerPacket(in PacketContext packet, ReadOnlySpan<uint> payload) => packet.Length - 1;

    internal uint FlipPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(6, PacketOpcode.Nop, PacketCustomCode.Flip));
        SetFlip(new FlipRequest((int)payload[0], (int)payload[1], (int)payload[2], (long)Address(payload[3], payload[4])));
        SubmitFlip();
        return 5;
    }

    internal uint WaitFlipDonePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(7, PacketOpcode.Nop, PacketCustomCode.WaitFlipDone));
        WaitForFlip(payload[0], payload[1]);
        return 6;
    }

    internal uint ClearStatePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(2, PacketOpcode.ClearState));
        if ((payload[0] & ~0xFu) != 0)
        {
            throw _host.Fatal($"The clear-state flags are not supported: flags=0x{payload[0]:X8} address=0x{packet.PacketAddress:X16}.");
        }

        TypedRegisters.ApplyContextState(ContextStateOperation.Clear);
        return 1;
    }

    internal uint ContextStatePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Length is not (3 or 5) || payload[0] > (uint)ContextStateOperation.PushClear)
        {
            throw _host.Fatal($"The context-state packet is not supported: length={packet.Length} operation={payload[0]} address=0x{packet.PacketAddress:X16}.");
        }

        TypedRegisters.ApplyContextState((ContextStateOperation)payload[0]);
        return packet.Length - 1;
    }

    internal uint DispatchResetPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(2, PacketOpcode.Nop, PacketCustomCode.DispatchReset));
        ResetForQueueReset();
        return 1;
    }

    internal uint DrawResetPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        ResetForQueueReset();
        return packet.Length - 1;
    }

    internal uint PfpSyncMePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(2, PacketOpcode.PfpSyncMe));
        return 1;
    }
}
