namespace DeadlockMVM.Core.Models;

/// <summary>One complete renderer-facing cinematic camera sample.</summary>
public readonly record struct CameraSample(
    double X,
    double Y,
    double Z,
    double Pitch,
    double Yaw,
    double Roll,
    double Fov)
{
    public const double MinFov = 5;
    public const double MaxFov = 170;
    public const double MaxWorldCoordinate = 200_000;

    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) &&
        double.IsFinite(Pitch) && double.IsFinite(Yaw) && double.IsFinite(Roll) &&
        double.IsFinite(Fov) &&
        Math.Abs(X) <= MaxWorldCoordinate && Math.Abs(Y) <= MaxWorldCoordinate &&
        Math.Abs(Z) <= MaxWorldCoordinate &&
        Pitch is >= -89 and <= 89 && Yaw is >= -360 and <= 360 &&
        Roll is >= -180 and <= 180 && Fov is >= MinFov and <= MaxFov;

    public static CameraSample Linear(CameraSample from, CameraSample to, double amount)
    {
        if (!from.IsValid || !to.IsValid || !double.IsFinite(amount))
            throw new ArgumentOutOfRangeException(nameof(amount));

        var t = Math.Clamp(amount, 0, 1);
        return new CameraSample(
            Lerp(from.X, to.X, t),
            Lerp(from.Y, to.Y, t),
            Lerp(from.Z, to.Z, t),
            Lerp(from.Pitch, to.Pitch, t),
            NormalizeAngle(from.Yaw + ShortestAngleDelta(from.Yaw, to.Yaw) * t),
            NormalizeAngle(from.Roll + ShortestAngleDelta(from.Roll, to.Roll) * t),
            Lerp(from.Fov, to.Fov, t));
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    private static double ShortestAngleDelta(double from, double to)
    {
        var delta = (to - from) % 360;
        if (delta > 180) delta -= 360;
        if (delta < -180) delta += 360;
        return delta;
    }

    private static double NormalizeAngle(double value)
    {
        value %= 360;
        if (value > 180) value -= 360;
        if (value < -180) value += 360;
        return value;
    }
}
