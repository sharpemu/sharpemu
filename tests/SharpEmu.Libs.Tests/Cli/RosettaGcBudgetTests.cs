// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.CLI;
using Xunit;

namespace SharpEmu.Libs.Tests.Cli;

public sealed class RosettaGcBudgetTests
{
    [Fact]
    public void DotnetRestartKeepsTheManagedEntryAssemblyAndGuestArguments()
    {
        Assert.Equal(new[] { "/runtime/dotnet", "/emulator/SharpEmu.CLI.dll", "--hdr=off", "/games/My Game/eboot.bin" },
            RosettaGcBudget.BuildArguments("/runtime/dotnet", "/emulator/SharpEmu.CLI.dll",
                ["--hdr=off", "/games/My Game/eboot.bin"]));
    }

    [Fact]
    public void AppHostRestartDoesNotPassTheManagedAssemblyAsAGuestArgument()
    {
        Assert.Equal(new[] { "/emulator/SharpEmu.CLI", "--hdr=off", "/games/My Game/eboot.bin" },
            RosettaGcBudget.BuildArguments("/emulator/SharpEmu.CLI", "/emulator/SharpEmu.CLI.dll",
                ["--hdr=off", "/games/My Game/eboot.bin"]));
    }
}
