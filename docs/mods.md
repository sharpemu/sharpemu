# Game mods

Right-click a game in the library and choose **Mods**. Mod support works for both
PS4 and PS5 games, whether the game is a folder or a `.zar` archive. It also applies
when launching from a desktop shortcut.

Use **Install from ZIP** or **Open mods folder** to add a mod manually, then
**Refresh**. Enable the mods you want with their checkboxes. New mods start
disabled. Selections are saved automatically and apply on the next game launch.

Each mod has its own folder under `user/mods/<game ID>` in SharpEmu's data
directory. This follows the same portable, AppData and Linux XDG location as the
other user data. **Open mods folder** opens the exact directory for the game.

The files inside a mod must follow the original game's directory structure:

```text
user/mods/PPSA00001/
  Textures/
    data/textures/character.bin
  Audio/
    data/sound/music.bin
```

For example, `Textures/data/textures/character.bin` replaces the game's
`/app0/data/textures/character.bin`. The emulator reads it directly from the mod
folder. It never copies mod files into the original game folder, the ZAR archive,
or the extracted archive cache. Files without a mod replacement continue to come
from the original game. Added files and folders appear in game directory listings.

Mods appear in alphabetical order. When several enabled mods replace the same
file, the last one in that order takes priority. Disabling a mod restores the
original file on the next launch. Versions of a game with the same title ID share
their mod selection; check that a mod supports the version you intend to launch.

ZIP files may contain game-relative paths directly, an `app0` wrapper, or a
wrapper matching the ZIP filename. Existing mod folders are preserved: repeated
imports receive a numbered folder name. Extraction displays progress, byte counts
and the current filename. Closing the dialog cancels extraction and removes its
temporary files. ZIP paths that escape the mod directory and symbolic links are
rejected.
