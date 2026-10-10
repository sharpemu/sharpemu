// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

public static class ShaderPrecompiler
{
    public const string WorkerFlag = VulkanVideoPresenter.ShaderCacheWorkerFlag;

    public static int RunWorker(IReadOnlyList<string> arguments) => VulkanVideoPresenter.RunShaderCacheWorker(arguments);

    public static int Run(string app0Root, bool full) => VulkanVideoPresenter.RunShaderPrecompile(app0Root, full);
}
