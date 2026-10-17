<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Contributing

> [!IMPORTANT]
> The pull request template is mandatory.
>
> Pull requests that do not follow the template or leave the required checklist incomplete will be closed without review, even if the proposed code is technically correct or beneficial. Please review these contribution guidelines before submitting a pull request.

Contributions are always welcome!

Before opening a pull request, please keep the following in mind:

- Keep PRs small and focused on a single topic.
- Discuss large architectural changes before implementing them.
- Follow the project's existing coding style.
- Do not submit generated code unless you fully understand and can maintain it.
- Do not introduce Sony proprietary code, firmware, keys, decrypted assets, or other copyrighted PlayStation materials.
- All reverse engineering should be based on publicly available information, clean-room techniques, or your own original research.
- Game-specific hacks should be avoided whenever possible. Prefer generic implementations that improve overall compatibility.
- New features should not break existing behavior.
- Ensure the project builds successfully before submitting a PR.

If you're unsure about a design decision, feel free to open a discussion or draft PR first.

## Pull Request Expectations

Pull requests should provide real, observable emulator behavior rather than only suppressing errors or unresolved imports.

Changes that only return success, zero, or fabricated handles without implementing the expected state, output, or side effects will generally not be accepted. Functions that create resources, write output structures, register callbacks, or expose runtime state should model the behavior required by the guest.

When applicable, PRs should include:

- The affected game or application.
- Relevant logs or failing imports.
- Behavior before and after the change.
- Real game testing and known limitations.

Avoid submitting large collections of speculative NIDs or unrelated exports. Keep each PR focused on one problem or a closely related set of changes.

Large architectural changes should be discussed with the maintainers before implementation. Contributors are encouraged to ask first when they are uncertain whether a proposed direction fits the project.

Opening a PR does not guarantee that it will be merged. Maintainers evaluate changes based on correctness, evidence, testing, scope, maintenance cost, and the long-term direction of the project.

## AI-Assisted Contributions

AI-assisted development is welcome and may be used for research, reverse engineering, code generation, or documentation.

However, contributors are expected to fully understand every line of code they submit. By opening a pull request, you confirm that you are able to explain, modify, debug, and maintain the submitted code without relying on the AI that generated it.

When submitting an AI-assisted PR:

- Clearly explain **what the change does**, **why it is needed**, and **what problem it solves**, using your own words.
- Describe **how you verified the change**, including the games, applications, or test cases used.
- Avoid excessive product-level logging. Use logging only when it provides meaningful diagnostic value.
- Comments should document design decisions or implementation details in your own words. Avoid generic AI-generated comments that merely restate what the code already does.
- Be prepared to answer review questions about the implementation. "The AI generated it" is not considered a sufficient explanation.
- Large AI-generated changes without a clear understanding of the implementation are unlikely to be accepted.
- If the implementation cannot be reasonably explained during code review, the pull request may be rejected regardless of whether it works.

The quality, correctness, maintainability, and long-term ownership of the submitted code remain the responsibility of the contributor.

## Coding Style

SharpEmu follows a consistent coding style across the project. Please ensure your contributions match the existing style.

- Use **4 spaces** for indentation (no tabs).
- Use **2 spaces** for XML-based files (e.g. `.csproj`, `.props`, `.targets`, `.xml`, `yml`, GitHub workflow files where applicable).
- Respect the project's `.editorconfig`.
- Ensure every text file ends with a **single trailing newline**
- Avoid formatting-only commits unless they are the purpose of the PR.
- Keep naming, formatting, and file organization consistent with the surrounding code.
- Prefer small, focused changes over large refactors.

### REUSE Compliance

This repository follows the REUSE Specification.

Every new file must contain the appropriate SPDX license header. Pull requests that do not comply with the project's REUSE requirements will fail CI and will not be merged.

### Recommended Development Environment

The repository includes an `.editorconfig` and Visual Studio solution files.

For the best experience, we recommend using:

- Visual Studio Code with;
  - C#
  - C# Dev Kit

Most editors that support `.editorconfig` will automatically apply the project's formatting rules.
