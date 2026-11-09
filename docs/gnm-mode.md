<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# GnmMode: Experimental PS4 Backward Compatibility

GnmMode is SharpEmu's experimental approach to PS4 backward compatibility. Rather than building a separate PS4 emulator, it adapts PS4 applications and graphics workloads to SharpEmu's existing PS5-oriented runtime.

The design is conceptually inspired by the PS5's backward compatibility, but it is an independent implementation and does not claim to reproduce Sony's internal approach.

## How it works

PS4 applications retain their original interfaces and expectations. GnmMode handles the differences needed to run them within SharpEmu, including PS4-specific system interfaces and GNM/GCN graphics behavior.

Where possible, GCN instructions and graphics state are translated into representations understood by SharpEmu's AGC-oriented execution path. Some PS4-specific texture formats and memory layouts require dedicated handling.

GnmMode reuses SharpEmu's existing native CPU execution infrastructure, shader intermediate representation, compilation pipeline, renderer, and resource caches. It does not introduce a separate shader recompiler or graphics backend for PS4 games.

## Current status

PS4 trophy packages use the TRP container and AES-encrypted ESFM metadata. GnmMode derives the metadata key from a user-supplied 128-bit trophy key and the package's NP communication ID in `sce_sys/npbind.dat`. The key is configured under **Options → GnmMode → Trophy key**; it is not bundled with SharpEmu. Trophy names, descriptions, icons, unlock timestamps, and notifications feed the existing trophy system. PS4 trophy calls use their own structure layouts, and platinum eligibility follows the package's trophy links so unrelated DLC trophies do not block the base game's platinum. The current integration handles the first trophy set (`trophy00.trp`, service label 0).

GnmMode is experimental. Some PS4 games can progress into menus or gameplay, but compatibility, rendering accuracy, performance, and system-service support remain incomplete. Results for individual games should not be treated as evidence of general compatibility.

## References

The implementation draws on publicly available technical information about PS4 graphics interfaces and GCN behavior, including [shadPS4](https://github.com/shadps4-emu/shadPS4) and [OpenGNM](https://github.com/PS4-OpenGNM/opengnm). GnmMode uses SharpEmu's own shared compiler and rendering architecture rather than those projects' execution pipelines.

For available GnmMode diagnostics and switches, see the [environment-variable documentation](sharpemu-gui-undocumented-env-vars.md#gnmmode-ps4--gcn).
