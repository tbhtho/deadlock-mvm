using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native;
using DeadlockMVM.Core.Native.InProcess;
using DeadlockMVM.Core.Services;
using System.Diagnostics;

// LiveVerify — exercises the real ReplayCameraService code path against the
// running Deadlock replay (VConsole on 127.0.0.1:29000).
//
// Default mode: moves the roaming camera +100 on X and back, verifying the
// engine-reported landing position.
//
// MATRIX <outdir> mode: runs every spectator-mode transition through the real
// service (one editor action each) and captures a desktop screenshot after
// each step so the actual in-game result can be verified visually.
//
// RESOLVE <client.dll> mode: runs the native signature resolver against the
// on-disk client.dll (no game needed) and compares against the documented
// build RVAs in tools/research/native_backend.md.

if (args.Length >= 2 && args[0].Equals("MATRIX", StringComparison.OrdinalIgnoreCase))
    return await RunMatrixAsync(args[1]);

if (args.Length >= 2 && args[0].Equals("RESOLVE", StringComparison.OrdinalIgnoreCase))
    return RunResolve(args[1]);

if (args.Length >= 2 && args[0].Equals("NATIVE", StringComparison.OrdinalIgnoreCase))
    return await RunNativeAsync(args[1], args.Length >= 3 ? args[2] : "both");

if (args.Length >= 1 && args[0].Equals("ROTHOLD", StringComparison.OrdinalIgnoreCase))
    return await RunRotHoldAsync(args.Length >= 2 ? args[1] : null);

if (args.Length >= 2 && args[0].Equals("SHOT", StringComparison.OrdinalIgnoreCase))
    return await RunShotAsync(args[1]);

if (args.Length >= 2 && args[0].Equals("INPROCESS", StringComparison.OrdinalIgnoreCase))
    return await RunInProcessAsync(args[1]);


using var transport = new VConsoleTransport();
transport.Connect("127.0.0.1", 29000);
var camera = new ReplayCameraService(transport);

var initial = await camera.ReadStateAsync();
if (initial?.ActiveTransform is not { } start)
{
    Console.WriteLine("FAIL: no active transform (is the demo playing and roaming?)");
    return 1;
}

Console.WriteLine($"start:    {Fmt(start)}  height={initial.CameraHeight}");

var target = new CameraTransform(start.X + 100, start.Y, start.Z, start.Pitch, start.Yaw, start.Roll);
var moved = await camera.GoToPositionAsync(target.X, target.Y, target.Z);
Console.WriteLine($"moved to: {(moved?.ActiveTransform is { } m ? Fmt(m) : "null")}");

var back = await camera.GoToPositionAsync(start.X, start.Y, start.Z);
Console.WriteLine($"returned: {(back?.ActiveTransform is { } b ? Fmt(b) : "null")}");

var okOut = Close(moved?.ActiveTransform, target);
var okBack = Close(back?.ActiveTransform, start);
Console.WriteLine(okOut ? "PASS: outbound landing matches request" : "FAIL: outbound landing mismatch");
Console.WriteLine(okBack ? "PASS: return landing matches original" : "FAIL: return landing mismatch");
return okOut && okBack ? 0 : 1;

static string Fmt(CameraTransform t)
    => $"pos=({t.X:0.000}, {t.Y:0.000}, {t.Z:0.000}) ang=({t.Pitch:0.000}, {t.Yaw:0.000}, {t.Roll:0.000})";

static bool Close(CameraTransform? actual, CameraTransform expected)
    => actual is { } a
       && Math.Abs(a.X - expected.X) <= 1.0
       && Math.Abs(a.Y - expected.Y) <= 1.0
       && Math.Abs(a.Z - expected.Z) <= 1.0;

// Drives every mode transition once through the real service and screenshots
// the game after each step. Mode switches need the demo playing (paused demos
// defer them), so playback is resumed first.
static async Task<int> RunMatrixAsync(string outDir)
{
    Directory.CreateDirectory(outDir);
    var captureScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "capture.ps1"));

    using var transport = new VConsoleTransport();
    transport.Connect("127.0.0.1", 29000);
    var camera = new ReplayCameraService(transport);

    transport.SendCommand("demo_resume");

    var step = 0;
    async Task Step(string name, Func<Task> action)
    {
        step++;
        await action();
        Console.WriteLine($"step {step}: {name} -> tracked {camera.Selection.Mode} slot={camera.Selection.PlayerSlot?.ToString() ?? "—"}");
        // Let the engine apply and render the new mode before the screenshot.
        await Task.Delay(1200);
        var png = Path.Combine(outDir, $"matrix_{step:00}_{name}.png");
        var psi = new ProcessStartInfo("powershell.exe", $"-ExecutionPolicy Bypass -File \"{captureScript}\" -Out \"{png}\" -ProcessName deadlock")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
    }

    await Step("roam", () => camera.EnterFreeRoamAsync());
    await Step("ineye", () => { camera.SelectInEye(); return Task.CompletedTask; });
    await Step("chase", () => { camera.SelectChase(); return Task.CompletedTask; });
    await Step("ineye2", () => { camera.SelectInEye(); return Task.CompletedTask; });
    await Step("roam2", () => camera.EnterFreeRoamAsync());
    await Step("chase2", () => { camera.SelectChase(); return Task.CompletedTask; });
    await Step("roam3", () => camera.EnterFreeRoamAsync());
    await Step("next", () => { camera.SelectNextPlayer(); return Task.CompletedTask; });
    await Step("next2", () => { camera.SelectNextPlayer(); return Task.CompletedTask; });
    await Step("prev", () => { camera.SelectPrevPlayer(); return Task.CompletedTask; });

    Console.WriteLine($"matrix screenshots saved to {outDir}");
    return 0;
}

