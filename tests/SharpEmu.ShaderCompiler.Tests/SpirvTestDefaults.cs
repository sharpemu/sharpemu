// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.ShaderCompiler.Vulkan;

namespace SharpEmu.ShaderCompiler.Tests;

internal static class SpirvTestDefaults
{
    [ModuleInitializer]
    internal static void KeepFinalRegisterState() => SpirvModuleBuilder.KeepsUnreadVariables = true;
}
