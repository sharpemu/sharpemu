using SharpEmu.Diagnostics;
using Xunit;

namespace SharpEmu.Diagnostics.Tests;

public class DiagnosticConfigTests
{
    [Fact]
    public void Load_WithNoConfig_ReturnsInactive()
    {
        // Clear all env vars
        Environment.SetEnvironmentVariable("SHARPEMU_DIAG", null);
        foreach (var name in new[] { "BOOT", "IMPORTS", "FAILURE", "CPU", "CRASH", "THREADS", "MEMORY", "STATS", "CONSOLE" })
            Environment.SetEnvironmentVariable($"SHARPEMU_DIAG_{name}", null);

        var config = DiagnosticConfig.Load();
        Assert.False(config.IsAnyEnabled);
    }

    [Fact]
    public void Load_WithGlobalEnable_EnablesDefaults()
    {
        // Clear all specific vars first
        foreach (var name in new[] { "BOOT", "IMPORTS", "FAILURE", "CPU", "CRASH", "THREADS", "MEMORY", "STATS", "CONSOLE" })
            Environment.SetEnvironmentVariable($"SHARPEMU_DIAG_{name}", null);
        Environment.SetEnvironmentVariable("SHARPEMU_DIAG", "1");

        var config = DiagnosticConfig.Load();
        Assert.True(config.IsEnabled("BootTimeline"));

        Environment.SetEnvironmentVariable("SHARPEMU_DIAG", null);
    }
}
