# Deadlock MVM

![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue.svg)

A cinematic camera and movie-making tool for local Deadlock replays.

## Get started

Build with `dotnet build DeadlockMVM.slnx -c Release`, then run `src/DeadlockMVM.Launcher/bin/Release/net8.0-windows/DeadlockMVM.Launcher.exe`. Select a local `.dem` replay.

Source builds need the SDK named in `global.json` and Visual Studio 2022 with C++. Requires Windows x64, Direct3D 11, and `-insecure`. Native hook compatibility depends on the installed game build.

## Features

- Free camera with movement speed, smoothing, FOV, and roll controls.
- Camera paths with keyframes, linear or smooth interpolation, undo/redo, and saved shots.
- Replay browsing, pause, seeking, tick stepping, and playback speed.
- HUD, health-bar, outline, and near-camera fade controls.

This snapshot has no native video capture or export pipeline. Camera paths do not provide editable tangents, layered paths, or a graph editor.

## Controls

**Tab** opens the menu. **F2** enters Free Camera. **F9** restores the game UI and input. [Full controls](CONTROLS.md).

[Third-party notices](THIRD_PARTY_NOTICES.md). Not affiliated with Valve.

No project-wide license was declared in this historical snapshot.
