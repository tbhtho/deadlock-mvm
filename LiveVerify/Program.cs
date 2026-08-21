using DeadlockMVM.Core.Models;
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

if (args.Length >= 2 && args[0].Equals("MATRIX", StringComparison.OrdinalIgnoreCase))
    return await RunMatrixAsync(args[1]);

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
        var psi = new ProcessStartInfo("powershell.exe", $"-ExecutionPolicy Bypass -File \"{captureScript}\" -Out \"{png}\"")
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
