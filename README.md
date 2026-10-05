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

---

<p align="center">
  <a href="https://discord.gg/zdTuUU9Uwn">
    <img src="https://img.shields.io/badge/Discord-Join%20our%20Community-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Join our Discord">
  </a>
</p>

<p align="center">
  <strong>Join our official Discord server for development updates, compatibility discussions, support, and community chat.</strong>
</p>

---

<p align="center">
  <a href="#support">
    <img src="https://img.shields.io/badge/Support-GitHub%20Sponsors%20%26%20Crypto-EA4AAA?style=for-the-badge&logo=githubsponsors&logoColor=white" alt="Support SharpEmu">
  </a>
</p>

---

> [!IMPORTANT]
> **Official channels:** Only **sharpemu.app** and the links listed in this GitHub repository are affiliated with SharpEmu. Any other websites, accounts, or donation pages are unofficial and unauthorized.

> [!NOTE]  
> SharpEmu supports Windows x64, Linux x64, and macOS x64. Apple Silicon Macs
> can run the macOS x64 build through Rosetta 2, and Windows on ARM devices
> (e.g. Snapdragon) can run the Windows x64 build through Windows' built-in
> x64 emulation.

> [!WARNING]  
> SharpEmu is an experimental PS5 emulator developed from scratch in C#. The current focus is on accuracy and infrastructure setup rather than game-specific compatibility.

## Info

SharpEmu is an emulator project currently in its early stages of development.

This project is developed purely for research and educational purposes. There are no commercial goals associated with it. We enjoy learning about system architecture and reverse engineering.

SharpEmu focuses exclusively on the PlayStation 5.  
Our goal is **not** to emulate PS4 games, as there is already an excellent emulator dedicated to that platform: **ShadPS4**.

## Games Tested

|               Demons Souls Remake                   |                     Astro Bot                        |
| :-----------------------------------------------------------: | :--------------------------------------------------------------------------------------------: |
| ![DeS screenshot](./.github/images/demons-souls.jpg) | ![Astro Bot](./.github/images/astro-bot.jpg) |

|                  Hades                    |                 Dead Cells                    |
| :------------------------------------------------------------------------: | :------------------------------------------------------------------: |
| ![Hades](./.github/images/hades.jpg) | ![Dead Cells](./.github/images/dead-cells.jpg) |

|                  PAC-MAN World Re-PAC                    |                 Tomb Raider V Remastered                    |
| :------------------------------------------------------------------------: | :------------------------------------------------------------------: |
| ![Pac-Man](./.github/images/pac-man-world-re-pac.jpg) | ![Tomb Raider V](./.github/images/tomb-raider-v-remastered.jpg) |

## Status

The emulator can currently load the `eboot.bin` of real games, execute native CPU instructions, and partially handle gpu-related functionality. Included 3D games.

SharpEmu supports Windows, Linux, and macOS hosts. Video output uses Vulkan on
Windows and Linux, and MoltenVK on macOS. Platform support is still experimental,
so compatibility and performance vary by game, operating system, and GPU driver.

## Using

Download the release archive for your operating system, extract it, and launch
SharpEmu with the path to a legally obtained game's `eboot.bin`.

Or command line;

Windows PowerShell:

```powershell
.\SharpEmu.exe "C:\path\to\game\eboot.bin" --log-to-file
```

Linux and macOS:

```bash
chmod +x ./SharpEmu

./SharpEmu "/path/to/game/eboot.bin" --log-to-file
```

SharpEmu supports environment variables that can be configured from the GUI. Undocumented variables are listed here: [docs/sharpemu-gui-undocumented-env-vars.md](docs/sharpemu-gui-undocumented-env-vars.md)

You can set them per game or globally in the GUI, or pass them directly through the CLI.

A Vulkan-capable GPU and current graphics driver are required. The macOS
release includes the MoltenVK Vulkan implementation.

> [!IMPORTANT]  
> This project does **not** support or condone piracy.  
> All games used during development and testing are dumped from consoles that we personally own.  
> Users are expected to use legally obtained copies of their games.

## Build

1. Install the .NET SDK version specified in [`global.json`](./global.json).
2. Clone the repository: `git clone https://github.com/sharpemu/sharpemu.git`
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

- [**GPL-2.0 license**](https://github.com/sharpemu/sharpemu/blob/main/LICENSE)

## Support

Support SharpEmu via GitHub Sponsors or cryptocurrency. Every contribution helps fund ongoing development and long-term maintenance. GitHub Sponsors is the preferred way to support the project, but cryptocurrency donations are also appreciated.

### ETH/USDT

`0xF315F5d986c790bB3A58DbE60F1B2760997dEd82`

### BTC

`bc1qmr9k8899njys5ny63xsues4jgmkk96erslrkmv`

## Contributing

Before opening an issue or pull request, please read our contribution guidelines:

**[CONTRIBUTING.md](./CONTRIBUTING.md)**

The guide covers:
- Coding style and formatting
- AI-assisted contributions
- Pull request expectations
- Testing guidelines
- Legal and reverse engineering policy
