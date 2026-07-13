<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu

<p align="center">
  <img src="./assets/images/logo.png" width=30% height=30% />
</p>

<p align="center">
  An experimental PlayStation 5 emulator for Windows, Linux and macOS.  
</p>

<p align="center">
  <a href="https://discord.gg/6GejPEDqpc">
    <img src="https://img.shields.io/badge/Discord-Join%20our%20Community-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Join our Discord">
  </a>
</p>

<p align="center">
  <strong>Join our Discord for development updates, compatibility discussions, support, and community chat.</strong>
</p>

---

> [!WARNING]  
> Currently the primary development target is Windows.

> [!WARNING]  
> SharpEmu is an experimental PS5 emulator developed from scratch in C#. The current focus is on accuracy and infrastructure setup rather than game-specific compatibility.

## Info

SharpEmu is an emulator project currently in its early stages of development.

This project is developed purely for research and educational purposes. There are no commercial goals associated with it. We enjoy learning about system architecture and reverse engineering.

SharpEmu focuses exclusively on the PlayStation 5.  
Our goal is **not** to emulate PS4 games, as there is already an excellent emulator dedicated to that platform: **ShadPS4**.

## Status

The emulator can currently load the `eboot.bin` of real games, execute native CPU instructions, and partially handle kernel-related functionality. However, several critical components are still missing.

Current capabilities include:

* Loading `eboot.bin` and `.elf` files
* Executing native CPU instructions
* Reading basic game metadata (title, version, etc.)
* Loading system modules (`prx` / `sys_module`)
* Partial support for some kernel functions  
* `Fiber` and `AMPR` exports
* PlayGo scenarios
* Initial loading game files
* Shader/resource submits and AGC initial
* Video outputs in some games

Some games have reached like `sceVideoOut` and AGC stages.

Currently the project primarily targets Windows. Cross-platform support (Linux and macOS) is planned, but development is currently focused on Windows to simplify early-stage debugging and iteration.

## Using

* Build or Publish project or download in release tab.
* Open Powershell.
  * Run Emulator GUI.
  * Or command: `.\SharpEmu "eboot.bin" 2>&1 | Tee-Object -FilePath "log.txt"`

## Games Tested

* **Demon's Souls Remake**
  * [Demon's Souls [PPSA01341]](https://github.com/par274/sharpemu/issues/2)
  * Demon's Souls is now video loop. Shaders are ready to be converted to SPIR-V/Vulkan. We are continuing our work on this.
  ![DeS videoOut submit first frame](./.github/images/des-videoout-shaders.jpg)

* **Poppy Playtime Chapter 1**
  * [Poppy Playtime Chapter 1 [PPSA20591]](https://github.com/par274/sharpemu/issues/3)

* **SILENT HILL: The Short Message**
  * [SILENT HILL: The Short Message [PPSA10112]](https://github.com/par274/sharpemu/issues/4)

* **Dreaming Sarah**
  * [Dreaming Sarah [PPSA02929]](https://github.com/par274/sharpemu/issues/9)
  * Real texture rendering for this game;
  ![Splash texture](./.github/images/dreaming-sarah.jpg)


> [!IMPORTANT]  
> This project does **not** support or condone piracy.  
> All games used during development and testing are dumped from consoles that we personally own.  
> Users are expected to use legally obtained copies of their games.

## Build

1. Install the **.NET SDK**.
2. Clone the repository: `git clone https://github.com/par274/sharpemu.git`
3. Open the solution file (`SharpEmu.slnx`) in **VSCode**.
4. Build the project: `dotnet build` or `dotnet publish`
5. Build artifacts will be located in the `artifacts` directory.

## Disclaimer

SharpEmu is an experimental emulator intended for research and educational purposes.

This project does not contain any copyrighted system firmware, game data, or proprietary PlayStation assets.

## Special Thanks

The following projects were extremely helpful during development:

* **[ShadPS4](https://github.com/shadps4-emu/shadPS4)**  
Helped with understanding the basic architecture of the PlayStation 4.

* **[Kyty](https://github.com/InoriRus/Kyty)**  
One of the few PS5 emulator projects available and very useful for studying native code execution.

* **Ryujinx**  
Provided valuable references for filesystem handling and low-level C# implementation patterns.

# License

- [**GPL-2.0 license**](https://github.com/par274/sharpemu/blob/main/LICENSE)

## Contributing

Before opening an issue or pull request, please read our contribution guidelines:

**[CONTRIBUTING.md](./CONTRIBUTING.md)**

The guide covers:
- Coding style and formatting
- AI-assisted contributions
- Pull request expectations
- Testing guidelines
- Legal and reverse engineering policy
