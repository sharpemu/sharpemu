# SharpEmu — Community-Powered AI Debugging

SharpEmu is not only an emulator. It is designed to become a debugging platform that allows anyone to help improve PS5 compatibility.

## Why?

Testing modern games is expensive and time-consuming.

Normally, emulator developers must:
- Download and manage many different games
- Reproduce every crash themselves
- Spend significant time collecting debug information
- Repeat the same investigation for every title

This does not scale.

## Our Approach

Instead of requiring developers to reproduce every issue, SharpEmu generates structured offline debug reports.

A player only needs to:
1. Run the game
2. Let SharpEmu automatically generate a debug package
3. Open that package with their preferred AI assistant (ChatGPT, Claude, Gemini, Copilot)
4. Send the AI analysis back to the project

No programming knowledge is required.

## Why AI?

Many users already have access to free AI assistants. Rather than maintaining expensive cloud infrastructure, every user can use their own AI account to analyze crashes locally.

This distributes the workload across the community. If 100 users test 100 different games, they collectively perform work that would otherwise require hundreds of hours from emulator developers.

## Privacy First

SharpEmu does not automatically upload any data. All reports are generated locally. The user decides whether to:
- Keep them
- Inspect them
- Analyze them with an AI
- Share them with the project

Nothing is transmitted automatically.

## Community Workflow

```
Game → SharpEmu → Offline Debug Package → User's AI Assistant → AI Analysis → Developer
```

## Debug Report Format

Every crash generates a structured JSON report:

```json
{
  "schema": 1,
  "session": { "game_id": "PPSA14677", "emulator_version": "v1.0.0011" },
  "boot_status": { "self_loader": "OK", "tls_setup": "OK", "agc_init": "NOT_REACHED" },
  "crash": {
    "type": "NULL_POINTER",
    "rip": "0x8007F33FE",
    "null_register": "RDI",
    "root_cause_candidate": {
      "description": "std::call_once unresolved → game proceeds with NULL object",
      "confidence": 0.85,
      "suggested_fix": "Implement std::call_once (NID: DiGVep5yB5w)"
    }
  },
  "missing_nids": [...],
  "suggested_actions": ["implement std::call_once", "check object initialization"]
}
```

See `docs/AI_DEBUG_PROMPT.md` for the ready-to-use AI prompt.

## Long-Term Goal

Make emulator debugging accessible to everyone. You don't need to be a reverse engineer. You don't need to understand PS5 internals. You only need to run a game and share the generated analysis.

Every report helps improve compatibility for everyone.
