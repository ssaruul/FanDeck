# FanDeck

A small Windows tray app that controls case-fan speed and motherboard RGB. It replaces Armoury Crate's Fan Xpert and Aura Sync with one light program.

It was built for and tested on one PC: an **ASUS ROG STRIX B650E-F GAMING WIFI**. That board has a Nuvoton **NCT6799D** fan chip and an **ASUS Aura USB** controller (`0B05:19AF`). Other ASUS boards with the same chips may work, but they haven't been tried.

## What it does

- **Detects** the fan headers with a fan spinning on them, the CPU and GPU temperatures, and the Aura RGB headers.
- **Speed profiles.** Silent, Balanced and Performance follow a temperature curve. Max runs every fan at 100%. Manual gives each fan its own fixed slider.
- **A curve per fan.** Pick a fan above the graph to edit its own curve for the active profile, so the CPU fan and the case fans can behave differently. Drag points to reshape the curve, double-click to add a point, right-click to remove one. The other fans' curves show faintly behind it, and dots show where each fan is right now.
- **Choice of temperature per fan.** Each fan follows the **CPU**, the **GPU**, or **the hotter of the two**. By default the CPU fan follows the CPU and case fans follow the hotter one.
- **RGB.** Static, breathing, strobing, colour cycle, rainbow or off, on all lighting or on one header. Effects are stored on the board, so they keep running after FanDeck closes.
- **Tray app.** Right-click to switch profiles. The icon's colour shows the active profile. It can start with Windows through a scheduled task, so there's no admin prompt at each boot.

## Safety

- Fans never go below 20% (25% for the CPU fan).
- Every fan goes to 100% if the CPU reaches 95 °C or its temperature can't be read, including fans that follow the GPU. If the GPU can't be read, fans that follow it use the CPU instead.
- Temperature is averaged over 3 s, and speed changes are rate-limited, so short Ryzen temperature spikes don't make the fans rev.
- Headers FanDeck can't name, and pump headers, are left to the BIOS unless you opt in.
- On exit or crash, the fans go back to the BIOS.
- While Armoury Crate's fan service is running, FanDeck only watches, so the two don't fight. **Take control** disables `AsusFanControlService` and `LightingService`. **Give back to Armoury Crate** restores exactly how they were set up.

## Anti-cheat

FanDeck is a normal user-mode program. It never touches a game's memory, injects into games, or hooks anything. It reaches the fan chip through the [PawnIO](https://pawnio.eu) driver, via LibreHardwareMonitor.

- FACEIT blocked PawnIO **before 2.1.0**, because of a shared signing certificate. 2.1.0 and later use a new signature. FanDeck warns you if the installed PawnIO is older.
- **Game-safe mode** (tray menu) hands the fans to the BIOS and unloads PawnIO from the kernel completely. That takes about 0.2 s and needs no reboot.
- By default, game-safe mode turns on by itself while FACEIT runs and turns off when FACEIT closes.

## Requirements

- Windows 10/11 x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PawnIO](https://pawnio.eu) 2.1.0 or newer
- Administrator rights (needed to reach the fan chip)

## Build and run

```
dotnet publish -c Release -o <folder>
```

This produces a single `FanDeck.exe`.

| Command | What it does |
|---|---|
| `FanDeck.exe` | Opens the window. If FanDeck is already running, brings it to the front. |
| `FanDeck.exe --tray` | Starts hidden in the tray. The autostart task uses this. |
| `FanDeck.exe --detect [file]` | Prints what it can see without changing anything. |
| `FanDeck.exe --exit` | Closes the running copy cleanly, handing the fans back to the BIOS. Useful before updating. |

Settings live in `%APPDATA%\FanDeck\config.json`, and errors go to `log.txt` in the same folder.

## Status

This is a personal tool, verified on one machine: detection, fan control, release, handing over from Armoury Crate, the autostart task, and loading and unloading the driver. **Writing RGB effects hasn't been verified on hardware yet.**

## Credits and license

- The Aura USB protocol in `Hardware/AuraUsb.cs` is ported from [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (GPL-2.0-or-later).
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0)
- [HidSharp](https://www.zer7.com/software/hidsharp) (Apache-2.0)
- [PawnIO](https://github.com/namazso/PawnIO) (GPL-2.0-or-later)

FanDeck is licensed under the **GNU General Public License v3.0 or later**. See [LICENSE](LICENSE).
