// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// The layout of the gamepad bridge's shared memory, for a sandboxed emulator child: an AppContainer process sees no
/// controller, so the launcher process reads it with SDL and writes the state here, and the child writes its output
/// commands (rumble, adaptive triggers, lightbar) here for the launcher to apply.
///
/// Offsets (the section is <see cref="Size"/> bytes, zeroed when created):
///   0     header: magic, version, size
///   64    input slot, written by the launcher under a seqlock: int sequence, int reserved, then <see cref="InputPayload"/>
///   640   event ring, written by the launcher: ulong written count (+ padding to 64 bytes), then 128 entries of
///         <see cref="BridgeEvent"/>; the child keeps its own read index
///   4800  output groups, written by the child, each 128 bytes under its own seqlock and carrying its own increasing
///         command sequence: rumble, haptic rumble, trigger rumble, adaptive trigger, lightbar
/// A seqlock slot: a writer makes the sequence odd, writes the payload, and makes it even again; a reader retries
/// when it saw an odd sequence or a sequence that changed while it copied.
/// </summary>
internal sealed unsafe class GamepadBridgeMemory
{
    public const uint Magic = 0x42475053; // "SPGB"
    public const uint Version = 1;
    public const int Size = 8192;
    public const int HeaderSize = 64;
    public const int InputOffset = 64;
    public const int RingOffset = 640;
    public const int RingCapacity = 128;
    public const int RingEntriesOffset = RingOffset + 64;
    public const int OutputOffset = RingEntriesOffset + RingCapacity * 32; // 4800
    public const int OutputGroupSize = 128;
    public const int OutputGroupCount = 5;
    public const int SlotHeaderSize = 8;
    public const int MaxNameBytes = 95;
    public const int DefaultReadAttempts = 256;

    private readonly byte* _base;

    public GamepadBridgeMemory(byte* memory)
    {
        _base = memory;
    }

    /// <summary>Writes the header; the launcher calls it once on the zeroed section.</summary>
    public void Initialize()
    {
        Unsafe.WriteUnaligned(_base + 4, Version);
        Unsafe.WriteUnaligned(_base + 8, Size);
        // The magic goes last: a reader that sees it sees the rest.
        Volatile.Write(ref *(uint*)_base, Magic);
    }

    /// <summary>True when the memory was initialized by a launcher speaking this layout.</summary>
    public bool IsValid =>
        Volatile.Read(ref *(uint*)_base) == Magic &&
        Unsafe.ReadUnaligned<uint>(_base + 4) == Version &&
        Unsafe.ReadUnaligned<int>(_base + 8) == Size;

    // ---- seqlock -------------------------------------------------------------------------------------------------

    internal static void BeginWrite(byte* slot) => Interlocked.Increment(ref *(int*)slot);

    internal static void EndWrite(byte* slot) => Interlocked.Increment(ref *(int*)slot);

