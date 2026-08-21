using DeadlockMVM.Core.Models;
using DeadlockMVM.Core.Services;

// LiveVerify — exercises the real ReplayCameraService code path against the
// running Deadlock replay (VConsole on 127.0.0.1:29000). Moves the roaming
// camera +100 on X and back, verifying the engine-reported landing position.

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
