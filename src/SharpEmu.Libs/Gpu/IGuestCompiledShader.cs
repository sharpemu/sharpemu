// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// A guest shader compiled by a backend, opaque to the export layers: only the backend
/// that produced it can submit it. <see cref="Payload"/> is the backend-defined compiled
/// bytes (SPIR-V words for Vulkan; MSL/DXIL for future backends), exposed solely for
/// diagnostics dumps and size traces — callers must never interpret it.
/// </summary>
internal interface IGuestCompiledShader
{
    byte[] Payload { get; }

    /// <summary>File extension for diagnostics dumps of <see cref="Payload"/> ("spv",
    /// "msl", ...), so dumps stay honestly labeled whatever the backend.</summary>
    string PayloadFileExtension { get; }
}