    /// <summary>
    /// Copies the payload of a slot. Returns false when the writer never finished within <paramref name="attempts"/>
    /// tries. <paramref name="afterCopy"/> runs between the copy and the check, where a torn read happens.
    /// </summary>
    internal static bool TryReadSlot<T>(byte* slot, out T value, int attempts = DefaultReadAttempts, Action? afterCopy = null)
        where T : unmanaged
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var before = Volatile.Read(ref *(int*)slot);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(16);
                continue;
            }

            var copy = Unsafe.ReadUnaligned<T>(slot + SlotHeaderSize);
            afterCopy?.Invoke();
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref *(int*)slot) == before)
            {
                value = copy;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal static void WriteSlot<T>(byte* slot, in T value) where T : unmanaged
    {
        BeginWrite(slot);
        Unsafe.WriteUnaligned(slot + SlotHeaderSize, value);
        EndWrite(slot);
    }

    private byte* InputSlot => _base + InputOffset;

    internal byte* InputSlotPointer => InputSlot;

    private byte* OutputGroup(int group) => _base + OutputOffset + group * OutputGroupSize;

    internal byte* OutputGroupPointer(int group) => OutputGroup(group);

    // ---- input (launcher writes, child reads) --------------------------------------------------------------------

    public void WriteInput(in HostGamepadState state, string? name) =>
        WriteSlot(InputSlot, InputPayload.From(state, name));

    public void WriteDisconnected() => WriteSlot(InputSlot, default(InputPayload));

    /// <summary>The latest state; false when a consistent copy could not be read (the caller keeps its last one).</summary>
    public bool TryReadInput(out HostGamepadState state, out string? name, int attempts = DefaultReadAttempts, Action? afterCopy = null)
    {
        if (!TryReadSlot<InputPayload>(InputSlot, out var payload, attempts, afterCopy))
        {
            state = default;
            name = null;
            return false;
        }

        payload.ToState(out state, out name);
        return true;
    }

    // ---- event ring (launcher writes, child reads) ---------------------------------------------------------------

    private byte* RingEntry(ulong index) => _base + RingEntriesOffset + (int)(index % RingCapacity) * sizeof(BridgeEvent);

    /// <summary>How many events the launcher has pushed so far.</summary>
    public ulong WrittenEvents => Volatile.Read(ref *(ulong*)(_base + RingOffset));

    public void PushEvent(in BridgeEvent entry)
    {
        var written = Volatile.Read(ref *(ulong*)(_base + RingOffset));
        Unsafe.WriteUnaligned(RingEntry(written), entry);
        Volatile.Write(ref *(ulong*)(_base + RingOffset), written + 1);
    }

    /// <summary>
    /// Copies the events after <paramref name="readIndex"/> in order and moves it forward. Events the ring has already
    /// overwritten (a reader that fell more than the capacity behind) are skipped.
    /// </summary>
    public int ReadEvents(ref ulong readIndex, Span<BridgeEvent> destination)
    {
        var count = 0;
        while (count < destination.Length)
        {
            var written = Volatile.Read(ref *(ulong*)(_base + RingOffset));
            if (readIndex >= written)
            {
                break;
            }

            // The writer fills the entry it is about to publish, so a slot is safe only while it is more than one
            // lap away from the newest published one.
            if (written - readIndex >= RingCapacity)
            {
                readIndex = written - (RingCapacity - 1);
            }

            var copy = Unsafe.ReadUnaligned<BridgeEvent>(RingEntry(readIndex));
            Interlocked.MemoryBarrier();
            var after = Volatile.Read(ref *(ulong*)(_base + RingOffset));
            if (after - readIndex >= RingCapacity)
            {
                readIndex = after - (RingCapacity - 1);
                continue;
            }

            destination[count++] = copy;
            readIndex++;
        }

        return count;
    }

    // ---- output (child writes, launcher reads) -------------------------------------------------------------------

    public void WriteOutput<T>(int group, in T payload) where T : unmanaged
    {
        if (sizeof(T) + SlotHeaderSize > OutputGroupSize)
        {
            throw new ArgumentException("The output payload does not fit its group.", nameof(payload));
        }

        WriteSlot(OutputGroup(group), payload);
    }

    public bool TryReadOutput<T>(int group, out T payload, int attempts = DefaultReadAttempts) where T : unmanaged =>
        TryReadSlot(OutputGroup(group), out payload, attempts);

    internal static byte ToByte(bool value) => value ? (byte)1 : (byte)0;
}

/// <summary>One event of the launcher's pad that a poll of the state cannot reproduce.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
internal struct BridgeEvent
{
    public const byte KindButton = 1;
    public const byte KindGyro = 2;
    public const byte KindAccel = 3;

    public byte Kind;
    public byte Down;
    public uint Button; // HostGamepadButtons, for KindButton
    public float X;
    public float Y;
    public float Z;
    public ulong TimestampNanoseconds;

    public static BridgeEvent ForButton(HostGamepadButtons button, bool down) =>
        new() { Kind = KindButton, Button = (uint)button, Down = GamepadBridgeMemory.ToByte(down) };

    public static BridgeEvent ForSensor(bool gyro, float x, float y, float z, ulong timestampNanoseconds) =>
        new() { Kind = gyro ? KindGyro : KindAccel, X = x, Y = y, Z = z, TimestampNanoseconds = timestampNanoseconds };
}

