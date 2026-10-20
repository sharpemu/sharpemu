// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public enum ResourceMaterializationFailure
{
    None,
    Other,
    IncompatibleImageCandidates,
    ImageCapacityExceeded,
    // An indirect image table names descriptors in guest memory that is not mapped.
    UnreadableImageCandidates,
}
