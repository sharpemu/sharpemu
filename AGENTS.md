<!--
SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Working on SharpEmu

SharpEmu is a C# PS5 emulator with a PS4 backward-compatibility mode.

- Correctness before compatibility numbers.
- General solutions before title-specific workarounds.
- Measured performance before speculative optimization.
- Verified results before claims.

## Emulation changes

- Fix the guest-visible behavior shared by titles that exercise the same path. For a rendering bug, identify the command, resource state or shader operation responsible. Do not introduce title-specific workarounds based on game IDs, shader hashes, hardcoded guest addresses or other incidental identifiers.
- Check which guest mode owns the behavior. When consulting a PS4 reference for a shared command, verify its fields and semantics on the PS5 path before changing code used by both modes.
- Keep all PS4-specific optimizations and implementations within GnmMode whenever possible. Do not mix PS4-specific behavior with the native PS5 execution path. Shared infrastructure should remain platform-neutral unless a change is explicitly required by both modes.
- Implement the effects a guest can observe from an HLE export, including output values, resource lifetime, callbacks and errors as applicable. A resolved import alone does not demonstrate that the guest can use the function.
- Do not silently skip unsupported guest operations or return fabricated success values. Report unsupported behavior explicitly with actionable diagnostics. Do not hide failures by weakening validation or disabling execution paths.
- Use public documentation, clean-room findings or original research. When real PS5 output or a minimal homebrew reproducer is available, record the inputs and compare the result against the emulator.
- Follow [.editorconfig](.editorconfig) and the surrounding C# conventions. Reuse existing helpers, express intent through names and keep unrelated refactoring out of the diff. Include the project's SPDX header in new files and preserve existing copyright and license notices.

## Graphics and shader compilation

- Preserve guest instruction semantics, including execution masks, subgroup behavior, memory ordering and synchronization.
- Fix shader translation at the instruction, IR or lowering level responsible for the incorrect behavior.
- Do not replace unsupported instructions with no-ops or arbitrary constants.
- Preserve resource barriers, image layouts and queue synchronization semantics.
- Verify changes against both native PS5 and PS4 backward-compatibility paths when shared infrastructure is affected.

## Performance

- Avoid unnecessary allocations, copying, locking and synchronization in hot paths.
- Preserve asynchronous execution where possible. Do not introduce blocking waits merely to simplify implementation.
- Profile before optimizing. Do not claim performance improvements without measurements.
- Preserve cache correctness, resource lifetime and invalidation semantics when optimizing.

## Change scope

- Make the smallest complete change that fixes the underlying issue.
- Do not introduce new abstractions, dependencies or architectural changes unless required by the task.
- Preserve existing public interfaces and configuration behavior unless explicitly requested otherwise.
- Do not revert or overwrite unrelated working-tree changes.

## Verification

- Never claim that a game boots, renders correctly or performs better unless it has actually been tested.
- Distinguish build verification, automated tests and in-game verification.
- Report regressions, failed or skipped checks and unverified behavior explicitly.
- Do not weaken existing tests to make an implementation pass.
- If a required verification cannot be performed, explain why rather than implying success.

## Commits

- Use a single subject line in the form `[Tag] Short English imperative`. Choose the subsystem that owns the change, such as `[GPU]`, `[Vulkan]`, `[ShaderCompiler]`, `[AudioOut2]` or `[GUI]`. Omit the body, signatures and co-author trailers by default. Preserve required attribution and genuine co-author trailers; never invent or remove legitimate credit.
- **Emulation paths:** split independent behavior changes into small commits, even when they touch the same file. For example, a guest descriptor-size fix and an unrelated GPU synchronization fix should be individually reviewable and revertible. Keep a change and the wiring it needs to compile together.
- **GUI features:** a single feature can be one commit across the view, settings, event handling and translations. For example, `[GUI] Add Xbox controller glyphs and hints to the GUI` can include all the files needed to display those hints. Hunk staging is optional when whole files belong to the same change.
- For mixed files, stage selected hunks with a contextual patch and `git apply --cached`. Keep context around insertions and inspect `git diff --cached` before committing so adjacent edits stay with their owner.
- Order commits by dependency so each builds. Group independent test changes in one final `[Tests]` commit, but keep regression tests with their corresponding fix when necessary for independent verification or atomicity. Check the full series against the intended diff and build the final tree with `dotnet build SharpEmu.slnx`. Verify intermediate commits individually when needed to establish that each builds; a final build alone does not establish this. Run the full test suite on request or before pushing. For in-game verification, use a Release publish of the affected entry point.
- When asked to commit on the current branch, use that branch, including `main`. Rebase a local feature branch when it needs updates from `main`, provided unrelated working-tree changes are protected. Do not rewrite shared or published history without explicit approval. Push only when asked.

## Pull requests

When asked for PR summary text, use the exact heading below and usually three or four bullets. Write one short English sentence per bullet. Add bullets for larger changes when there are distinct effects to describe. Keep the summary free of extra sections, introductory paragraphs and generated-by lines.

```markdown
# What PR does?

- Add Xbox controller glyphs to the launcher.
- Show the matching button hint for each action.
- Update translations for the new hints.
```

For submission, the contributor writes the summary in their own words and includes it above the required template sections. Record the game or application tested and the observed result under Testing, or use `N/A` for changes with no runtime impact. Complete the checklist personally. See the [PR template](.github/pull_request_template.md) and [contribution guidelines](CONTRIBUTING.md).
