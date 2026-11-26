# Controller Navigation

A .NET 10 WinForms tray app: any controller (SDL3) drives the desktop as a mouse and keyboard in every
app except games and apps that read the controller themselves (detected), so games are never touched.
Design and decisions: `docs/SPEC.md` (the agreed spec;
code wins where they differ, then update the spec).

## Verify

```bash
dotnet build src/ControllerNavigation
```

```bash
dotnet run -c Release --project tests/ControllerNavigation.Tests
```

The tests are a console harness (`tests/ControllerNavigation.Tests/Harness.cs`): every static class
named `*Tests` with `public static void Run()` is run; `-v` lists each check, any other argument
filters groups by name.

Parallel builds in one checkout (several agents): set `CN_BUILD_DIR` to a folder of your own first,
so bin and obj go there (`Directory.Build.props`).

## Layout

- `Engine.cs`: the mapping rules (who owns a press, layers, long press, repeat, inherited buttons) and
  the per-frame pointer thread. Pure enough to test with a fake `IOutput` (`EngineTests.cs`).
- `Pads.cs`: SDL3 on the input thread (the only thread that calls SDL), pad merge, virtual-pad
  detection.
- `Foreground.cs`: what is in front, app rules (always/never), title rules, the on-delay.
- `GameDetector.cs`: is an app with no rule a game? Its open handles to controller devices,
  Windows' game list, game folders.
- `TrayApp.cs`: wiring, the tray menu, `ISettingsHost`.
- `ActionRunner.cs`: system actions (volume, media, windows, run, power off) on an STA worker.
- `SettingsForm.cs`: the settings window, plain WinForms (the owner wants a native window, not a web UI).
- `OnScreenKeyboard.cs`, `WebUi.cs`, `ui/keyboard.*`: the on-screen keyboard, HTPC's WebView2 page,
  embedded in the exe.
- `ControllerMenu.cs`: the controller menu (Start + R3), a hand-drawn panel that never takes the
  focus; the engine hands it every press while it is open.
- `Installer.cs`, `Updater.cs`, `InputHelper.cs`, `ElevatedInput.cs`, `InputFrame.cs`: install to
  Program Files, the scheduled tasks, auto-update from GitHub releases, the elevated input helper.
- `Contracts.cs`: types shared across those parts. `Settings.cs`: the settings model and defaults.

Much of it is lifted from the owner's HTPC launcher (`..\HTPC\launcher\src\Launcher`); check there
before reinventing input, elevation or update code.

## Rules specific to this project

- Never let the app change a controller's state (SDL hints in `PadReader.StartSdl`): games read the
  same pad.
- Anything elevated (installer, update job, input helper) must refuse to run unless it is the exe in
  Program Files.
