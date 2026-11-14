// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Rendering;

// An automatic indirect mesh draw is launched by a task stage instead of a conversion dispatch. One task workgroup reads
// the draw's current arguments, applies the conversion's count, limit and zero rules and emits the mesh groups with the
// draw parameters in its payload, so the draw needs no conversion dispatch, no pass break and no barriers.
// SHARPEMU_MESH_TASK_LAUNCH=0 keeps the conversion dispatch and leaves the device's task shader feature off (a device
// feature changes the driver's shader cache keys).
public static class MeshTaskLaunch
{
    public static readonly bool Configured = Environment.GetEnvironmentVariable("SHARPEMU_MESH_TASK_LAUNCH") != "0";

    private static volatile bool _deviceSupported;

    // Set by the renderer when it created its device with task shaders and the task push range fits.
    public static bool DeviceSupported
    {
        get => _deviceSupported;
        set => _deviceSupported = value;
    }

    // Whether an automatic indirect mesh draw resolved now is task-launched.
    public static bool Active => Configured && _deviceSupported;
}