/// <summary>A blittable copy of <see cref="HostGamepadState"/> and the pad's name.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct InputPayload
{
    public byte Connected;
    public byte Type;
    public byte Connection;
    public byte Battery;
    public uint Buttons;
    public byte LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger;
    public byte MotionAvailable;
    public byte Reserved;
    public float AccelX, AccelY, AccelZ;
    public float GyroX, GyroY, GyroZ;
    public float OrientationX, OrientationY, OrientationZ, OrientationW;
    public byte Touch1Active, Touch1Id;
    public float Touch1X, Touch1Y;
    public byte Touch2Active, Touch2Id;
    public float Touch2X, Touch2Y;
    public byte NameLength;
    public fixed byte Name[GamepadBridgeMemory.MaxNameBytes];

    public static InputPayload From(in HostGamepadState state, string? name)
    {
        var payload = new InputPayload
        {
            Connected = GamepadBridgeMemory.ToByte(state.Connected),
            Type = (byte)state.Type,
            Connection = (byte)state.Connection,
            Battery = state.BatteryPercent,
            Buttons = (uint)state.Buttons,
            LeftX = state.LeftX,
            LeftY = state.LeftY,
            RightX = state.RightX,
            RightY = state.RightY,
            LeftTrigger = state.LeftTrigger,
            RightTrigger = state.RightTrigger,
            MotionAvailable = GamepadBridgeMemory.ToByte(state.Motion.Available),
            AccelX = state.Motion.AccelerationX,
            AccelY = state.Motion.AccelerationY,
            AccelZ = state.Motion.AccelerationZ,
            GyroX = state.Motion.AngularVelocityX,
            GyroY = state.Motion.AngularVelocityY,
            GyroZ = state.Motion.AngularVelocityZ,
            OrientationX = state.Motion.OrientationX,
            OrientationY = state.Motion.OrientationY,
            OrientationZ = state.Motion.OrientationZ,
            OrientationW = state.Motion.OrientationW,
            Touch1Active = GamepadBridgeMemory.ToByte(state.Touch.First.Active),
            Touch1Id = state.Touch.First.Id,
            Touch1X = state.Touch.First.X,
            Touch1Y = state.Touch.First.Y,
            Touch2Active = GamepadBridgeMemory.ToByte(state.Touch.Second.Active),
            Touch2Id = state.Touch.Second.Id,
            Touch2X = state.Touch.Second.X,
            Touch2Y = state.Touch.Second.Y,
        };
        if (!string.IsNullOrEmpty(name))
        {
            var bytes = new Span<byte>(payload.Name, GamepadBridgeMemory.MaxNameBytes);
            payload.NameLength = (byte)TruncatedUtf8(name, bytes);
        }

        return payload;
    }

    // UTF-8 of the name cut at a character boundary to fit the field.
    private static int TruncatedUtf8(string name, Span<byte> destination)
    {
        var length = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (length + size > destination.Length)
            {
                break;
            }

            rune.EncodeToUtf8(destination[length..]);
            length += size;
        }

        return length;
    }

    public void ToState(out HostGamepadState state, out string? name)
    {
        if (Connected == 0)
        {
            state = default;
            name = null;
            return;
        }

        state = new HostGamepadState(
            true,
            (HostGamepadButtons)Buttons,
            LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger,
            (HostGamepadType)Type,
            (HostGamepadConnection)Connection,
            new HostMotionState(
                MotionAvailable != 0,
                AccelX, AccelY, AccelZ,
                GyroX, GyroY, GyroZ,
                OrientationX, OrientationY, OrientationZ, OrientationW),
            new HostTouchState(
                new HostTouchPoint(Touch1Active != 0, Touch1Id, Touch1X, Touch1Y),
                new HostTouchPoint(Touch2Active != 0, Touch2Id, Touch2X, Touch2Y)),
            Battery);
        var length = Math.Min((int)NameLength, GamepadBridgeMemory.MaxNameBytes);
        fixed (byte* text = Name)
        {
            name = length == 0 ? null : Encoding.UTF8.GetString(text, length);
        }
    }
}

