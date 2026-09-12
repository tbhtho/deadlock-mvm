# Deadlock MVM

![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue.svg)

A cinematic camera and movie-making tool for local Deadlock replays.

## Get started

Build a portable ZIP with `package-release.ps1`, extract it, and run `DeadlockMVM.Launcher.exe`. Select a local `.dem` replay.

Source builds need the SDK named in `global.json` and Visual Studio 2022 with C++. Requires Windows x64, Direct3D 11, and `-insecure`. Native hook compatibility depends on the installed game build.

## Features

- Free camera with movement speed, smoothing, FOV, and roll controls.
- Camera paths with keyframes, linear or smooth interpolation, undo/redo, and saved shots.
- Replay browsing, pause, seeking, tick stepping, and playback speed.
- HUD, health-bar, outline, and near-camera fade controls.
- Configurable keyboard and mouse bindings, a movable menu, and a separate effects panel.
- Built-in Fog, Color, Bloom, LUT, Sharpen, Vignette, and Grain controls; saved and imported look presets.
- Movie capture: World, Z-Depth, and Green Screen passes, TGA/PFM sequences, AVI output, and H.264 previews.

Audio and Green Screen are unreliable. Camera/UI recovery and character tint while pausing during slides have known issues. External ReShade and arbitrary `.fx` shaders are unsupported. Grading uses SDR; it cannot recover HDR highlights. Depth effects remain unavailable pending depth and projection calibration. Look edits lock during a take. Imported `.cube` LUTs support sizes 2–33, unit domains, and files up to 4 MiB.

## Controls

**Tab** opens the menu. **CapsLock** opens effects. **F9** restores the game UI and input. Change bindings in **Advanced > Open keyboard + mouse**. [Full controls](CONTROLS.md).

[Third-party notices](THIRD_PARTY_NOTICES.md). Not affiliated with Valve.

No project-wide license was declared in this historical snapshot.
