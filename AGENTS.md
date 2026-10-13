<!--
SPDX-FileCopyrightText: Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Repository workflow reminders

- Never create a draft PR automatically; only create or update one when the user explicitly asks.
- When the user asks to create a draft PR after implementation work is complete, first inspect the current repository and CI state, then run the relevant local tests/builds using the current code before creating or updating the draft.
- For updater changes, include the full solution test command when Release artifacts are available:
  `dotnet test SharpEmu.slnx -c Release --no-build --verbosity normal`
- If CI uses architecture-specific environment settings, reproduce those settings locally where possible and report when the host cannot emulate the runner architecture exactly.

## Validation tools

- Use the repository-local SDK at `.dotnet/dotnet.exe` for all .NET commands; the required SDK is pinned by `global.json`.
- Standard full validation is:
  `dotnet build SharpEmu.slnx -c Release --no-restore`
  followed by `dotnet test SharpEmu.slnx -c Release --no-build --verbosity normal`.
- For updater-only iteration, use the focused `UpdaterTests` filter before the full suite.
- The CI REUSE check uses `fsfe/reuse-action@v6`; a local `reuse` executable is not assumed to be installed.
- Do not repeat unchanged checks solely to rediscover the workflow commands; use this section as the command reference and rerun only after relevant changes.