// Output command payloads: each starts with the group's own command sequence, which the child increments per command.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RumbleCommand
{
    public uint Sequence;
    public byte Large, Small;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HapticCommand
{
    public uint Sequence;
    public byte Large, Small;
    public uint DurationMs;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TriggerRumbleCommand
{
    public uint Sequence;
    public byte LeftPresent, Left, RightPresent, Right;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct AdaptiveTriggerCommand
{
    public uint Sequence;
    public byte LeftPresent, RightPresent;
    public byte LeftFallback, RightFallback;
    public fixed byte Left[11];
    public fixed byte Right[11];

    public static AdaptiveTriggerCommand From(
        uint sequence, HostAdaptiveTriggerEffect? left, HostAdaptiveTriggerEffect? right)
    {
        var command = new AdaptiveTriggerCommand { Sequence = sequence };
        if (left is { } leftEffect)
        {
            command.LeftPresent = 1;
            command.LeftFallback = leftEffect.FallbackStrength;
            leftEffect.CopyTo(new Span<byte>(command.Left, 11));
        }

        if (right is { } rightEffect)
        {
            command.RightPresent = 1;
            command.RightFallback = rightEffect.FallbackStrength;
            rightEffect.CopyTo(new Span<byte>(command.Right, 11));
        }

        return command;
    }

    public HostAdaptiveTriggerEffect? GetLeft()
    {
        if (LeftPresent == 0)
        {
            return null;
        }

        fixed (byte* data = Left)
        {
            return HostAdaptiveTriggerEffect.FromBytes(new ReadOnlySpan<byte>(data, 11), LeftFallback);
        }
    }

    public HostAdaptiveTriggerEffect? GetRight()
    {
        if (RightPresent == 0)
        {
            return null;
        }

        fixed (byte* data = Right)
        {
            return HostAdaptiveTriggerEffect.FromBytes(new ReadOnlySpan<byte>(data, 11), RightFallback);
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LightbarCommand
{
    public uint Sequence;
    public byte Reset;
    public byte Red, Green, Blue;
}

/// <summary>The child's <see cref="IHostGamepadOutput"/>: each call becomes a new command of its group.</summary>
internal sealed class GamepadBridgeOutputWriter : IHostGamepadOutput
{
    public const int GroupRumble = 0;
    public const int GroupHaptic = 1;
    public const int GroupTriggerRumble = 2;
    public const int GroupAdaptive = 3;
    public const int GroupLightbar = 4;

    private readonly GamepadBridgeMemory _memory;
    private readonly uint[] _sequences = new uint[GamepadBridgeMemory.OutputGroupCount];
    private readonly object _gate = new();

    public GamepadBridgeOutputWriter(GamepadBridgeMemory memory)
    {
        _memory = memory;
    }

    public void SetRumble(byte largeMotor, byte smallMotor)
    {
        lock (_gate)
        {
            _memory.WriteOutput(GroupRumble, new RumbleCommand { Sequence = ++_sequences[GroupRumble], Large = largeMotor, Small = smallMotor });
        }
    }

    public void SetHapticRumble(byte largeMotor, byte smallMotor, uint durationMs)
    {
        lock (_gate)
        {
            _memory.WriteOutput(GroupHaptic, new HapticCommand
            {
                Sequence = ++_sequences[GroupHaptic],
                Large = largeMotor,
                Small = smallMotor,
                DurationMs = durationMs,
            });
        }
    }

    public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
    {
        lock (_gate)
        {
            _memory.WriteOutput(GroupTriggerRumble, new TriggerRumbleCommand
            {
                Sequence = ++_sequences[GroupTriggerRumble],
                LeftPresent = GamepadBridgeMemory.ToByte(leftTrigger.HasValue),
                Left = leftTrigger ?? 0,
                RightPresent = GamepadBridgeMemory.ToByte(rightTrigger.HasValue),
                Right = rightTrigger ?? 0,
            });
        }
    }

    public void SetAdaptiveTriggerEffect(HostAdaptiveTriggerEffect? leftTrigger, HostAdaptiveTriggerEffect? rightTrigger)
    {
        lock (_gate)
        {
            _memory.WriteOutput(
                GroupAdaptive,
                AdaptiveTriggerCommand.From(++_sequences[GroupAdaptive], leftTrigger, rightTrigger));
        }
    }

    public void SetLightbar(byte red, byte green, byte blue)
    {
        lock (_gate)
        {
            _memory.WriteOutput(GroupLightbar, new LightbarCommand
            {
                Sequence = ++_sequences[GroupLightbar],
                Red = red,
                Green = green,
                Blue = blue,
            });
        }
    }

    public void ResetLightbar()
    {
        lock (_gate)
        {
            _memory.WriteOutput(GroupLightbar, new LightbarCommand { Sequence = ++_sequences[GroupLightbar], Reset = 1 });
        }
    }
}

/// <summary>
/// The launcher's reader of the child's commands: each poll applies every group whose sequence moved since the last
/// poll, once, in group order. A command group written several times between two polls is applied as its latest.
/// </summary>
internal sealed class GamepadBridgeOutputReader
{
    private readonly GamepadBridgeMemory _memory;
    private readonly uint[] _applied = new uint[GamepadBridgeMemory.OutputGroupCount];

    public GamepadBridgeOutputReader(GamepadBridgeMemory memory)
    {
        _memory = memory;
    }

    /// <summary>Applies the new commands to <paramref name="target"/>; returns how many groups it applied.</summary>
    public int Poll(IHostGamepadOutput target)
    {
        var count = 0;
        if (_memory.TryReadOutput<RumbleCommand>(GamepadBridgeOutputWriter.GroupRumble, out var rumble) &&
            rumble.Sequence != _applied[GamepadBridgeOutputWriter.GroupRumble])
        {
            _applied[GamepadBridgeOutputWriter.GroupRumble] = rumble.Sequence;
            target.SetRumble(rumble.Large, rumble.Small);
            count++;
        }

        if (_memory.TryReadOutput<HapticCommand>(GamepadBridgeOutputWriter.GroupHaptic, out var haptic) &&
            haptic.Sequence != _applied[GamepadBridgeOutputWriter.GroupHaptic])
        {
            _applied[GamepadBridgeOutputWriter.GroupHaptic] = haptic.Sequence;
            target.SetHapticRumble(haptic.Large, haptic.Small, haptic.DurationMs);
            count++;
        }

        if (_memory.TryReadOutput<TriggerRumbleCommand>(GamepadBridgeOutputWriter.GroupTriggerRumble, out var trigger) &&
            trigger.Sequence != _applied[GamepadBridgeOutputWriter.GroupTriggerRumble])
        {
            _applied[GamepadBridgeOutputWriter.GroupTriggerRumble] = trigger.Sequence;
            target.SetTriggerRumble(
                trigger.LeftPresent != 0 ? trigger.Left : null,
                trigger.RightPresent != 0 ? trigger.Right : null);
            count++;
        }

        if (_memory.TryReadOutput<AdaptiveTriggerCommand>(GamepadBridgeOutputWriter.GroupAdaptive, out var adaptive) &&
            adaptive.Sequence != _applied[GamepadBridgeOutputWriter.GroupAdaptive])
        {
            _applied[GamepadBridgeOutputWriter.GroupAdaptive] = adaptive.Sequence;
            target.SetAdaptiveTriggerEffect(adaptive.GetLeft(), adaptive.GetRight());
            count++;
        }

        if (_memory.TryReadOutput<LightbarCommand>(GamepadBridgeOutputWriter.GroupLightbar, out var lightbar) &&
            lightbar.Sequence != _applied[GamepadBridgeOutputWriter.GroupLightbar])
        {
            _applied[GamepadBridgeOutputWriter.GroupLightbar] = lightbar.Sequence;
            if (lightbar.Reset != 0)
            {
                target.ResetLightbar();
            }
            else
            {
                target.SetLightbar(lightbar.Red, lightbar.Green, lightbar.Blue);
            }

            count++;
        }

        return count;
    }
}
