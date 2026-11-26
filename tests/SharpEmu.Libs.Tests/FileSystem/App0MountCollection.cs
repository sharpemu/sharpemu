// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

// /app0 holds one mount at a time, so tests that mount an archive or a package must not overlap.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class App0MountCollection
{
    public const string Name = "App0Mount";
}