// Runs the signature resolver against an on-disk client.dll and checks the
// documented build RVAs (spec: tools/research/native_backend.md §3).
static int RunResolve(string clientDllPath)
{
    if (!File.Exists(clientDllPath))
    {
        Console.WriteLine($"FAIL: file not found: {clientDllPath}");
        return 1;
    }

    var sw = Stopwatch.StartNew();
    var image = PeImage.Load(File.ReadAllBytes(clientDllPath));
    var result = NativeCameraResolver.Resolve(image);
    sw.Stop();

    Console.WriteLine($"fingerprint: TDS=0x{image.Fingerprint.TimeDateStamp:X8} " +
                      $"checksum=0x{image.Fingerprint.CheckSum:X8} sizeImage=0x{image.Fingerprint.SizeOfImage:X} " +
                      $"fileLen={image.Fingerprint.FileLength}");
    foreach (var item in result.Items)
    {
        Console.WriteLine($"  {(item.Success ? "OK  " : "FAIL")} {item.Name,-26} hits={item.MatchCount} " +
                          $"rva=0x{item.Rva:X} {(item.Success ? "" : item.Error)}");
    }

    // Documented values for the analyzed build (54,467,224 bytes).
    const long ExpectedFovRefRva = 0x32B2DB8;
    const long ExpectedManagerRva = 0x32B13D0;
    const long ExpectedControllersRva = 0x37C6520;
    const long ExpectedEntitySystemRva = 0x30BCD70;
    var fovOk = result.SpectatorFovRefRva == ExpectedFovRefRva;
    var mgrOk = result.CameraManagerRva == ExpectedManagerRva;
    var ctrlOk = result.LocalControllersRva == ExpectedControllersRva;
    var entsOk = result.EntitySystemRva == ExpectedEntitySystemRva;
    Console.WriteLine($"fov ref:     0x{result.SpectatorFovRefRva:X} (documented 0x{ExpectedFovRefRva:X}) {(fovOk ? "MATCH" : "MISMATCH — build may have changed")}");
    Console.WriteLine($"manager:     0x{result.CameraManagerRva:X} (documented 0x{ExpectedManagerRva:X}) {(mgrOk ? "MATCH" : "MISMATCH")}");
    Console.WriteLine($"controllers: 0x{result.LocalControllersRva:X} (documented 0x{ExpectedControllersRva:X}) {(ctrlOk ? "MATCH" : "MISMATCH")}");
    Console.WriteLine($"entity sys:  0x{result.EntitySystemRva:X} (documented 0x{ExpectedEntitySystemRva:X}) {(entsOk ? "MATCH" : "MISMATCH")}");
    Console.WriteLine($"resolved in {sw.ElapsedMilliseconds} ms");
    return fovOk && mgrOk && ctrlOk && entsOk ? 0 : 1;
}

