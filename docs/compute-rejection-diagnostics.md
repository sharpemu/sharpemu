<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Shader rejection diagnostics

`SHARPEMU_STRICT_COMPUTE` controls the rejections listed below for compute dispatches
and graphics draws. The existing variable name is retained; there is no separate
`SHARPEMU_STRICT_DRAW` switch. Restart the game after a change. The pipeline cache and
draw executor read the value when created.

- Unset or `1`: stop on the rejection so that the failure can be diagnosed (default).
- `0`: skip the affected dispatch or draw. Only this value enables skipping.

The GUI provides this switch under **Environment > Debug**, globally and per game.
It is on by default. Turning it off saves an explicit `0`; an inherited process
environment value does not override the GUI choice. Other values retain strict mode.

## Complete skip list

With the switch set to `0`, these are the only handled rejection categories:

| Rejection | Detection point | Operation skipped |
| --- | --- | --- |
| Resource-plan extraction throws `ResourcePlanException`, including an unsupported resource expression or a value graph that does not converge | `ShaderProgramCache.CreateEntry` | The dispatch or draw that needs the rejected shader |
| Shader compilation returns failure or no compiled program, including an unsupported opcode reported by the compiler | `ShaderProgramCache.CompilePermutation` | The dispatch or draw that needs the rejected shader |
| Indirect image candidates have incompatible image classes (`IncompatibleImageCandidates`) | `ResourceMaterializer.Materialize`, handled by `ShaderProgramCache` | The dispatch or draw that needs those resources |
| Indirect image expansion exceeds the dense image resource limit (`ImageCapacityExceeded`) | `ResourceMaterializer.Materialize`, handled by `ShaderProgramCache` | The dispatch or draw that needs those resources |
| A graphics texture's cached image type cannot support the requested view type, such as a 2D view of a 1D image | Vulkan `ValidateDrawImageTypes`, handled by `RenderExecutor.RecordDraw` | The whole indexed or automatic draw |

The last category applies to vertex and pixel resource preparation, not compute
image binding. It checks available, non-stale cache entries and excludes host movie
textures. A mismatch detected later in binding remains fatal.

The draw image-type check runs before resource binding and draw recording. Skip mode
does not create an invalid view or issue the rejected draw. The preparation scope is
released, and the caller resets the image bindings before the next operation.

## Failures that remain fatal

- Shader decoding failures and invalid shader source reads.
- Unreadable resource memory, invalid indirect table state, and materialization failures
  other than the two named categories above.
- Invalid binding layouts or invalid resource snapshot state.
- Other image checks, including format, usage, aspect, component mapping, and range
  checks reached during binding or view creation. The image cache checks are unchanged.
- Image-type mismatches in compute binding or outside the graphics preparation check.
- Host allocation failures, device loss, synchronization failures, and unexpected exceptions.

This is not a general exception bypass. Setting the switch to `1` also makes every
category in the skip list fatal. Supported operations use the same path in both modes.

## Warnings and test limits

Program rejections log `COMPUTE_SKIPPED` or `DRAW_SKIPPED` once per stage, shader hash,
and code size in each pipeline cache. Draw image-type rejections log `DRAW_SKIPPED`
once per shader hash, image type, and view type in each draw executor. Warning counts
are not skipped-operation counts. No rejected permutation is recorded as compiled;
a later request can retry it.

A skipped operation does not produce its outputs. Missing images, incorrect results,
later errors, and stalls are possible. FPS can rise because less work is executed.
Skip-mode smoke tests can check progress and expose other defects, but must record
the skip warnings. They do not close the skipped defects or establish a final visual
or performance baseline. This switch does not restore the old renderer.

## PowerShell

Enable strict testing in PowerShell:

```powershell
$env:SHARPEMU_STRICT_COMPUTE = '1'
```

Explicitly enable skip mode:

```powershell
$env:SHARPEMU_STRICT_COMPUTE = '0'
```
