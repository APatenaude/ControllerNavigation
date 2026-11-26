# Controller Navigation: spec

A tray app that lets any game controller drive the Windows desktop, Chrome and a few apps as a mouse
and keyboard, without ever getting in the way of a game. It replaces JoyXoff. Installed from a GitHub
release and kept up to date from there: a new PC or a new controller never needs a rebuild.

Decisions below come from the interview with the owner on 8 Nov 2026, then a design review.

## The problem it solves

JoyXoff drives the mouse everywhere by default, so every game needs its own profile to turn it off,
otherwise the stick moves the cursor instead of (or as well as) the game's camera. The owner had 27
profiles, 24 of them only there to disable it. Profiles matched the full exe path, so an app that
updates into a new folder (Discord: `app-1.0.9230`, `app-1.0.9231`, ...) lost its profile each time.

## Which apps get the mouse

Changed on 9 Nov 2026 after the first test with the pad: the owner wants the mouse in every app (the
Claude app, VS Code...), except games and apps that read the controller themselves.

The settings window calls the rules **app exceptions**: "Always" and "Excluded" (the owner's word,
9 Nov 2026; "never" in settings.json).

- **Always** apps get the mouse, whatever else is true. Built in are only the apps that keep the
  controller open without using it, so detection alone would leave them out. On the owner's PC on
  9 Nov 2026, Explorer, Chrome and Discord held the 8BitDo open; Start, Search, Settings, Edge and
  VLC did not, or could not be looked into, so they get the mouse anyway and need no rule:
  - `explorer.exe` (the desktop and File Explorer);
  - the web browsers (`chrome.exe`, and `msedge.exe`, `firefox.exe`, `brave.exe`, `opera.exe`,
    `vivaldi.exe`, which have the same gamepad support for web pages);
  - `Discord.exe`;
  - Steam's windows (`steamwebhelper.exe`, which does not hold the pad itself: `steam.exe` does)
    with the title rule "Steam Big Picture Mode*", so Big Picture, which Steam drives with the
    pad, is left alone.
  This tool's own windows always get the mouse too.