// Live-proves the native backend against the running game (demo must be playing):
// active FOV read + A/B/A write with screenshots, then rotation write verified via
// the engine's own getpos readback plus hold-stability while the demo plays.
static async Task<int> RunNativeAsync(string outDir, string mode = "both")
{
    Directory.CreateDirectory(outDir);
    var runFov = mode is "both" or "fov";
    var runRotation = mode is "both" or "rot";
    var captureScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "capture.ps1"));

    using var backend = new NativeCameraBackend(new ConsoleLog());
    backend.Refresh(replayActive: true);
    foreach (var line in backend.Status.Diagnostics)
        Console.WriteLine($"  diag: {line}");

    if (!backend.Attached)
    {
        Console.WriteLine("FAIL: native backend did not attach");
        return 1;
    }

    var status = backend.Status;
    Console.WriteLine($"attached: fovRead={status.CanReadActiveFov} fovWrite={status.CanWriteActiveFov} " +
                      $"rotationWrite={status.CanWriteRotation} fingerprint={status.Fingerprint}");

    async Task Capture(string name)
    {
        // Let the engine render the change before the screenshot.
        await Task.Delay(800);
        var png = Path.Combine(outDir, name);
        var psi = new ProcessStartInfo("powershell.exe", $"-ExecutionPolicy Bypass -File \"{captureScript}\" -Out \"{png}\" -ProcessName deadlock")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
    }

    var failures = 0;

    using var transport = new VConsoleTransport();
    transport.Connect("127.0.0.1", 29000);
    var camera = new ReplayCameraService(transport);
    if (runRotation)
    {
        // The rotation proof runs against the roaming camera (the mode MVM controls).
        // FOV-only runs keep whatever spectator mode the game is already in, so the
        // per-mode FOV matrix can be driven externally (roam/chase/in-eye).
        var roamState = await camera.EnterFreeRoamAsync();
        Console.WriteLine($"roam entry: {(roamState?.ActiveTransform is { } rs ? Fmt(rs) : "unavailable")}");
    }

    // ---- FOV A/B/A ----
    if (runFov)
    {
    var originalFov = backend.ReadActiveFov();
    if (originalFov is null)
    {
        Console.WriteLine("FAIL: could not read active FOV");
        return 1;
    }
    Console.WriteLine($"fov original: {originalFov:0.###}");

    foreach (var (target, name) in new[] { (40.0, "fov40.png"), (110.0, "fov110.png") })
    {
        if (!backend.TrySetActiveFov(target))
        {
            Console.WriteLine($"FAIL: TrySetActiveFov({target}) returned false");
            failures++;
            continue;
        }

        var readBack = backend.ReadActiveFov();
        Console.WriteLine($"fov set {target} -> readback {readBack:0.###}");
        if (readBack is null || Math.Abs(readBack.Value - target) > 0.5)
        {
            Console.WriteLine($"FAIL: FOV readback {readBack} does not match {target}");
            failures++;
        }

        await Capture(name);
    }

    backend.TrySetActiveFov(originalFov.Value);
    var restored = backend.ReadActiveFov();
    Console.WriteLine($"fov restored: {restored:0.###}");
    await Capture("fov_restored.png");
    if (restored is null || Math.Abs(restored.Value - originalFov.Value) > 0.5)
    {
        Console.WriteLine("FAIL: FOV did not restore");
        failures++;
    }
    }

    // ---- Rotation write + hold ----
    if (runRotation)
    {
    var before = (await camera.ReadStateAsync())?.ActiveTransform;
    if (before is not { } beforeT)
    {
        Console.WriteLine("FAIL: no transform via getpos (not roaming?)");
        return 1;
    }
    Console.WriteLine($"rot before: {Fmt(beforeT)}");

    const double targetPitch = 25.0;
    const double targetYaw = 200.0;
    if (!backend.TrySetCameraRotation(targetPitch, targetYaw, 0))
    {
        Console.WriteLine("FAIL: TrySetCameraRotation returned false");
        failures++;
    }
    else
    {
        await Task.Delay(400);
        var after = (await camera.ReadStateAsync())?.ActiveTransform;
        Console.WriteLine($"rot after set: {(after is { } a ? Fmt(a) : "null")}");

        // Hold check while the demo keeps playing.
        await Task.Delay(1500);
        var held = (await camera.ReadStateAsync())?.ActiveTransform;
        Console.WriteLine($"rot after 1.5s: {(held is { } h ? Fmt(h) : "null")}");

        bool Match(CameraTransform? t) =>
            t is { } x && Math.Abs(x.Pitch - targetPitch) < 1.0 && Math.Abs(NormalizeYawDelta(x.Yaw, targetYaw)) < 1.0;
        if (!Match(after))
        {
            Console.WriteLine("FAIL: getpos did not report the written rotation");
            failures++;
        }
        if (!Match(held))
        {
            Console.WriteLine("FAIL: rotation did not hold while the demo played");
            failures++;
        }

        await Capture("rotation.png");
        backend.TrySetCameraRotation(beforeT.Pitch, beforeT.Yaw, beforeT.Roll);
        await Task.Delay(300);
        var restoredRot = (await camera.ReadStateAsync())?.ActiveTransform;
        Console.WriteLine($"rot restored: {(restoredRot is { } r ? Fmt(r) : "null")}");
    }
    }

    Console.WriteLine(failures == 0 ? "NATIVE PASS" : $"NATIVE FAIL ({failures} failures)");
    return failures == 0 ? 0 : 1;
}

