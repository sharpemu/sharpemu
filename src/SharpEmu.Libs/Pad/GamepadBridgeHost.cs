// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.Versioning;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Sdl;
using SDL;
using static SDL.SDL3;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// The launcher's end of the gamepad bridge, used only when the emulator child runs in the AppContainer sandbox (an
/// AppContainer process sees no controller). One background thread, the only user of SDL gamepads in this process
/// while it runs, polls the pad every 0.5 ms, writes its state and button/sensor events to the shared section, and
/// applies the output commands the child wrote there. The launcher passes <see cref="Handle"/> in the child's handle
/// list and <see cref="ChildEnvironment"/> in its environment.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class GamepadBridgeHost : IDisposable
{
    private const SDL_InitFlags InitFlags = SDL_InitFlags.SDL_INIT_GAMEPAD;
    // SDL has no event to wait on here (the gamepad drivers are read when SDL pumps, and SDL_WaitEventTimeout only takes
    // whole milliseconds and sleeps the same way), so the thread polls on a high-resolution timer instead. The timer
    // fires on a 0.5 ms grid and a shorter request still waits for the next line, so 250 us measures as 0.5 ms per
    // round (SDL_DelayNS 1 ms measured 1.5 ms).
    private const long PollNanoseconds = 250_000;
    private static readonly long ReopenIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 2;

    private readonly GamepadBridgeSection _section;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly SdlGamepadOutput _output;
    private readonly GamepadBridgeOutputReader _outputReader;
    private SDL_Gamepad* _gamepad;
    private TouchContactIds _touchIds = new();
    private string _gamepadName = string.Empty;
    private long _nextOpenAttempt;
    private long _nextAudioProbe;
    private readonly GamepadBridgePcmPump[] _pcmPumps;
    private volatile bool _stop;
    private bool _disposed;

    private GamepadBridgeHost(GamepadBridgeSection section)
    {
        _section = section;
        _output = new SdlGamepadOutput(_gate);
        _outputReader = new GamepadBridgeOutputReader(section.Memory);
        _pcmPumps = new GamepadBridgePcmPump[GamepadBridgeMemory.PcmSlotCount];
        for (var slot = 0; slot < _pcmPumps.Length; slot++)
        {
            _pcmPumps[slot] = new GamepadBridgePcmPump(section.Memory, slot, SdlControllerLink.Instance.TryOpenBluetoothStream);
        }

        _thread = new Thread(Run, 256 * 1024)
        {
            IsBackground = true,
            Name = "SharpEmu gamepad bridge",
        };
    }

    /// <summary>The handle of the shared section, for the child's handle list (inheritable only inside the launcher's InheritScope).</summary>
    public nint Handle => _section.Handle;

    /// <summary>The variable that tells the child which handle is the section.</summary>
    public KeyValuePair<string, string> ChildEnvironment =>
        new(GamepadBridgeClient.EnvironmentVariable, ((long)_section.Handle).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Starts the bridge, or returns null (with a warning) when it cannot: the child then runs without a gamepad.
    /// </summary>
    public static GamepadBridgeHost? TryStart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var host = new GamepadBridgeHost(GamepadBridgeSection.Create());
            // The launcher's own menu polling stops while the bridge owns the pad, so SDL gamepads are never touched
            // from two threads: SdlLauncherGamepad.Suspend closes its handle and answers "no gamepad" until Resume.
            SdlLauncherGamepad.Suspend();
            host._thread.Start();
            Console.Error.WriteLine("[LOADER][INFO] gamepad bridge: on");
            return host;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] gamepad bridge: {exception.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop = true;
        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            // The thread is stuck inside SDL: leave the section mapped for it and do not hand SDL back.
            Console.Error.WriteLine("[LOADER][WARN] gamepad bridge: the polling thread did not stop");
            return;
        }

        _section.Dispose();
        SdlLauncherGamepad.Resume();
    }

    private void Run()
    {
        SdlGamepadStateReader.EnableSonyHidApi();
        // The game window, not this process, has the focus while the child runs.
        fixed (byte* enabled = "1"u8)
        fixed (byte* name = SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS)
        {
            SDL_SetHint(name, enabled);
        }

        if (!SDL_InitSubSystem(InitFlags))
        {
            Console.Error.WriteLine("[LOADER][WARN] gamepad bridge: SDL gamepad initialization failed.");
            _section.Memory.WriteDisconnected();
            return;
        }

        try
        {
            var events = stackalloc SDL_Event[32];
            using var wait = new HighResolutionWait();
            while (!_stop)
            {
                PollOnce(events);
                if (!wait.Wait(PollNanoseconds))
                {
                    SDL_DelayNS(1_000_000);
                }
            }
        }
        finally
        {
            CloseGamepad(announce: false);
            foreach (var pump in _pcmPumps)
            {
                pump.Dispose();
            }

            _section.Memory.WriteDisconnected();
            SDL_QuitSubSystem(InitFlags);
        }
    }

    private void PollOnce(SDL_Event* events)
    {
        SDL_UpdateGamepads();
        var added = DrainEvents(events);
        if (_gamepad is not null && !SDL_GamepadConnected(_gamepad))
        {
            CloseGamepad(announce: true);
        }

        if (_gamepad is null &&
            (added || System.Diagnostics.Stopwatch.GetTimestamp() >= _nextOpenAttempt))
        {
            OpenGamepad();
            _nextOpenAttempt = System.Diagnostics.Stopwatch.GetTimestamp() + ReopenIntervalTicks;
        }

        if (_gamepad is null)
        {
            _section.Memory.WriteDisconnected();
            PublishAudioPads(force: false);
            return;
        }

        _section.Memory.WriteInput(SdlGamepadStateReader.ReadFull(_gamepad, _touchIds), _gamepadName);
        PublishAudioPads(force: false);
        _outputReader.Poll(_output, _output);
        foreach (var pump in _pcmPumps)
        {
            pump.Poll();
        }
    }

    // The DualSense pads the child's controller audio may use (it sees no gamepad itself): re-checked a few times a
    // second and right after the pad opens or closes, and only written when it changed.
    private void PublishAudioPads(bool force)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!force && now < _nextAudioProbe)
        {
            return;
        }

        _nextAudioProbe = now + System.Diagnostics.Stopwatch.Frequency / 4;
        var pads = _gamepad is null ? HostControllerPads.None : SdlControllerLink.Instance.QueryPads();
        if (pads != _section.Memory.AudioPads)
        {
            _section.Memory.AudioPads = pads;
            Console.Error.WriteLine($"[LOADER][INFO] gamepad bridge: controller audio pads: {pads}");
        }
    }

    // The gamepad events SdlHostWindow turns into pad changes (a button, a trigger crossing its button threshold, a
    // sensor reading), queued for the child in the order SDL produced them. Returns true when a gamepad was added.
    private bool DrainEvents(SDL_Event* events)
    {
        var added = false;
        int count;
        while ((count = SDL_PeepEvents(
                   events,
                   32,
                   SDL_EventAction.SDL_GETEVENT,
                   (uint)SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION,
                   (uint)SDL_EventType.SDL_EVENT_GAMEPAD_STEAM_HANDLE_UPDATED)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                ref var entry = ref events[index];
                switch (entry.Type)
                {
                    case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                        added = true;
                        break;
                    case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                    case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                        QueueButton(in entry);
                        break;
                    case SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION:
                        QueueTrigger(in entry);
                        break;
                    case SDL_EventType.SDL_EVENT_GAMEPAD_SENSOR_UPDATE:
                        QueueSensor(in entry);
                        break;
                }
            }
        }

        return added;
    }

    private void QueueButton(in SDL_Event entry)
    {
        var button = SdlGamepadStateReader.MapButton(entry.gbutton.button);
        if (_gamepad is not null && entry.gbutton.which == SDL_GetGamepadID(_gamepad) &&
            button != HostGamepadButtons.None)
        {
            _section.Memory.PushEvent(BridgeEvent.ForButton(button, entry.gbutton.down));
        }
    }

    private void QueueTrigger(in SDL_Event entry)
    {
        var axis = (SDL_GamepadAxis)entry.gaxis.axis;
        if (_gamepad is not null && entry.gaxis.which == SDL_GetGamepadID(_gamepad) &&
            axis is SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER or SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)
        {
            _section.Memory.PushEvent(BridgeEvent.ForButton(
                axis == SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER ? HostGamepadButtons.L2 : HostGamepadButtons.R2,
                SdlGamepadStateReader.IsTriggerPressed(HostWindowInput.ToTriggerByte(entry.gaxis.value))));
        }
    }

    private void QueueSensor(in SDL_Event entry)
    {
        var sensor = (SDL_SensorType)entry.gsensor.sensor;
        if (_gamepad is null || entry.gsensor.which != SDL_GetGamepadID(_gamepad) ||
            sensor is not (SDL_SensorType.SDL_SENSOR_GYRO or SDL_SensorType.SDL_SENSOR_ACCEL))
        {
            return;
        }

        _section.Memory.PushEvent(BridgeEvent.ForSensor(
            sensor == SDL_SensorType.SDL_SENSOR_GYRO,
            entry.gsensor.data[0],
            entry.gsensor.data[1],
            entry.gsensor.data[2],
            entry.gsensor.sensor_timestamp));
    }

    private void OpenGamepad()
    {
        var gamepad = SdlGamepadStateReader.OpenPreferredGamepad();
        if (gamepad is null)
        {
            return;
        }

        _touchIds = new TouchContactIds();
        _gamepadName = SdlGamepadStateReader.GetName(gamepad);
        SdlGamepadStateReader.EnableSensors(gamepad);
        lock (_gate)
        {
            _gamepad = gamepad;
            _output.Gamepad = gamepad;
            _output.Type = SdlGamepadStateReader.MapGamepadType(SDL_GetRealGamepadType(gamepad));
        }

        PublishAudioPads(force: true);
        Console.Error.WriteLine(
            $"[LOADER][INFO] gamepad bridge: gamepad connected: {_gamepadName} " +
            $"type={_output.Type} connection={SdlGamepadStateReader.GetConnection(gamepad)}");
    }

    private void CloseGamepad(bool announce)
    {
        if (_gamepad is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var pump in _pcmPumps)
            {
                pump.Release();
            }

            _output.RestoreAudioRoute();
            SDL_CloseGamepad(_gamepad);
            _gamepad = null;
            _output.Reset();
        }

        PublishAudioPads(force: true);

        if (announce)
        {
            Console.Error.WriteLine($"[LOADER][INFO] gamepad bridge: gamepad disconnected: {_gamepadName}");
        }
    }
}
