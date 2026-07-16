// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core;
using SharpEmu.Core.Memory;
using SharpEmu.GameContent;
using SharpEmu.HLE;

namespace SharpEmu.Core.Loader;

public interface ISelfLoader
{
    SelfImage Load(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory);

    SelfImage Load(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory, IReadOnlyGameFileSystem? fs, string? mountRoot);

    SelfImage Load(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory, IModuleManager moduleManager);

    SelfImage Load(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory, IModuleManager moduleManager, IReadOnlyGameFileSystem? fs, string? mountRoot);

    SelfImage LoadAdditional(ReadOnlySpan<byte> imageData, IVirtualMemory virtualMemory, IModuleManager moduleManager, IReadOnlyGameFileSystem? fs, string? mountRoot);
}