static double NormalizeYawDelta(double actual, double expected)
{
    var delta = (actual - expected) % 360.0;
    if (delta > 180.0) delta -= 360.0;
    if (delta < -180.0) delta += 360.0;
    return delta;
}
// Save/Restore end-to-end proof through the REAL composite service: save a shot,
// move+rotate+re-FOV away, restore, then verify the engine reports the exact
// original composition. Also checks rotation determinism while paused and at
// timescale 0.25 (the restore path must work in all replay states).
static async Task<int> RunShotAsync(string outDir)
{
    Directory.CreateDirectory(outDir);
    var captureScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "capture.ps1"));

    using var shotTransport = new VConsoleTransport();
    var controller = new ReplayController(shotTransport);
    var backend = new NativeCameraBackend(new ConsoleLog());
    var camera = new CompositeCameraService(new ReplayCameraService(shotTransport), backend, controller, new ConsoleLog());

    controller.Connect("127.0.0.1", 29000);

    // Wait for confirmed playback (no fixed sleeps: poll the controller state).
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
    while (controller.State.ReplayName is null && DateTime.UtcNow < deadline)
        await Task.Delay(250);
    if (controller.State.ReplayName is null)
    {
        Console.WriteLine("FAIL: no confirmed demo playback");
        return 1;
    }
    Console.WriteLine($"playback confirmed: {controller.State.ReplayName} tick {controller.State.CurrentTick}/{controller.State.TotalTicks}");

    await camera.EnterFreeRoamAsync();
    await Task.Delay(500);

    // Save in open air: glide landings are exact when unobstructed, and the
    // save/move/restore glides here must not clip world geometry.
    var preState = await camera.ReadStateAsync();
    if (preState?.ActiveTransform is { } pre)
        await camera.GoToPositionAsync(pre.X, pre.Y, pre.Z + 150);

    var failures = 0;
    var shot = await camera.SaveCameraAsync();
    if (shot is null)
    {
        Console.WriteLine("FAIL: SaveCameraAsync returned null");
        return 1;
    }
    Console.WriteLine($"saved: {Fmt(shot.Transform)} fov={shot.Fov:0.###}");

    async Task Capture(string name)
    {
        await Task.Delay(700);
        var psi = new ProcessStartInfo("powershell.exe", $"-ExecutionPolicy Bypass -File \"{captureScript}\" -Out \"{Path.Combine(outDir, name)}\" -ProcessName deadlock")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
    }

    // Move, rotate and re-FOV away from the shot (modest horizontal glide so the
    // restore glide has clear airspace and doesn't slide on geometry).
    await camera.GoToPositionAsync(shot.Transform.X + 80, shot.Transform.Y, shot.Transform.Z);
    await camera.SetCameraRotationAsync(-20, 45, 0);
    await camera.SetActiveFovAsync(60);
    await Task.Delay(600);
    var moved = (await camera.ReadStateAsync())?.ActiveTransform;
    var movedFov = (await camera.ReadStateAsync())?.ActiveFov;
    Console.WriteLine($"moved: {(moved is { } m ? Fmt(m) : "null")} fov={movedFov:0.###}");
    await Capture("shot_moved.png");

    var restored = await camera.RestoreCameraAsync();
    Console.WriteLine($"restore returned: {restored}");
    var after = await camera.ReadStateAsync();
    Console.WriteLine($"after restore: {(after?.ActiveTransform is { } a ? Fmt(a) : "null")} fov={after?.ActiveFov:0.###}");
    await Capture("shot_restored.png");

    bool Close(double x, double y, double tol) => Math.Abs(x - y) <= tol;
    if (!restored)
    {
        Console.WriteLine("FAIL: restore verification failed");
        failures++;
    }
    if (after?.ActiveTransform is not { } rt ||
        !Close(rt.X, shot.Transform.X, 1.5) || !Close(rt.Y, shot.Transform.Y, 1.5) || !Close(rt.Z, shot.Transform.Z, 2.5) ||
        !Close(rt.Pitch, shot.Transform.Pitch, 0.5) || !Close(NormalizeYawDelta(rt.Yaw, shot.Transform.Yaw), 0, 0.5) ||
        after.ActiveFov is not { } rf || !Close(rf, shot.Fov, 0.5))
    {
        Console.WriteLine("FAIL: restored state does not match the saved shot");
        failures++;
    }

    // Rotation determinism while PAUSED.
    shotTransport.SendCommand("demo_pause");
    await Task.Delay(600);
    await camera.SetCameraRotationAsync(10, 200, 0);
    await Task.Delay(700);
    var paused = (await camera.ReadStateAsync())?.ActiveTransform;
    var pausedOk = paused is { } p && Close(p.Pitch, 10, 1.0) && Close(NormalizeYawDelta(p.Yaw, 200), 0, 1.0);
    Console.WriteLine($"paused write: {(paused is { } p2 ? Fmt(p2) : "null")} -> {(pausedOk ? "OK" : "FAIL")}");
    if (!pausedOk) failures++;

    // And at slow motion.
    shotTransport.SendCommand("demo_resume");
    shotTransport.SendCommand("demo_timescale 0.25");
    await Task.Delay(800);
    await camera.SetCameraRotationAsync(5, 100, 0);
    await Task.Delay(900);
    var slow = (await camera.ReadStateAsync())?.ActiveTransform;
    var slowOk = slow is { } sl && Close(sl.Pitch, 5, 1.0) && Close(NormalizeYawDelta(sl.Yaw, 100), 0, 1.0);
    Console.WriteLine($"slow-mo write: {(slow is { } sl2 ? Fmt(sl2) : "null")} -> {(slowOk ? "OK" : "FAIL")}");
    if (!slowOk) failures++;
    shotTransport.SendCommand("demo_timescale 1");

    // Leave the camera where the shot was.
    await camera.SetCameraRotationAsync(shot.Transform.Pitch, shot.Transform.Yaw, shot.Transform.Roll);
    await camera.SetActiveFovAsync(shot.Fov);

    Console.WriteLine(failures == 0 ? "SHOT PASS" : $"SHOT FAIL ({failures} failures)");
    return failures == 0 ? 0 : 1;
}


static async Task<int> RunRotHoldAsync(string? outDir)
{
    using var backend = new NativeCameraBackend(new ConsoleLog());
    backend.Refresh(replayActive: true);
    if (!backend.Attached)
    {
        Console.WriteLine("FAIL: native backend did not attach");
        foreach (var line in backend.Status.Diagnostics)
            Console.WriteLine($"  diag: {line}");
        return 1;
    }

    using var probeTransport = new VConsoleTransport();
    probeTransport.Connect("127.0.0.1", 29000);
    var cam = new ReplayCameraService(probeTransport);
    await cam.EnterFreeRoamAsync();

    var start = (await cam.ReadStateAsync())?.ActiveTransform;
    if (start is not { } s)
    {
        Console.WriteLine("FAIL: no transform (not roaming?)");
        return 1;
    }
    Console.WriteLine($"start: {Fmt(s)}");
    backend.TryReadCameraU32(0xB0, out var h0);
    backend.TryReadCameraU32(0xB4, out var h1);
    Console.WriteLine($"cam tracked-target handles: +0xB0=0x{h0:X8} +0xB4=0x{h1:X8}");

    backend.TrySetCameraRotation(30, 220, 0);
    for (var i = 0; i < 32; i++)
    {
        await Task.Delay(250);
        var t = (await cam.ReadStateAsync())?.ActiveTransform;
        Console.WriteLine($"t+{(i + 1) * 250,5}ms: {(t is { } x ? $"ang=({x.Pitch:0.000}, {x.Yaw:0.000}, {x.Roll:0.000})" : "null")}");
    }

    backend.TryReadCameraU32(0xB0, out var h2);
    backend.TryReadCameraU32(0xB4, out var h3);
    Console.WriteLine($"cam tracked-target handles after: +0xB0=0x{h2:X8} +0xB4=0x{h3:X8}");
    backend.TrySetCameraRotation(s.Pitch, s.Yaw, s.Roll);
    Console.WriteLine("restored original rotation");
    return 0;
}