- **Excluded** apps never get it.
- **Every other app is automatic**: it gets the mouse unless the GameDetector finds any of:
  - **it has a game controller open.** Any program that reads the pad holds a handle to its device
    (a game, Moonlight, an emulator, Steam). The detector lists the app's handles and compares their
    kernel names with the controllers' device names (XInput devices and HID game controllers,
    virtual ones included). Tested on the owner's PC on 9 Nov 2026: Steam, Chrome, Discord and
    Explorer hold the 8BitDo open, the Claude app does not. That is why the browsers, Discord and
    the shell are built-in Always apps;
  - **Windows recognized it as a game** (`HKCU\System\GameConfigStore\Children`, the list behind
    Game Bar and Game Mode: 49 entries on the owner's PC, almost all real games);
  - **its exe is under a game folder** (`\XboxGames\`, `\steamapps\common\`, `\Epic Games\`,
    `\GOG Games\`, `\Riot Games\`, editable). This catches Xbox app games, which read the pad
    through a Windows service and hold no handle themselves, and anti-cheat games, which cannot be
    looked into.
- A process that cannot be looked into (elevated, protected) counts as not reading the controller.
  A UWP app whose window has not attached yet is left alone until it does.
- An app not looked into yet stays off until the detector answers (a few milliseconds); then the
  on-delay applies. A process found reading the pad stays a reader until it exits. One that does not
  is looked at again every 2 s during its first 30 s in front (games open the pad some time after
  starting), then every 15 s (fewer handle looks into a process an anti-cheat may watch). When the
  controllers change (the pad switched on), every "not reading" answer is dropped and looked into
  again, since it was given with other controllers or none.
- Windows' game list has mistakes (on the owner's PC: Deluge, Meta Horizon): the tray's Always fixes
  them.
- The tray sets any app to Always, Never or Automatic in one click.
- Apps are matched by **exe file name** (case-insensitive), not full path, so updates that move the
  exe keep working. UWP apps sit inside `ApplicationFrameHost.exe`: the app is the process of its
  core window, not the frame host.
- **Window-title rules** (Always apps): an app can list title patterns that turn the mouse off while they
  match, e.g. Chrome with a title ending in "Xbox Cloud Gaming - Google Chrome". Patterns are anchored
  (start/end of the title), not "contains", since a page or video title is page-controlled text
  (a YouTube video named "GeForce NOW review" must not match). Titles are logged to help write them.
  No patterns are built in (real tab titles vary): the settings window fills one from the title last
  seen for that app.

### Foreground tracking

- The input thread reads `GetForegroundWindow` on every poll and classifies the window only when the
  handle changes (cached by window + process id). While an app with title rules is in front, its title
  is re-read about 4 times a second. No WinEvent hook: it misses the foreground going NULL and a UWP
  window attaching to a frame host already in front.
- **Off at once, on after ~300 ms of a stable allowed window.** During a game launch or a fullscreen
  mode switch the foreground can be NULL or flicker through the desktop; a click sent then would pull
  focus out of the game.
- Off when: no foreground window, the exe cannot be read (anti-cheat, process gone), the session is
  locked, or the PC goes to sleep.
- Turning on or off releases every key and mouse button the tool is holding. Buttons already held at
  the moment it turns on are ignored until released (HTPC's "inherited" buttons), so the A that closed
  a game does not click the desktop.

## Controllers: any pad

- Read through **SDL3** (C# bindings `ppy.SDL3-CS`, maintained by the osu! team), which supports Xbox,
  PlayStation, Switch, 8BitDo and most generic pads, with a community mapping database built in.
- Buttons are named by **position** (south, east, west, north), so the bottom face button is the same
  binding on every pad (A on Xbox, Cross on PlayStation, B on Switch). The settings window shows the
  connected pad's own labels.
- **Guide** means the pad's center button: Xbox, PS or Home. On Xbox pads SDL gets it by correlating
  RawInput with XInput (`SDL_HINT_JOYSTICK_RAWINPUT_CORRELATE_XINPUT`, on by default).
- Extra buttons are bindable when the pad has them: touchpad click, Share/Create/Capture, mic,
  back paddles.
- **All real pads drive it.** Virtual pads (Steam Input's, ViGEm, Parsec, Vibeshine, streaming guests)
  are off by default, so a guest streaming in cannot drive this desktop. Each pad shows in the
  settings with an on/off switch, remembered by its SDL id.
- **A pad SDL does not know**: a "map this controller" wizard in the settings (press each button in
  turn) saves an SDL mapping in the settings, loaded at start with `SDL_AddGamepadMapping`. No rebuild.

### Reading only, never changing the pad

Games keep using the pad while this tool reads it, so the tool must not change the pad's state:

- `SDL_HINT_JOYSTICK_ENHANCED_REPORTS = "0"`. SDL's default (`"1"`) switches PlayStation pads to
  enhanced reports, which "break Windows DirectInput for other applications that don't use SDL" and
  cannot be undone without turning the pad off
  ([SDL wiki](https://wiki.libsdl.org/SDL3/SDL_HINT_JOYSTICK_ENHANCED_REPORTS)).
- Home LEDs left alone (their hints unset: "0" would turn them off), player LEDs off, no rumble.
- Only enabled gamepads are opened: SDL's DirectInput backend may take a device exclusively, which
  a game (a force-feedback wheel) would then miss. A bare joystick is opened only while the mapping
  wizard reads it.
- `SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS = "1"`: the tool has no focused window.
- **Spike first** (a short throwaway test to answer one question): a PlayStation and a Switch pad
  read by the tool still work normally in a DirectInput game and in Steam, and Guide reads on Xbox and
  8BitDo pads with Steam running. If SDL's direct Switch driver changes the pad's mode, the Switch
  family falls back to SDL's plain RawInput path (a setting per pad family).

## Base map (apps that get the mouse)

The same map as HTPC's Mouse preset (the owner, 20 Nov 2026: "exactly the same in both projects",
except that HTPC moves windows in desktop mode only and keeps Guide as its Home), with Menu (Start)
as the layer-2 button. Guide is left to Windows: on the owner's Windows 11, holding it opens Task
View, and there is no setting to turn that off (9 Nov 2026).

| Control | Action |
|---|---|
| L stick | pointer, with acceleration |
| R stick | scroll (vertical and horizontal) |
| South (A) | Enter |
| East (B) | Esc |
| West (X) | left click (hold = drag) |
| North (Y) | Space |
| D-pad | arrow keys (repeat) |
| LB / RB | volume down / up, at once, repeating after 400 ms every 125 ms |
| LB + RB | mute / unmute (the second bumper while the first is down; no step, no repeat after it) |
| LT | right click |
| RT | left click (hold = drag) |
| LT + RT | middle click |
| View | Esc |
| Menu | tap: play/pause; hold: layer 2 |
| L3 | precise pointer (x0.5, no acceleration) on / off; off again when another app comes in front |
| R3 | on-screen keyboard (manual only, no pop-up on text fields) |
| Guide | nothing (Windows' own) |

- Pairs (LB + RB, LT + RT, bindable in the base map and app overrides, not in layers): a trigger
  whose own action is a click or key waits up to 80 ms (Feel) for the other, and the pointer waits
  with it, so a drag starts where it was aimed; let go sooner, it is a tap. A button whose action
  is anything else (a volume step) runs at once, and the other one pressed while it is down runs
  the pair instead of its own action. A pair forms only from two presses the base map owns: one
  held since a game, or pressed under a layer, keeps its job. An app override that rebinds either
  button drops the pair there.
- Acceleration: once the stick has stayed past 90 % of its travel (until it falls under 85 %) for
  200 ms, the pointer's speed ramps up to the Acceleration factor (default 3.5) over the
  acceleration time (default 2 s), and is back to normal the moment the stick leaves the edge;
  small moves stay slow without holding anything. Not while precise. The owner, 20 Nov 2026: "too
  fast for small moves but too slow for big moves".
- Pointer speed is physical, from the height each screen reports (its EDID, through GDI): full
  tilt starts at 134 mm a second on a desk screen (x1 pointer speed; 0.4 heights of the owner's
  34-inch ultrawides), 469 once accelerated. A screen taller than a desk one (33.5 cm) is watched
  from farther away, so it goes x the square root of its height over 33.5 cm: 208 mm a second
  (x1.55) on the owner's 65-inch TV; a smaller one is not slower. A screen that reports none (or
  under 10 cm, an aspect ratio only) moves 0.4 of its height a second; sizes are read again every
  5 s. Precise is half that start speed (67 mm a second on a desk screen), never accelerated. The
  owner, 25 Nov 2026: in screen heights the pointer was too fast on a scaled-up screen (the TV at
  200 %, 2.4 times as fast); he also found the start too fast at 1.1 heights a second, then 0.7
  (0.2.1, factor 4.5) and 0.55, and settled on 0.4 with a factor of 3.5 over a slower ramp, and
  precise at half (it was a quarter). 26 Nov 2026: the same mm a second as the desk was too slow
  on the TV; he chose the automatic square root over a speed set per screen.
- Volume in apps that get the mouse only: in a game a held bumper is play, never the volume.
- Settings files from before 20 Nov 2026 (version 1) have every binding still equal to the old
  defaults moved to these; bindings the owner changed are kept (`Migrations` in `Settings.cs`).

The on-screen keyboard closes as soon as a window that does not get the mouse comes forward, otherwise the pad
would type into the game behind it. It also closes on pause and on session lock.

On-screen keyboard pages (HTPC's, extended on 9 Nov 2026 at the owner's request):

- letters, symbols, and **Keys**: Esc, Tab, Enter, Backspace, Delete, Insert, Home, End, PageUp,
  PageDown, F1 to F12, PrintScreen, an arrow block, and a shortcut row (Alt+Tab, Win+D, copy,
  paste, cut, undo, select all, find, close window);
- **Ctrl, Alt, Shift and Win are toggles** held down in Windows until pressed again or until the
  keyboard closes, so Alt on, Tab, Tab, Alt off works as on a real keyboard. While one is held,
  other keys go out as virtual keys (a Unicode character does not combine with Ctrl). Alt or Win
  released with nothing pressed in between is masked (a tap of the unassigned key 0xE8 first), so
  it does not open the menu bar or Start;
- no modifier may stay held: everything is released when the keyboard closes, opens again, or its
  page reloads, and a key release Windows refused while an admin window was in front (no helper)
  is sent again when a normal window comes back.

## Layer 2: hold Menu (Start) + button (everywhere, games included)

| Control | Action |
|---|---|
| LB / RB | back / forward (Alt+Left / Alt+Right; not in games) |
| LT / RT | previous / next track |
| D-pad left / right | move the window in front to the next screen that way (wraps round, as Win+Shift+arrow: stacked screens work) |
| D-pad up | maximize (restore if already maximized) |
| D-pad down | minimize |
| North (Y) | minimize all |
| R3 | the controller menu (below) |

- Timing (HTPC's Start-chord rule): a button pressed while Guide is held stays a layer-2 button until
  it is released, even if Guide is released first; a button held before Guide was pressed keeps its
  base action. Guide and a button pressed in the same instant count as Guide first. While Guide is
  held, other base-map buttons are off (the sticks still move the pointer); buttons with no layer-2
  action do nothing. Any other control down while Guide is held (even one held since before) cancels
  Guide's own tap and hold actions.
- Volume and mute: through the volume keys by default (Feel.ShowVolumeSlider), because the owner
  wants Windows' volume slider to show (9 Nov 2026); one key press per 2 % of the step, Windows'
  own step. Off: through Core Audio (lifted from HTPC's `AudioOutputs.cs`), any step, no slider,
  and a game that swallows key presses cannot block it.
- The rest done through APIs, not shortcut keys, so a game that swallows keys or blocks the Win key
  cannot eat them and a lone Win never opens Start over a game: media through the system media
  session manager (GSMTC), window moves through `SetWindowPos` / `ShowWindowAsync`, minimize all
  through `Shell.Application.MinimizeAll`. To test with exclusive-fullscreen games.
- Menu alone (a short press, no other button) still does play/pause, as the layer's Tap action, but
  only in apps that get the mouse: in a game, Menu opens the game's pause menu and must not pause
  the music too. With the on-screen keyboard open, the short press goes to the keyboard (Enter and
  close). No hold action by default (the pads turn themselves off with their own Home hold).
- In apps that do not get the mouse (games), a layer combo counts only when its button comes at
  least 150 ms after the layer button: a game's own combos (Select + Start quits RetroBat) must not
  skip the track. There, only system actions run (no keys, no clicks): Start + LB sends no
  Alt+Left into a game.
- Volume left layer 2 on 20 Nov 2026 (the owner: no Start for volume); it is on the bumpers.
- Known limit: the tool does not hide the controller from games, so in a game the buttons of a
  layer-2 combo also reach the game: Menu opens its pause menu, and the game sees LT too. The owner
  moved layer 2 to Menu on 9 Nov 2026; this in-game side effect is flagged to them, not yet decided.
- The timing rules above say "Guide" from the first design; they apply to whichever button holds the
  layer.

## Controller menu (Start + R3)

The owner, 26 Nov 2026: in an app that reads the controller he wants to force the mouse from the
pad, but not through a bare shortcut ("I'm gonna forget it and hit it accidentally and then I'm
gonna think it's broken"). So a menu, which shows what is going on before anything changes.

- **Opens with Start + R3** (the `menu` action, layer 2's R3; settings version 5 adds it unless R3
  already has a job there). Everywhere, games included (after the 150 ms game delay), and while
  paused. Not while the session is locked; it closes at a lock.
- **A dark panel down the left edge** of the screen the app in front is on, the screen's full
  height, sized from that height (readable from the couch on a TV), Windows 11's dark colors and
  icon font (the owner, 26 Nov 2026, after a plain light first version: "make it dark mode ...
  don't make it so bare bones and ugly"). Topmost and never activated (as the on-screen keyboard),
  so the app keeps the foreground and its answer.
- It shows the app in front with its icon and why it gets the mouse or not (a green dot "Mouse
  on", an amber one "Left alone", with the reason), then:
  - **This app / Mouse in this app**: Automatic / Always / Excluded, picked with left and right,
    saved with A (the tray's same app rule; the owner chose a saved rule over "until the app
    closes");
  - **Pointer**: pointer speed, top speed (acceleration) and scroll speed, changed with left and
    right and saved 0.6 s after the last change (one write, not one per step), so the stick tries
    the new speed while the menu is still open;
  - **Controller Navigation**: pause / resume, turn off this controller (only for an XInput pad,
    the one that pressed last), what the buttons do (a page listing the sticks, the base map and
    each layer from the settings; B goes back, the D-pad scrolls when it does not fit), all
    settings (the settings window, brought in front).
  No mode row when nothing, or this tool's own window, is in front.
- While it is open it takes every new button press: D-pad up and down move (held, they repeat), A
  does the row, B or Start closes. Nothing reaches Windows from the pad, layers included, except
  that the sticks keep moving the pointer and scrolling where the app gets the mouse (no arrow or
  WASD keys from a stick). What was down when it opened (Start and R3)
  or when it closes (the A that picked) does nothing until released.
- Closes after A on a row that acts, B or Start, 15 s without a press, and when another window
  comes in front (not for a moment of no window at all). Pending speed changes are saved then.
- Known limits: the app in front still sees every press (Start may open a game's own menu); the
  panel does not show over exclusive-fullscreen games or over Start, Search and Task View, where
  it still takes the presses for up to 15 s.

## Configurability

Everything is set in the settings window (opened from the tray: a small native Windows window with
tabs, not a web page); nothing needs a file edit or a rebuild. The tables above are the built-in
defaults.

- **Bindings**: any control to any action, in the base map, in each layer and in each app override.
  - Actions: key or key combo (Win included), mouse button, scroll step, volume up/down, mute, media
    play/pause/next/previous, window move/maximize/minimize, minimize all, on-screen keyboard, run a
    program or open a file/URL, pause the tool, the controller menu, nothing.
  - Sticks: pointer, scroll, arrow keys, WASD, nothing.
  - Per binding: repeat on/off, and an optional long-press action (tap and hold do different things).
- **Layers**: each layer is a held button plus its own map. Default: one layer on Guide; more can be
  added, each on its own button.
- **Feel**: pointer, precise and scroll speeds; acceleration and its time; curve; deadzone per stick; trigger
  threshold; key repeat delay and rate; long-press time; the two-button (pair) window; volume step
  and repeat rate; the ~300 ms on-delay; the power-off hold time.
- **Apps**: the always/never app rules, each Always app's overrides and title rules; the detection
  switches and game folders.
- **Controllers**: on/off per pad, the mapping wizard, Switch-family driver choice.

Settings live in `%LOCALAPPDATA%\ControllerNavigation\settings.json`. No sync between PCs (the owner
relies on the built-in defaults).

## Tray icon

- Icon shows the state: grey (the app in front is left alone), blue (mouse active), paused; the
  tooltip says why ("reads the controller", "always (app rule)"...). No toasts, no rumble.
- Menu: **Automatic / Always / Never for the app that was in front** (the last window before the
  tray was clicked that is neither the shell nor this tool, since clicking the tray puts the taskbar
  in front), a "Running apps" submenu with the same three choices per app, **Pause / Resume**
  (everything off, layers included), Settings, Exit.
- Starts at sign-in.

## Install and updates: GitHub releases, no build

- A GitHub repo with a release workflow (HTPC's `.github/workflows/release.yml` as the model) publishes
  **one self-contained exe** (.NET included, nothing to install first) plus an `update.json` with its
  version and SHA-256. This folder is not a git repo yet.
- Running it on a new PC installs it: one admin prompt copies it to
  `C:\Program Files\ControllerNavigation`, registers the admin-window helper task, the update task and
  the sign-in start. Nothing in the code is specific to this PC or to one controller.
- **Auto-update** (HTPC's rules, `UpdateRules.cs`): a quiet check every ~20 h, retried after an hour
  when offline. It reads where `github.com/<repo>/releases/latest` redirects (no GitHub API, so no rate
  limit) and follows redirects only to the repo itself or `*.githubusercontent.com`. Stable releases
  only.
- The exe lives in Program Files, so a user process cannot replace it. The update is applied by an
  elevated scheduled task registered at install. It downloads the release exe, checks its SHA-256
  against `update.json`, swaps the exe and restarts the app. It waits until the pad has been idle for a
  few minutes, so it never restarts in the middle of a drag or a layer-2 hold.
- Settings: auto-update on/off (on by default), "Check now", the current version. The tray tooltip
  shows when an update was installed.
- Integrity rests on TLS plus the SHA-256 from the same release. A compromised GitHub account could
  ship a bad update; code signing would close that, but there is no certificate (same as HTPC).
- The settings window has a **Checks** page for what would fight the tool: JoyXoff running, Windows
  Game Bar's Guide shortcut on, Windows' own controller navigation on, and a reminder for Steam's
  Desktop Layout and Guide Button Chord Layout (see setup notes).

## Admin windows

Supported the HTPC way: Windows blocks injected input into elevated windows, so an elevated helper
(scheduled task, started on demand, named pipe locked to this exe) sends the input instead.

- The helper's trust check depends on the exe sitting in Program Files: a highest-privileges task
  pointing at a user-writable exe would be a UAC bypass.
- Layer-2 window actions on an elevated window may be refused by Windows (UIPI); if so they go
  through the helper too. To check while building.
- The UAC prompt and the sign-in screen (secure desktop) stay out of reach by design.

## Setup notes (this PC)

- Steam Input for Xbox pads is on here. Turn off Steam's **Desktop Layout** (it turns the pad into a
  mouse and keyboard outside Steam games, a second JoyXoff) and its **Guide Button Chord Layout**
  (it reacts to Guide + button in Steam Input games, overlapping layer 2). "Guide focuses Steam" is
  already off.
- Windows Game Bar's "open with the Guide button" is already off here.
- Uninstall or disable JoyXoff and its `JoyXoffSvc` service, or both tools inject at once.

## Known limits

- Games keep reading the pad while not in front. With Chrome on one screen and a running game on the
  other, both react to the pad. Only hiding the device from games (HidHide) would prevent it; out of
  scope.
- Buttons pressed with Guide reach a game in front (see layer 2).

## Built from HTPC

Lifted from `HTPC\launcher\src\Launcher` (copied, the two apps stay independent):

- `ControllerService.cs`: its polling loop, triggers as buttons, repeat and Start-chord logic, on top
  of SDL3 instead of XInput (XInput kept only for power off).
- `PadMapper.cs`: per-frame pointer (DwmFlush pacing), curve, scroll, key repeat, inherited buttons.
- `ButtonMap.cs`, `ButtonMapStore.cs`: map and JSON format, extended with layers, long press, per-
  binding repeat and the new actions; the launcher's `do:` commands replaced by this app's.
- `Input.cs`: SendInput with the tool's `dwExtraInfo` tag.
- `AudioOutputs.cs` (volume and mute part), `MediaWatcher.cs` (if it holds the GSMTC code).
- `ElevatedInput.cs`, `InputHelper.cs`, `InputFrame.cs`: the admin-window helper, with the parts of
  `SetupElevation`, `AsUser`, `Native` and `Rights` they need.
- `KeyboardForm.cs` and its `ui/keyboard.*`: the on-screen keyboard (WebView2).
- The self-install, release and update flow (`SetupElevation.cs`, `UpdateRules.cs`, the update
  service and `setup\lib\UpdateCore.ps1`'s checks, `release.yml`), cut down to this app.

Changes from HTPC: any controller through SDL3; multi-monitor pointer (HTPC clamps to the primary
screen) with per-monitor DPI; foreground read on the input thread with the on-delay; app rules,
game detection and title rules instead of launcher tiles; HTPC's Start + D-pad volume chord dropped
(layer 2, on Start, has volume).

Stack: .NET 10, WinForms tray app, SDL3 for input, WebView2 for the on-screen keyboard only (HTPC's,
kept as is by the owner's choice), log files in `%LOCALAPPDATA%\ControllerNavigation\logs`.
