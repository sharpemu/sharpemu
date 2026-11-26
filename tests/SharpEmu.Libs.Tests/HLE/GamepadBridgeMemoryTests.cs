// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed unsafe class GamepadBridgeMemoryTests : IDisposable
{
    private readonly byte* _block = (byte*)NativeMemory.AllocZeroed(GamepadBridgeMemory.Size);
    private readonly GamepadBridgeMemory _memory;

    public GamepadBridgeMemoryTests()
    {
        _memory = new GamepadBridgeMemory(_block);
        _memory.Initialize();
    }

    public void Dispose() => NativeMemory.Free(_block);

    private static HostGamepadState FullState() => new(
        Connected: true,
        Buttons: HostGamepadButtons.Cross | HostGamepadButtons.L2 | HostGamepadButtons.Mic | HostGamepadButtons.Left,
        LeftX: 1, LeftY: 2, RightX: 250, RightY: 251, LeftTrigger: 64, RightTrigger: 255,
        Type: HostGamepadType.DualSense,
        Connection: HostGamepadConnection.Wireless,
        Motion: new HostMotionState(true, 0.5f, -1.5f, 9.8f, 0.1f, 0.2f, -0.3f, 0.25f, 0.5f, 0.75f, 0.125f),
        Touch: new HostTouchState(new HostTouchPoint(true, 7, 0.25f, 0.75f), new HostTouchPoint(false, 8, 0.5f, 0.125f)),
        BatteryPercent: 87);

    [Fact]
    public void HeaderIsValidOnlyAfterInitialize()
    {
        var raw = (byte*)NativeMemory.AllocZeroed(GamepadBridgeMemory.Size);
        try
        {
            var memory = new GamepadBridgeMemory(raw);
            Assert.False(memory.IsValid);
            memory.Initialize();
            Assert.True(memory.IsValid);
        }
        finally
        {
            NativeMemory.Free(raw);
        }
    }

    [Fact]
    public void LayoutFitsTheSection()
    {
        Assert.True(GamepadBridgeMemory.InputOffset + GamepadBridgeMemory.SlotHeaderSize + sizeof(InputPayload) <= GamepadBridgeMemory.RingOffset);
        Assert.Equal(32, sizeof(BridgeEvent));
        Assert.True(
            GamepadBridgeMemory.OutputOffset + GamepadBridgeMemory.OutputGroupCount * GamepadBridgeMemory.OutputGroupSize <=
            GamepadBridgeMemory.Size);
        Assert.True(sizeof(AdaptiveTriggerCommand) + GamepadBridgeMemory.SlotHeaderSize <= GamepadBridgeMemory.OutputGroupSize);
    }

    [Fact]
    public void InputRoundTripsEveryField()
    {
        var state = FullState();
        _memory.WriteInput(state, "DualSense Wireless Controller");

        Assert.True(_memory.TryReadInput(out var read, out var name));
        Assert.Equal(state, read);
        Assert.Equal("DualSense Wireless Controller", name);
    }

    [Fact]
    public void DisconnectedInputReadsAsDefault()
    {
        _memory.WriteInput(FullState(), "pad");
        _memory.WriteDisconnected();

        Assert.True(_memory.TryReadInput(out var read, out var name));
        Assert.False(read.Connected);
        Assert.Equal(default, read);
        Assert.Null(name);
    }

    [Fact]
    public void LongNamesAreCutAtACharacterBoundary()
    {
        var long_ = new string('a', 90) + "ééé";
        _memory.WriteInput(FullState(), long_);

        Assert.True(_memory.TryReadInput(out _, out var name));
        Assert.NotNull(name);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= GamepadBridgeMemory.MaxNameBytes);
        Assert.StartsWith(new string('a', 90), name);
        Assert.DoesNotContain('�', name);
    }

    [Fact]
    public void ReaderGivesUpWhileAWriteIsOpen()
    {
        _memory.WriteInput(FullState(), "first");
        GamepadBridgeMemory.BeginWrite(_memory.InputSlotPointer);

        // A writer stuck mid-update: the reader never returns the half-written payload.
        Assert.False(_memory.TryReadInput(out _, out _, attempts: 8));

        GamepadBridgeMemory.EndWrite(_memory.InputSlotPointer);
        Assert.True(_memory.TryReadInput(out var read, out var name, attempts: 8));
        Assert.Equal("first", name);
        Assert.Equal(FullState(), read);
    }

    [Fact]
    public void TornReadIsRetriedAndReturnsTheNewState()
    {
        var first = FullState();
        var second = first with { Buttons = HostGamepadButtons.Triangle, LeftX = 99, BatteryPercent = 12 };
        _memory.WriteInput(first, "one");

        // The writer updates between the reader's copy and its sequence check, once.
        var interfered = 0;
        var ok = _memory.TryReadInput(out var read, out var name, afterCopy: () =>
        {
            if (interfered++ == 0)
            {
                _memory.WriteInput(second, "two");
            }
        });

        Assert.True(ok);
        Assert.Equal(2, interfered);
        Assert.Equal(second, read);
        Assert.Equal("two", name);
    }

    [Fact]
    public void ReaderNeverSeesAMixOfTwoStatesUnderConcurrentWrites()
    {
        var a = FullState() with { Buttons = HostGamepadButtons.Cross, LeftX = 10, RightY = 10, BatteryPercent = 10 };
        var b = FullState() with { Buttons = HostGamepadButtons.Circle, LeftX = 20, RightY = 20, BatteryPercent = 20 };
        using var stop = new CancellationTokenSource();
        var writer = new Thread(() =>
        {
            var useA = true;
            while (!stop.IsCancellationRequested)
            {
                _memory.WriteInput(useA ? a : b, useA ? "a" : "b");
                useA = !useA;
            }
        });
        writer.Start();

        try
        {
            for (var index = 0; index < 20_000; index++)
            {
                if (!_memory.TryReadInput(out var read, out var name))
                {
                    continue;
                }

                var expected = name == "a" ? a : b;
                if (name is not ("a" or "b") && !read.Connected)
                {
                    continue; // read before the first write
                }

                Assert.Equal(expected, read);
            }
        }
        finally
        {
            stop.Cancel();
            writer.Join();
        }
    }

    [Fact]
    public void EventsArriveInOrderOnceAndTheReaderStartsWhereItJoined()
    {
        _memory.PushEvent(BridgeEvent.ForButton(HostGamepadButtons.Cross, true));
        ulong readIndex = _memory.WrittenEvents; // a late joiner skips the old events
        _memory.PushEvent(BridgeEvent.ForButton(HostGamepadButtons.Cross, false));
        _memory.PushEvent(BridgeEvent.ForSensor(true, 1, 2, 3, 99));
        _memory.PushEvent(BridgeEvent.ForButton(HostGamepadButtons.L2, true));

        var buffer = new BridgeEvent[8];
        var count = _memory.ReadEvents(ref readIndex, buffer);

        Assert.Equal(3, count);
        Assert.Equal(BridgeEvent.KindButton, buffer[0].Kind);
        Assert.Equal((uint)HostGamepadButtons.Cross, buffer[0].Button);
        Assert.Equal(0, buffer[0].Down);
        Assert.Equal(BridgeEvent.KindGyro, buffer[1].Kind);
        Assert.Equal((1f, 2f, 3f, 99UL), (buffer[1].X, buffer[1].Y, buffer[1].Z, buffer[1].TimestampNanoseconds));
        Assert.Equal((uint)HostGamepadButtons.L2, buffer[2].Button);
        Assert.Equal(1, buffer[2].Down);
        Assert.Equal(0, _memory.ReadEvents(ref readIndex, buffer));
    }

    [Fact]
    public void ARingReaderThatFellBehindSkipsToTheNewestEvents()
    {
        ulong readIndex = 0;
        for (var index = 0; index < GamepadBridgeMemory.RingCapacity * 2 + 10; index++)
        {
            _memory.PushEvent(BridgeEvent.ForSensor(false, index, 0, 0, (ulong)index));
        }

        var buffer = new BridgeEvent[GamepadBridgeMemory.RingCapacity * 2];
        var count = _memory.ReadEvents(ref readIndex, buffer);

        Assert.InRange(count, 1, GamepadBridgeMemory.RingCapacity);
        // What it reads is consecutive and ends with the newest event.
        for (var index = 1; index < count; index++)
        {
            Assert.Equal(buffer[index - 1].TimestampNanoseconds + 1, buffer[index].TimestampNanoseconds);
        }

        Assert.Equal((ulong)(GamepadBridgeMemory.RingCapacity * 2 + 9), buffer[count - 1].TimestampNanoseconds);
    }

    private sealed class RecordingOutput : IHostGamepadOutput
    {
        public List<string> Calls { get; } = [];

        public void SetRumble(byte largeMotor, byte smallMotor) => Calls.Add($"rumble {largeMotor} {smallMotor}");

        public void SetHapticRumble(byte largeMotor, byte smallMotor, uint durationMs) =>
            Calls.Add($"haptic {largeMotor} {smallMotor} {durationMs}");

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger) =>
            Calls.Add($"trigger {leftTrigger?.ToString() ?? "-"} {rightTrigger?.ToString() ?? "-"}");

        public void SetAdaptiveTriggerEffect(HostAdaptiveTriggerEffect? leftTrigger, HostAdaptiveTriggerEffect? rightTrigger) =>
            Calls.Add($"adaptive {Describe(leftTrigger)} {Describe(rightTrigger)}");

        public void SetLightbar(byte red, byte green, byte blue) => Calls.Add($"lightbar {red} {green} {blue}");

        public void ResetLightbar() => Calls.Add("lightbar reset");

        private static string Describe(HostAdaptiveTriggerEffect? effect)
        {
            if (effect is not { } value)
            {
                return "-";
            }

            Span<byte> bytes = stackalloc byte[11];
            value.CopyTo(bytes);
            return $"{Convert.ToHexString(bytes)}/{value.FallbackStrength}";
        }
    }

    [Fact]
    public void OutputCommandsAreAppliedOnceInGroupOrder()
    {
        var writer = new GamepadBridgeOutputWriter(_memory);
        var reader = new GamepadBridgeOutputReader(_memory);
        var target = new RecordingOutput();
        Assert.Equal(0, reader.Poll(target));

        // Written in an order unlike the group order: the poll applies them by group.
        writer.SetLightbar(1, 2, 3);
        writer.SetAdaptiveTriggerEffect(
            HostAdaptiveTriggerEffect.FromBytes([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], 40), null);
        writer.SetTriggerRumble(200, null);
        writer.SetHapticRumble(5, 6, 700);
        writer.SetRumble(255, 17);

        Assert.Equal(5, reader.Poll(target));
        Assert.Equal(
            [
                "rumble 255 17",
                "haptic 5 6 700",
                "trigger 200 -",
                "adaptive 0102030405060708090A0B/40 -",
                "lightbar 1 2 3",
            ],
            target.Calls);

        // Nothing new: nothing applied again.
        target.Calls.Clear();
        Assert.Equal(0, reader.Poll(target));
        Assert.Empty(target.Calls);
    }

    [Fact]
    public void EachGroupHasItsOwnSequenceAndAppliesOnlyWhenItMoves()
    {
        var writer = new GamepadBridgeOutputWriter(_memory);
        var reader = new GamepadBridgeOutputReader(_memory);
        var target = new RecordingOutput();

        writer.SetRumble(1, 1);
        writer.SetLightbar(9, 9, 9);
        reader.Poll(target);
        target.Calls.Clear();

        writer.SetRumble(2, 2);
        writer.SetRumble(3, 3); // two writes between polls: the latest wins, applied once
        Assert.Equal(1, reader.Poll(target));
        Assert.Equal(["rumble 3 3"], target.Calls);

        // The same values again is still a new command.
        target.Calls.Clear();
        writer.SetRumble(3, 3);
        Assert.Equal(1, reader.Poll(target));
        Assert.Equal(["rumble 3 3"], target.Calls);

        target.Calls.Clear();
        writer.ResetLightbar();
        Assert.Equal(1, reader.Poll(target));
        Assert.Equal(["lightbar reset"], target.Calls);
    }

    [Fact]
    public void AdaptiveTriggersKeepBothSidesAndTheirPresence()
    {
        var writer = new GamepadBridgeOutputWriter(_memory);
        var reader = new GamepadBridgeOutputReader(_memory);
        var target = new RecordingOutput();

        writer.SetAdaptiveTriggerEffect(
            HostAdaptiveTriggerEffect.FromBytes([1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], 3),
            HostAdaptiveTriggerEffect.FromBytes([2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2], 4));
        reader.Poll(target);
        writer.SetAdaptiveTriggerEffect(null, HostAdaptiveTriggerEffect.FromBytes([9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9], 0));
        reader.Poll(target);

        Assert.Equal(
            [
                "adaptive 0101010101010101010101/3 0202020202020202020202/4",
                "adaptive - 0909090909090909090909/0",
            ],
            target.Calls);
    }

    [Fact]
    public void OutputReadWaitsOutAnOpenWrite()
    {
        var writer = new GamepadBridgeOutputWriter(_memory);
        var reader = new GamepadBridgeOutputReader(_memory);
        var target = new RecordingOutput();
        writer.SetRumble(4, 5);
        reader.Poll(target);
        target.Calls.Clear();

        writer.SetRumble(6, 7);
        GamepadBridgeMemory.BeginWrite(_memory.OutputGroupPointer(GamepadBridgeOutputWriter.GroupRumble));
        // The group is mid-update: nothing is applied from it, and nothing is lost afterwards.
        Assert.Equal(0, reader.Poll(target));
        GamepadBridgeMemory.EndWrite(_memory.OutputGroupPointer(GamepadBridgeOutputWriter.GroupRumble));
        Assert.Equal(1, reader.Poll(target));
        Assert.Equal(["rumble 6 7"], target.Calls);
    }
}