static async Task<int> RunInProcessAsync(string dllPath)
{
    var process = Process.GetProcessesByName("deadlock").FirstOrDefault()
                  ?? Process.GetProcessesByName("project8").FirstOrDefault();
    if (process is null)
    {
        Console.WriteLine("FAIL: Deadlock is not running");
        return 1;
    }

    var load = NativeReplayModuleLoader.LoadForReplay(process.Id, dllPath);
    Console.WriteLine($"loader: success={load.Success} already={load.AlreadyLoaded} {load.Message}");
    if (!load.Success)
        return 1;

    await using var native = new NativeReplayCameraClient();
    InProcessCameraStatus hello;
    try
    {
        hello = await native.ConnectAsync(process.Id, TimeSpan.FromSeconds(15));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: native IPC connection: {ex.Message}");
        return 1;
    }
    PrintNative("hello", hello);
    if (hello.State == InProcessBackendState.Failed)
    {
        Console.WriteLine($"FAIL: native backend initialization error {hello.Error}");
        await native.ShutdownAsync();
        return 1;
    }

    using var transport = new VConsoleTransport();
    var controller = new ReplayController(transport) { PollInterval = TimeSpan.FromMilliseconds(250) };
    var camera = new ReplayCameraService(transport);
    using var externalReadback = new NativeCameraBackend(new ConsoleLog());
    controller.Connect("127.0.0.1", 29000);

    var replayDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
    while (controller.State.ReplayName is null && DateTime.UtcNow < replayDeadline)
        await Task.Delay(100);
    if (controller.State.ReplayName is null)
    {
        Console.WriteLine("FAIL: replay playback was not confirmed");
        await native.ShutdownAsync();
        controller.Dispose();
        return 1;
    }

    await camera.EnterFreeRoamAsync();
    var stable = await WaitForStableTransformAsync(camera, TimeSpan.FromSeconds(8));
    if (stable is not { } start)
    {
        Console.WriteLine("FAIL: Free Roam did not stabilize");
        await native.ShutdownAsync();
        controller.Dispose();
        return 1;
    }
    Console.WriteLine($"free-roam baseline: {Fmt(start)} tick={controller.State.CurrentTick}");

    externalReadback.Refresh(replayActive: true);
    if (!externalReadback.Attached)
    {
        Console.WriteLine("FAIL: external authoritative FOV readback unavailable");
        await native.ShutdownAsync();
        controller.Dispose();
        return 1;
    }

    using var heartbeatStop = new CancellationTokenSource();
    var heartbeatTask = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            do
            {
                await native.HeartbeatAsync(
                    true,
                    true,
                    controller.State.CurrentTick ?? -1,
                    controller.GameTickOffset ?? -1,
                    heartbeatStop.Token);
            } while (await timer.WaitForNextTickAsync(heartbeatStop.Token));
        }
        catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"heartbeat stopped: {ex.Message}");
        }
    });

    var a = new CameraSample(start.X, start.Y, start.Z + 120, 5, 30, 0, 35);
    var b = new CameraSample(start.X + 200, start.Y - 120, start.Z + 200, -15, 120, 0, 70);
    var c = new CameraSample(start.X + 360, start.Y + 80, start.Z + 260, 12, 175, 0, 45);
    var d = new CameraSample(start.X + 140, start.Y + 260, start.Z + 180, -8, -120, 0, 80);
    var e = new CameraSample(start.X - 100, start.Y + 120, start.Z + 100, 3, -25, 0, 50);
    var failures = 0;

    try
    {
        // HLAE-style passive capture: observe exact native frames while replay
        // playback remains untouched. No pause/resume/seek/POV command occurs here.
        controller.SetSpeed(1);
        controller.Play();
        await WaitForPauseStateAsync(controller, false, TimeSpan.FromSeconds(5));
        var passiveFrames = new List<CampathKeyframe>();
        foreach (var label in new[] { "passive key 1", "passive key 2", "passive key 3" })
        {
            var before = await native.GetStatusAsync();
            var prepared = await native.PrepareCameraObservationAsync();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            var observed = prepared;
            while ((!observed.CameraObserved || observed.HookCalls <= before.HookCalls) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
                observed = await native.GetStatusAsync();
            }
            PrintNative(label, observed);
            if (!observed.CameraObserved || controller.State.IsPaused != false || controller.State.Timescale != 1)
            {
                Console.WriteLine("  FAIL: passive capture changed playback or returned no authoritative camera frame");
                failures++;
            }
            else
            {
                passiveFrames.Add(new CampathKeyframe(observed.ReplayTick, observed.Camera));
            }
            await Task.Delay(300);
        }
        var monotonic = passiveFrames.Count == 3 && passiveFrames.Zip(passiveFrames.Skip(1),
            (left, right) => left.DemoTick < right.DemoTick).All(value => value);
        Console.WriteLine($"passive playing workflow: keys={passiveFrames.Count} monotonicTicks={monotonic} pause={controller.State.IsPaused} speed={controller.State.Timescale}");
        if (!monotonic) failures++;

        controller.SetSpeed(0.25);
        var quarterBefore = await native.GetStatusAsync();
        await native.PrepareCameraObservationAsync();
        await Task.Delay(50);
        var quarterCapture = await native.GetStatusAsync();
        var quarterPreserved = quarterCapture.CameraObserved && controller.State.IsPaused == false && controller.State.Timescale == 0.25;
        Console.WriteLine($"passive 0.25x capture: tick {quarterBefore.ReplayTick}->{quarterCapture.ReplayTick} preserved={quarterPreserved}");
        if (!quarterPreserved) failures++;

        controller.Pause();
        await WaitForPauseStateAsync(controller, true, TimeSpan.FromSeconds(5));
        var pausedBefore = await native.GetStatusAsync();
        await native.PrepareCameraObservationAsync();
        await Task.Delay(50);
        var pausedCapture = await native.GetStatusAsync();
        var pausedPreserved = pausedCapture.CameraObserved && controller.State.IsPaused == true && controller.State.Timescale == 0.25 &&
                              Math.Abs(pausedCapture.ReplayTick - pausedBefore.ReplayTick) <= 1;
        Console.WriteLine($"passive paused capture: tick {pausedBefore.ReplayTick}->{pausedCapture.ReplayTick} preserved={pausedPreserved}");
        if (!pausedPreserved) failures++;
        controller.SetSpeed(1);

        // Foundation 1: exact paused A/B/A.
        controller.Pause();
        await WaitForPauseStateAsync(controller, true, TimeSpan.FromSeconds(5));
        await ApplyAndVerify("paused A", a, enable: true);
        await ApplyAndVerify("paused B", b);
        await ApplyAndVerify("paused A return", a);

        // Foundation 2: world advances for >=10 s while A stays exact.
        controller.Play();
        await WaitForPauseStateAsync(controller, false, TimeSpan.FromSeconds(5));
        await ApplyAndVerify("hold start A", a);
        var holdStartTick = controller.State.CurrentTick;
        var holdDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < holdDeadline)
        {
            await Task.Delay(500);
            if (!await VerifyCurrentAsync(a, 0.075, print: false))
                failures++;
        }
        var holdEndTick = controller.State.CurrentTick;
        var holdOk = holdStartTick is { } hs && holdEndTick is { } he && he > hs;
        Console.WriteLine($"10-second hold: tick {holdStartTick}->{holdEndTick}, camera={(failures == 0 ? "fixed" : "mismatch")}, worldAdvanced={holdOk}");
        if (!holdOk) failures++;

        // Foundation 3: explicit XYZ samples.
        foreach (var percent in new[] { 0, 25, 50, 75, 100 })
        {
            var sample = CameraSample.Linear(a, b, percent / 100.0) with
            {
                Pitch = a.Pitch,
                Yaw = a.Yaw,
                Roll = a.Roll,
                Fov = a.Fov,
            };
            await ApplyAndVerify($"XYZ {percent}%", sample);
        }

        // Foundation 4: full position/rotation/FOV sampling.
        foreach (var percent in new[] { 25, 50, 75 })
            await ApplyAndVerify($"FULL {percent}%", CameraSample.Linear(a, b, percent / 100.0));

        // Five-keyframe Campath: every segment evaluates from calibrated demo tick.
        controller.Pause();
        await WaitForPauseStateAsync(controller, true, TimeSpan.FromSeconds(5));
        var clock = await native.GetStatusAsync();
        var pathStart = clock.ReplayTick + 128;
        var path = new CampathPath(new[]
        {
            new CampathKeyframe(pathStart, a),
            new CampathKeyframe(pathStart + 128, b),
            new CampathKeyframe(pathStart + 320, c),
            new CampathKeyframe(pathStart + 544, d),
            new CampathKeyframe(pathStart + 800, e),
        });
        var configured = await native.SetCampathAsync(path);
        var enabledPath = await native.EnableOverrideAsync();
        PrintNative("Campath configured", enabledPath);
        if (!configured.Flags.HasFlag(InProcessStatusFlags.CampathActive) ||
            !enabledPath.Flags.HasFlag(InProcessStatusFlags.CampathActive))
        {
            Console.WriteLine("  FAIL: native Campath did not become active");
            failures++;
        }

        foreach (var offset in new long[] { 0, 64, 224, 432, 672, 800, 224 })
        {
            var requestedTick = pathStart + offset;
            controller.SeekToTick(checked((int)requestedTick));
            await WaitForNativeTickAsync(requestedTick, TimeSpan.FromSeconds(12));
            controller.Pause();
            await Task.Delay(250);
            await camera.EnterFreeRoamAsync();
            var status = await WaitForNativeTickStableAsync(TimeSpan.FromSeconds(4));
            var expected = path.Evaluate(status.ReplayTick);
            PrintNative($"LINEAR offset={offset} requestedTick={requestedTick}", status);
            if (Math.Abs(status.ReplayTick - requestedTick) > 2)
            {
                Console.WriteLine($"  FAIL: seek landed at native demo tick {status.ReplayTick}");
                failures++;
            }
            if (!await VerifyCurrentAsync(expected, 0.075, print: true))
                failures++;
        }

        // Pausing freezes both the replay clock and the evaluated camera at mid-path.
        var freezeTick = pathStart + 432;
        controller.SeekToTick(checked((int)freezeTick));
        await WaitForNativeTickAsync(freezeTick, TimeSpan.FromSeconds(12));
        controller.Pause();
        await Task.Delay(500);
        await camera.EnterFreeRoamAsync();
        await WaitForNativeTickStableAsync(TimeSpan.FromSeconds(4));
        await Task.Delay(1000);
        var freezeStart = await WaitForNativeTickStableAsync(TimeSpan.FromSeconds(4));
        await Task.Delay(1000);
        var freezeEnd = await native.GetStatusAsync();
        var freezeOk = freezeEnd.ReplayTick == freezeStart.ReplayTick &&
                       SampleEquals(freezeStart.Camera, freezeEnd.Camera, 0.0001) &&
                       await VerifyCurrentAsync(path.Evaluate(freezeEnd.ReplayTick), 0.075, print: false);
        Console.WriteLine($"Campath pause freeze: tick {freezeStart.ReplayTick}->{freezeEnd.ReplayTick} exact={freezeOk}");
        if (!freezeOk) failures++;

        // At quarter speed the same demo-tick function remains authoritative.
        controller.SeekToTick(checked((int)(pathStart + 224)));
        await WaitForNativeTickAsync(pathStart + 224, TimeSpan.FromSeconds(12));
        controller.Pause();
        await Task.Delay(250);
        await camera.EnterFreeRoamAsync();
        await WaitForNativeTickStableAsync(TimeSpan.FromSeconds(4));
        controller.SetSpeed(0.25);
        controller.Play();
        await WaitForPauseStateAsync(controller, false, TimeSpan.FromSeconds(4));
        var slowStart = await native.GetStatusAsync();
        await Task.Delay(1000);
        controller.Pause();
        var slowEnd = await WaitForNativeTickStableAsync(TimeSpan.FromSeconds(4));
        var slowExpected = path.Evaluate(slowEnd.ReplayTick);
        var slowOk = slowEnd.ReplayTick > slowStart.ReplayTick && SampleEquals(slowExpected, slowEnd.Camera, 0.0001);
        PrintNative("Campath 0.25x end", slowEnd);
        Console.WriteLine($"  expected authoritative {FmtSample(slowExpected)} fov={slowExpected.Fov:0.000}");
        Console.WriteLine($"  native authoritative   {FmtSample(slowEnd.Camera)} fov={slowEnd.Camera.Fov:0.000}");
        Console.WriteLine($"Campath 0.25x: tick {slowStart.ReplayTick}->{slowEnd.ReplayTick} exact={slowOk}");
        if (!slowOk) failures++;
        controller.SetSpeed(1);

        // Centripetal smooth position plus shortest unwrapped rotation and FOV.
        controller.Pause();
        await WaitForPauseStateAsync(controller, true, TimeSpan.FromSeconds(5));
        var smoothPath = new CampathPath(path.Keyframes, CampathInterpolationMode.Smooth, CampathEasingMode.Linear);
        configured = await native.SetCampathAsync(smoothPath);
        if (!configured.Flags.HasFlag(InProcessStatusFlags.CampathActive))
        {
            Console.WriteLine("FAIL: smooth Campath was rejected");
            failures++;
        }
        foreach (var offset in new long[] { 0, 64, 128, 224, 320, 432, 544, 672, 800 })
        {
            var requestedTick = pathStart + offset;
            controller.SeekToTick(checked((int)requestedTick));
            await WaitForNativeTickAsync(requestedTick, TimeSpan.FromSeconds(12));
            controller.Pause();
            await Task.Delay(250);
            await camera.EnterFreeRoamAsync();
            var status = await WaitForNativeTickStableAsync(TimeSpan.FromSeconds(4));
            var expected = smoothPath.Evaluate(status.ReplayTick);
            var smoothOk = SampleEquals(expected, status.Camera, 0.001) &&
                           await VerifyCurrentAsync(expected, 0.075, print: false);
            PrintNative($"SMOOTH offset={offset}", status);
            Console.WriteLine($"  managed/native/render exact={smoothOk}");
            if (!smoothOk) failures++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: live foundation threw: {ex}");
        failures++;
    }
    finally
    {
        try { await native.DisableOverrideAsync(); } catch { }
        heartbeatStop.Cancel();
        await heartbeatTask;
        try { await native.ShutdownAsync(); } catch { }
        controller.Play();
        controller.Dispose();
    }

    Console.WriteLine(failures == 0 ? "INPROCESS FOUNDATION PASS" : $"INPROCESS FOUNDATION FAIL ({failures})");
    return failures == 0 ? 0 : 1;

    async Task ApplyAndVerify(string label, CameraSample sample, bool enable = false)
    {
        var accepted = await native.SetCameraSampleAsync(sample);
        if (enable)
            accepted = await native.EnableOverrideAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        InProcessCameraStatus status;
        do
        {
            await Task.Delay(10);
            status = await native.GetStatusAsync();
        } while ((!status.OverrideActive || status.AppliedSequence < accepted.AcceptedSequence) && DateTime.UtcNow < deadline);

        PrintNative(label, status);
        if (!status.OverrideActive || status.Error != InProcessErrorCode.None)
        {
            Console.WriteLine($"  FAIL: override not active ({status.Error})");
            failures++;
            return;
        }
        if (!await VerifyCurrentAsync(sample, 0.075, print: true))
            failures++;
    }

    async Task<bool> VerifyCurrentAsync(CameraSample expected, double tolerance, bool print)
    {
        await Task.Delay(35);
        var transform = (await camera.ReadTransformAsync())?.ActiveTransform;
        var fov = externalReadback.ReadActiveFov();
        var ok = transform is { } actual && fov is { } actualFov &&
                 Math.Abs(actual.X - expected.X) <= tolerance &&
                 Math.Abs(actual.Y - expected.Y) <= tolerance &&
                 Math.Abs(actual.Z - expected.Z) <= tolerance &&
                 Math.Abs(actual.Pitch - expected.Pitch) <= tolerance &&
                 Math.Abs(NormalizeYawDelta(actual.Yaw, expected.Yaw)) <= tolerance &&
                 Math.Abs(actualFov - expected.Fov) <= tolerance;
        if (print || !ok)
        {
            Console.WriteLine($"  expected pos=({expected.X:0.000},{expected.Y:0.000},{expected.Z:0.000}) " +
                              $"ang=({expected.Pitch:0.000},{expected.Yaw:0.000},{expected.Roll:0.000}) fov={expected.Fov:0.000}");
            Console.WriteLine($"  actual   {(transform is { } value ? Fmt(value) : "null")} fov={fov:0.000} -> {(ok ? "EXACT" : "FAIL")}");
        }
        return ok;
    }

    async Task<InProcessCameraStatus> WaitForNativeTickAsync(long target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        InProcessCameraStatus status;
        do
        {
            await Task.Delay(15);
            status = await native.GetStatusAsync();
            if (Math.Abs(status.ReplayTick - target) <= 2)
                return status;
        } while (DateTime.UtcNow < deadline);
        return status;
    }

    async Task<InProcessCameraStatus> WaitForNativeTickStableAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var previous = await native.GetStatusAsync();
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            var current = await native.GetStatusAsync();
            if (current.ReplayTick == previous.ReplayTick && current.OverrideActive)
            {
                if (++stable >= 3)
                    return current;
            }
            else
            {
                stable = 0;
            }
            previous = current;
        }
        return previous;
    }

    static bool SampleEquals(CameraSample expected, CameraSample actual, double tolerance) =>
        Math.Abs(actual.X - expected.X) <= tolerance &&
        Math.Abs(actual.Y - expected.Y) <= tolerance &&
        Math.Abs(actual.Z - expected.Z) <= tolerance &&
        Math.Abs(actual.Pitch - expected.Pitch) <= tolerance &&
        Math.Abs(NormalizeYawDelta(actual.Yaw, expected.Yaw)) <= tolerance &&
        Math.Abs(actual.Roll - expected.Roll) <= tolerance &&
        Math.Abs(actual.Fov - expected.Fov) <= tolerance;

    static string FmtSample(CameraSample sample) =>
        $"pos=({sample.X:0.000}, {sample.Y:0.000}, {sample.Z:0.000}) " +
        $"ang=({sample.Pitch:0.000}, {sample.Yaw:0.000}, {sample.Roll:0.000})";
}

