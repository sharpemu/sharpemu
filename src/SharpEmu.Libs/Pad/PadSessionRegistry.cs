// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Pad;

internal sealed class PadSessionRegistry
{
    private const int ErrorInvalidHandle = unchecked((int)0x80920003);
    private const int ErrorAlreadyOpened = unchecked((int)0x80920004);
    private const int ErrorNotInitialized = unchecked((int)0x80920005);
    private const int ErrorNoHandle = unchecked((int)0x80920008);

    private readonly object _gate = new();
    private readonly Dictionary<int, PadSession> _sessions = [];
    private int _nextStandardHandle = 1;
    private int _nextSpecialHandle;
    private int _nextExtendedHandle = 2;
    private bool _initialized;

    public bool IsInitialized
    {
        get
        {
            lock (_gate)
            {
                return _initialized;
            }
        }
    }

    public void Initialize()
    {
        lock (_gate)
        {
            _initialized = true;
        }
    }

    public int Open(int userId, int portType, int portIndex, bool allowMultipleOpens)
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                return ErrorNotInitialized;
            }

            if (!allowMultipleOpens && FindHandle(userId, portType, portIndex) >= 0)
            {
                return ErrorAlreadyOpened;
            }

            // Separate handle sequences keep each port independent across repeated opens.
            var handle = portType switch
            {
                0 => _nextStandardHandle,
                2 => _nextSpecialHandle,
                _ => _nextExtendedHandle,
            };
            if (handle > int.MaxValue - 4)
            {
                return ErrorNoHandle;
            }

            switch (portType)
            {
                case 0:
                    _nextStandardHandle += 4;
                    break;
                case 2:
                    _nextSpecialHandle += 4;
                    break;
                default:
                    _nextExtendedHandle += 4;
                    break;
            }

            _sessions.Add(handle, new PadSession(userId, portType, portIndex, portType == 0));
            return handle;
        }
    }

    public int GetHandle(int userId, int portType, int portIndex)
    {
        lock (_gate)
        {
            if (!_initialized)
            {
                return ErrorNotInitialized;
            }

            var handle = FindHandle(userId, portType, portIndex);
            return handle >= 0 ? handle : ErrorNoHandle;
        }
    }

    public bool TryGet(int handle, out PadSession session)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(handle, out session!);
        }
    }

    public int Close(int handle)
    {
        lock (_gate)
        {
            return _sessions.Remove(handle) ? 0 : ErrorInvalidHandle;
        }
    }

    private int FindHandle(int userId, int portType, int portIndex)
    {
        var firstHandle = int.MaxValue;
        foreach (var (handle, session) in _sessions)
        {
            if (session.UserId == userId && session.PortType == portType && session.PortIndex == portIndex)
            {
                firstHandle = Math.Min(firstHandle, handle);
            }
        }

        return firstHandle == int.MaxValue ? -1 : firstHandle;
    }
}

internal sealed class PadSession(int userId, int portType, int portIndex, bool connected)
{
    public int UserId { get; } = userId;
    public int PortType { get; } = portType;
    public int PortIndex { get; } = portIndex;
    public bool Connected { get; } = connected;
    public int MotionSensorEnabled = 1;
}
