namespace DeadlockMVM.Core.Models;

/// <summary>One deterministic camera composition at a demo-timeline tick.</summary>
public sealed record CampathKeyframe(long DemoTick, CameraSample Camera)
{
    public bool IsValid => DemoTick is >= 0 and <= int.MaxValue && Camera.IsValid;
}

public enum CampathInterpolationMode : uint
{
    Linear = 0,
    Smooth = 1,
}

public enum CampathEasingMode : uint
{
    Linear = 0,
    EaseIn = 1,
    EaseOut = 2,
    EaseInOut = 3,
}

/// <summary>A bounded, sorted, replay-tick-authoritative cinematic camera path.</summary>
public sealed class CampathPath
{
    public const int MaxKeyframes = 128;
    private readonly IReadOnlyList<CampathKeyframe> _keyframes;

    public CampathPath(
        IEnumerable<CampathKeyframe> keyframes,
        CampathInterpolationMode interpolation = CampathInterpolationMode.Linear,
        CampathEasingMode easing = CampathEasingMode.Linear)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        _keyframes = keyframes.OrderBy(keyframe => keyframe.DemoTick).ToArray();
        Interpolation = interpolation;
        Easing = easing;
    }

    public IReadOnlyList<CampathKeyframe> Keyframes => _keyframes;
    public CampathInterpolationMode Interpolation { get; }
    public CampathEasingMode Easing { get; }

    public bool IsValid =>
        _keyframes.Count is >= 2 and <= MaxKeyframes &&
        Enum.IsDefined(Interpolation) && Enum.IsDefined(Easing) &&
        _keyframes.All(keyframe => keyframe.IsValid) &&
        _keyframes.Zip(_keyframes.Skip(1), (left, right) => left.DemoTick < right.DemoTick).All(value => value);

    public CameraSample Evaluate(long demoTick)
    {
        if (!IsValid)
            throw new InvalidOperationException("Campath requires 2-128 valid keyframes at unique increasing demo ticks.");

        if (demoTick <= _keyframes[0].DemoTick)
            return _keyframes[0].Camera;
        if (demoTick >= _keyframes[^1].DemoTick)
            return _keyframes[^1].Camera;

        var right = FindRightKeyframe(demoTick);
        var left = right - 1;
        var from = _keyframes[left];
        var to = _keyframes[right];
        var amount = (double)(demoTick - from.DemoTick) / (to.DemoTick - from.DemoTick);
        amount = ApplyEasing(amount, Easing);

        return Interpolation == CampathInterpolationMode.Smooth
            ? EvaluateSmooth(left, amount)
            : CameraSample.Linear(from.Camera, to.Camera, amount);
    }

    public int FindSegment(long demoTick)
    {
        if (!IsValid)
            throw new InvalidOperationException("Campath is invalid.");
        if (demoTick <= _keyframes[0].DemoTick)
            return 0;
        if (demoTick >= _keyframes[^1].DemoTick)
            return _keyframes.Count - 2;
        return FindRightKeyframe(demoTick) - 1;
    }

    private int FindRightKeyframe(long tick)
    {
        var low = 1;
        var high = _keyframes.Count - 1;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_keyframes[middle].DemoTick < tick)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private CameraSample EvaluateSmooth(int left, double amount)
    {
        var p1 = _keyframes[left].Camera;
        var p2 = _keyframes[left + 1].Camera;
        var p0 = left > 0 ? _keyframes[left - 1].Camera : Reflect(p1, p2);
        var p3 = left + 2 < _keyframes.Count ? _keyframes[left + 2].Camera : Reflect(p2, p1);

        var points = new[]
        {
            new Vector3d(p0.X, p0.Y, p0.Z),
            new Vector3d(p1.X, p1.Y, p1.Z),
            new Vector3d(p2.X, p2.Y, p2.Z),
            new Vector3d(p3.X, p3.Y, p3.Z),
        };
        var knots = CreateCentripetalKnots(points);
        var position = Interpolate(points, knots, amount);
        var pitchValues = UnwrapAngles(p0.Pitch, p1.Pitch, p2.Pitch, p3.Pitch);
        var yawValues = UnwrapAngles(p0.Yaw, p1.Yaw, p2.Yaw, p3.Yaw);
        var rollValues = UnwrapAngles(p0.Roll, p1.Roll, p2.Roll, p3.Roll);
        var fovValues = new[] { p0.Fov, p1.Fov, p2.Fov, p3.Fov };

        return new CameraSample(
            position.X,
            position.Y,
            position.Z,
            Math.Clamp(InterpolateScalar(pitchValues, knots, amount), -89d, 89d),
            NormalizeAngle(InterpolateScalar(yawValues, knots, amount)),
            NormalizeAngle(InterpolateScalar(rollValues, knots, amount)),
            Math.Clamp(InterpolateScalar(fovValues, knots, amount), fovValues.Min(), fovValues.Max()));
    }

    private static CameraSample Reflect(CameraSample origin, CameraSample other) => new(
        (2 * origin.X) - other.X,
        (2 * origin.Y) - other.Y,
        (2 * origin.Z) - other.Z,
        origin.Pitch - ShortestAngleDelta(origin.Pitch, other.Pitch),
        origin.Yaw - ShortestAngleDelta(origin.Yaw, other.Yaw),
        origin.Roll - ShortestAngleDelta(origin.Roll, other.Roll),
        Math.Clamp((2 * origin.Fov) - other.Fov, CameraSample.MinFov, CameraSample.MaxFov));

    private static double[] CreateCentripetalKnots(IReadOnlyList<Vector3d> points)
    {
        var knots = new double[4];
        for (var index = 1; index < knots.Length; index++)
        {
            var distance = points[index].DistanceTo(points[index - 1]);
            knots[index] = knots[index - 1] + Math.Max(Math.Sqrt(distance), 1e-6);
        }
        return knots;
    }

    private static Vector3d Interpolate(IReadOnlyList<Vector3d> values, IReadOnlyList<double> knots, double amount)
    {
        var parameter = Lerp(knots[1], knots[2], amount);
        var a1 = Blend(values[0], values[1], knots[0], knots[1], parameter);
        var a2 = Blend(values[1], values[2], knots[1], knots[2], parameter);
        var a3 = Blend(values[2], values[3], knots[2], knots[3], parameter);
        var b1 = Blend(a1, a2, knots[0], knots[2], parameter);
        var b2 = Blend(a2, a3, knots[1], knots[3], parameter);
        return Blend(b1, b2, knots[1], knots[2], parameter);
    }

    private static double InterpolateScalar(IReadOnlyList<double> values, IReadOnlyList<double> knots, double amount)
    {
        var parameter = Lerp(knots[1], knots[2], amount);
        var a1 = Blend(values[0], values[1], knots[0], knots[1], parameter);
        var a2 = Blend(values[1], values[2], knots[1], knots[2], parameter);
        var a3 = Blend(values[2], values[3], knots[2], knots[3], parameter);
        var b1 = Blend(a1, a2, knots[0], knots[2], parameter);
        var b2 = Blend(a2, a3, knots[1], knots[3], parameter);
        return Blend(b1, b2, knots[1], knots[2], parameter);
    }

    private static Vector3d Blend(Vector3d left, Vector3d right, double leftTime, double rightTime, double time)
    {
        var amount = (time - leftTime) / Math.Max(rightTime - leftTime, 1e-9);
        return left + ((right - left) * amount);
    }

    private static double Blend(double left, double right, double leftTime, double rightTime, double time)
    {
        var amount = (time - leftTime) / Math.Max(rightTime - leftTime, 1e-9);
        return Lerp(left, right, amount);
    }

    private static double[] UnwrapAngles(double p0, double p1, double p2, double p3)
    {
        var one = NormalizeAngle(p1);
        var zero = one - ShortestAngleDelta(p0, one);
        var two = one + ShortestAngleDelta(one, p2);
        var three = two + ShortestAngleDelta(two, p3);
        return new[] { zero, one, two, three };
    }

    private static double ApplyEasing(double value, CampathEasingMode easing)
    {
        var t = Math.Clamp(value, 0d, 1d);
        return easing switch
        {
            CampathEasingMode.EaseIn => t * t,
            CampathEasingMode.EaseOut => 1d - ((1d - t) * (1d - t)),
            CampathEasingMode.EaseInOut => t * t * (3d - (2d * t)),
            _ => t,
        };
    }

    private static double ShortestAngleDelta(double from, double to)
    {
        var delta = (to - from) % 360d;
        if (delta > 180d) delta -= 360d;
        if (delta < -180d) delta += 360d;
        return delta;
    }

    private static double NormalizeAngle(double value)
    {
        value %= 360d;
        if (value > 180d) value -= 360d;
        if (value < -180d) value += 360d;
        return value;
    }

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    private readonly record struct Vector3d(double X, double Y, double Z)
    {
        public double DistanceTo(Vector3d other)
        {
            var dx = X - other.X;
            var dy = Y - other.Y;
            var dz = Z - other.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        public static Vector3d operator +(Vector3d left, Vector3d right) =>
            new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        public static Vector3d operator -(Vector3d left, Vector3d right) =>
            new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        public static Vector3d operator *(Vector3d value, double amount) =>
            new(value.X * amount, value.Y * amount, value.Z * amount);
    }
}

/// <summary>Compatibility wrapper retained for the original two-point API.</summary>
public sealed record LinearCampath(CampathKeyframe From, CampathKeyframe To)
{
    public bool IsValid => From.IsValid && To.IsValid && To.DemoTick > From.DemoTick;

    public CameraSample Evaluate(long demoTick)
    {
        if (!IsValid)
            throw new InvalidOperationException("Campath requires two valid keyframes at increasing demo ticks.");
        var amount = (double)(demoTick - From.DemoTick) / (To.DemoTick - From.DemoTick);
        return CameraSample.Linear(From.Camera, To.Camera, amount);
    }
}