static void PrintNative(string label, InProcessCameraStatus status) =>
    Console.WriteLine($"{label}: state={status.State} error={status.Error} flags={status.Flags} " +
                      $"accepted={status.AcceptedSequence} applied={status.AppliedSequence} hooks={status.HookCalls} " +
                      $"nativeTick={status.ReplayTick}");

static async Task<CameraTransform?> WaitForStableTransformAsync(ReplayCameraService camera, TimeSpan timeout)
{
    CameraTransform? previous = null;
    var stable = 0;
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(150);
        var current = (await camera.ReadTransformAsync())?.ActiveTransform;
        if (current is null)
            continue;
        if (previous is { } prior && Math.Abs(prior.X - current.X) < 0.01 &&
            Math.Abs(prior.Y - current.Y) < 0.01 && Math.Abs(prior.Z - current.Z) < 0.01)
        {
            if (++stable >= 4)
                return current;
        }
        else
        {
            stable = 0;
        }
        previous = current;
    }
    return previous;
}

static async Task<bool> WaitForPauseStateAsync(ReplayController controller, bool paused, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (controller.State.IsPaused == paused)
            return true;
        await Task.Delay(100);
    }
    return false;
}


sealed class ConsoleLog : DeadlockMVM.Core.Contracts.ILogService
{
    public string LogFilePath => "";
    public void Info(string message) => Console.WriteLine($"  log: {message}");
    public void Warn(string message) => Console.WriteLine($"  log(W): {message}");
    public void Error(string message) => Console.WriteLine($"  log(E): {message}");
}
