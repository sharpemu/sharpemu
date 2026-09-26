// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Libs.Tests.Fiber;

/// <summary>
/// FiberExports keeps its continuations, stack ranges and thread states in
/// static maps, and the tests reset them between cases. xUnit runs separate
/// classes in parallel, so every fiber test class has to share one collection
/// or a reset in one class wipes the state another is midway through using.
/// </summary>
[CollectionDefinition(Name)]
public sealed class FiberStateCollection
{
    public const string Name = "fiber-runtime-state";
}
