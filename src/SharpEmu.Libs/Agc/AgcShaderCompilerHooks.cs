// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
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
    [SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "This is the rule's intended advanced scenario: the hook must be " +
            "installed before any code path can reach the evaluator, and every such path " +
            "enters through this assembly.")]
    internal static void Install()
    {
        Gen5ShaderScalarEvaluator.FallbackMemoryReader =
            KernelMemoryCompatExports.TryReadTrackedLibcHeap;
        Gen5ShaderScalarEvaluator.GlobalMemoryPool =
            VulkanVideoPresenter.GuestDataPool;
    }
}
