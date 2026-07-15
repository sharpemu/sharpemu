// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.Libs.Kernel;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Wires the backend-neutral shader compiler to this assembly's HLE services. The
/// module initializer runs before any Libs code can invoke the evaluator, so the hook
/// is always installed first.
/// </summary>
internal static class AgcShaderCompilerHooks
{
    [ModuleInitializer]
    internal static void Install()
    {
        Gen5ShaderScalarEvaluator.FallbackMemoryReader =
            KernelMemoryCompatExports.TryReadTrackedLibcHeap;
    }
}
