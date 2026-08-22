using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Native;
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


sealed class ConsoleLog : DeadlockMVM.Core.Contracts.ILogService
{
    public string LogFilePath => "";
    public void Info(string message) => Console.WriteLine($"  log: {message}");
    public void Warn(string message) => Console.WriteLine($"  log(W): {message}");
    public void Error(string message) => Console.WriteLine($"  log(E): {message}");
}

