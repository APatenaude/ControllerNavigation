# Controller Navigation

Use any game controller as a mouse and keyboard in Windows apps, without it ever getting in the way
of a game.

Every app gets the mouse, except games and apps that read the controller themselves (Steam,
Moonlight, emulators). Those are detected: an app that has the controller open, a game Windows
already knows, or anything under a game folder (Steam, Xbox, Epic, GOG, Riot) is left alone, so a
game you install tomorrow needs no profile. The desktop, browsers, Discord and VLC always get the
mouse. Any app can be set to Always or Never from the tray icon. Holding Menu (Start) gives a second
layer that works everywhere: volume, media keys, moving windows between screens.

The full design and the reasons behind it are in [docs/SPEC.md](docs/SPEC.md).

## Install

Download `ControllerNavigation.exe` from the [latest release](https://github.com/APatenaude/ControllerNavigation/releases/latest)
and run it. One administrator prompt installs it to `C:\Program Files\ControllerNavigation`, starts it
at sign-in and keeps it up to date from the GitHub releases. Nothing to build, nothing else to install.

Then, once:

- Uninstall or turn off JoyXoff (both would drive the mouse at once).
- In Steam: turn off the Desktop Layout and the Guide Button Chord Layout (Settings > Controller).
- The settings window's **Checks** page lists anything else on the PC that would fight it.

## Default buttons

In apps that get the mouse:

| Control | Action |
|---|---|
| Left stick | pointer |
| Right stick | scroll |
| A (bottom) | Enter |
| B (right) | Esc |
| X (left) | left click (hold to drag) |
| Y (top) | Space |
| D-pad | arrow keys |
| LB / RB | back / forward |
| LT | right click |
| RT | hold for a precise pointer |
| View | Esc |
| Menu | tap: play / pause; hold: layer 2 |
| L3 | middle click |
| R3 | on-screen keyboard |
| Guide | left to Windows (holding it opens Task View on Windows 11) |

Hold Menu (Start), everywhere:

| Control | Action |
|---|---|
| LT / RT | volume down / up |
| View | mute |
| LB / RB | previous / next track |
| D-pad left / right | move the window to the next screen (wraps round, stacked screens too) |
| D-pad up / down | maximize (or restore) / minimize |
| Y | minimize all |

In a game, the game also sees the buttons of a layer-2 combo, so Menu opens its pause menu.

Everything is configurable in the settings window (tray icon > Settings): every button and layer, long
presses, the sticks, speeds and dead zones, the always/never app rules with per-app overrides and
window-title rules (for example, off while Chrome shows a cloud-gaming tab), the game detection, and
which controllers are used.

## Build from source

Needs the .NET 10 SDK.

```bash
dotnet build src/ControllerNavigation
```

```bash
dotnet run -c Release --project tests/ControllerNavigation.Tests
```

`dev\Publish.ps1` makes the single self-contained exe in `dist\`. Pushing a `v*` tag that matches the
version in `Directory.Build.props` builds and publishes a release (`.github/workflows/release.yml`).
