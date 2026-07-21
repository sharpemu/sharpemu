# How to Create a SharpEmu Diagnostic Plugin

Creating a diagnostic plugin takes 5 minutes. Here's how.

## Step 1: Implement `IDiagnosticPlugin`

```csharp
using SharpEmu.Diagnostics.Contracts;
using SharpEmu.Diagnostics.Contracts.Events;

public sealed class MyPlugin : IDiagnosticPlugin
{
    public static PluginMetadata Meta => new()
    {
        Name = "MyPlugin",
        Version = "1.0",
        Description = "Does something useful",
        EnvVar = "SHARPEMU_DIAG_MINE",
        EnabledByDefault = false
    };

    public PluginMetadata Metadata => Meta;

    public void Initialize(IDiagnosticContext context)
    {
        // Called once at startup. Save context if you need to publish events.
    }

    public void OnEvent(IDiagnosticEvent e)
    {
        // Called for EVERY event. Check the category/type and handle yours.
        if (e.Category == "import" && e is ImportEvent ie)
        {
            // Process the event
        }
    }

    public object? Shutdown()
    {
        // Called at session end. Return a string/object for the exporter to write.
        // Do NOT write files directly — the exporter handles IO.
        return "=== My Plugin Report ===\n  Nothing happened.";
    }
}
```

## Step 2: Register it in `DiagnosticManager.Start()`

```csharp
if (config.IsEnabled("MyPlugin")) _registry.Register<MyPlugin>();
```

## Step 3: Enable it

```bash
# Via environment variable
export SHARPEMU_DIAG_MINE=1

# Or via diagnostics.json
echo '{"MyPlugin": true}' > diagnostics.json

# Or enable all default plugins
export SHARPEMU_DIAG=1
```

## Available Events

| Event | Category | When |
|---|---|---|
| `BootEvent` | boot | Boot stage reached |
| `ImportEvent` | import | Import call completed |
| `CpuEvent` | cpu | Instruction checkpoint |
| `MemoryEvent` | memory | Allocate/Free/Map/Unmap |
| `ThreadEvent` | thread | Create/Sleep/Wake/Exit |
| `CrashEvent` | crash | SIGSEGV/SIGILL/etc. |
| `GpuEvent` | gpu | Submit/Draw/Flip/Present |

## Rules

1. **Never write files** — return data from `Shutdown()`, the exporter handles IO.
2. **Never call other plugins** — publish events via `context.Publish()`.
3. **Be fast** — `OnEvent` is called for every event. Queue long work internally.
4. **No game-specific code** — plugins are general-purpose tools.
5. **No AI, no database** — just collect and return data.

## Architecture

```
Event published → EventBus → all plugins' OnEvent()
                              ↓
                    Plugin collects data internally
                              ↓
                    Shutdown() returns data
                              ↓
                    Exporter writes JSON/Text/Markdown
```
