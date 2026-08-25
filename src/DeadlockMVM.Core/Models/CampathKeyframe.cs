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

        return Interpolation == CampathInterpolationMode.Smooth
            ? EvaluateSmooth(left, amount)
            : CameraSample.Linear(from.Camera, to.Camera, ApplyEasing(amount, Easing));
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
        var x = _keyframes.Select(key => key.Camera.X).ToArray();
        var y = _keyframes.Select(key => key.Camera.Y).ToArray();
        var z = _keyframes.Select(key => key.Camera.Z).ToArray();
        var fov = _keyframes.Select(key => key.Camera.Fov).ToArray();
        var sample = new CameraSample(
            EvaluateSpline(left, amount, x),
            EvaluateSpline(left, amount, y),
            EvaluateSpline(left, amount, z),
            0,
            0,
            0,
            Math.Clamp(EvaluateSpline(left, amount, fov), fov.Min(), fov.Max()));
        return WithQuaternionAngles(sample, EvaluateRotationSpline(left, amount));
    }

    private double EvaluateSpline(int segment, double amount, IReadOnlyList<double> values)
    {
        var second = SplineSecondDerivatives(values);
        var span = Math.Max(_keyframes[segment + 1].DemoTick - _keyframes[segment].DemoTick, 1);
        var right = Math.Clamp(amount, 0, 1);
        var left = 1 - right;
        return (left * values[segment]) + (right * values[segment + 1]) +
               ((((left * left * left) - left) * second[segment]) +
                (((right * right * right) - right) * second[segment + 1])) *
               span * span / 6;
    }

    private double[] SplineSecondDerivatives(IReadOnlyList<double> values)
    {
        var count = _keyframes.Count;
        var second = new double[count];
        var upper = new double[count];
        var easeIn = Easing is CampathEasingMode.EaseIn or CampathEasingMode.EaseInOut;
        var easeOut = Easing is CampathEasingMode.EaseOut or CampathEasingMode.EaseInOut;
        var firstSpan = Math.Max(_keyframes[1].DemoTick - _keyframes[0].DemoTick, 1);
        if (easeIn)
        {
            second[0] = -0.5;
            upper[0] = (3d / firstSpan) * ((values[1] - values[0]) / firstSpan);
        }

        for (var index = 1; index + 1 < count; index++)
        {
            var previousSpan = Math.Max(_keyframes[index].DemoTick - _keyframes[index - 1].DemoTick, 1);
            var nextSpan = Math.Max(_keyframes[index + 1].DemoTick - _keyframes[index].DemoTick, 1);
            var sigma = (double)previousSpan / (previousSpan + nextSpan);
            var pivot = (sigma * second[index - 1]) + 2;
            second[index] = (sigma - 1) / pivot;
            var slopeDelta = ((values[index + 1] - values[index]) / nextSpan) -
                             ((values[index] - values[index - 1]) / previousSpan);
            upper[index] = ((6 * slopeDelta / (previousSpan + nextSpan)) -
                            (sigma * upper[index - 1])) / pivot;
        }

        var finalSecond = 0d;
        var finalUpper = 0d;
        if (easeOut)
        {
            var finalSpan = Math.Max(_keyframes[^1].DemoTick - _keyframes[^2].DemoTick, 1);
            finalSecond = 0.5;
            finalUpper = (-3d / finalSpan) * ((values[^1] - values[^2]) / finalSpan);
        }
        second[^1] = (finalUpper - (finalSecond * upper[^2])) /
                     ((finalSecond * second[^2]) + 1);
        for (var index = count - 2; index >= 0; index--)
            second[index] = (second[index] * second[index + 1]) + upper[index];
        return second;
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

    private Quaterniond EvaluateRotationSpline(int segment, double rawAmount)
    {
        var rotations = new Quaterniond[_keyframes.Count];
        for (var index = 0; index < rotations.Length; index++)
        {
            rotations[index] = Quaterniond.FromCamera(_keyframes[index].Camera);
            if (index > 0 && Quaterniond.Dot(rotations[index - 1], rotations[index]) < 0)
                rotations[index] = rotations[index] * -1;
        }
        var span = Math.Max(_keyframes[segment + 1].DemoTick - _keyframes[segment].DemoTick, 1);
        var fromTangent = RotationTangent(rotations, segment);
        var toTangent = RotationTangent(rotations, segment + 1);
        var firstControl = rotations[segment] * Quaterniond.Exp(fromTangent * (span / 3d));
        var secondControl = rotations[segment + 1] * Quaterniond.Exp(toTangent * (-span / 3d));
        var amount = Math.Clamp(rawAmount, 0, 1);
        var first = Quaterniond.Slerp(rotations[segment], firstControl, amount);
        var middle = Quaterniond.Slerp(firstControl, secondControl, amount);
        var last = Quaterniond.Slerp(secondControl, rotations[segment + 1], amount);
        return Quaterniond.Slerp(
            Quaterniond.Slerp(first, middle, amount),
            Quaterniond.Slerp(middle, last, amount),
            amount);
    }

    private Vector3d RotationTangent(IReadOnlyList<Quaterniond> rotations, int index)
    {
        if (index == 0 || index + 1 >= rotations.Count)
            return default;
        var previousSpan = Math.Max(_keyframes[index].DemoTick - _keyframes[index - 1].DemoTick, 1);
        var nextSpan = Math.Max(_keyframes[index + 1].DemoTick - _keyframes[index].DemoTick, 1);
        var previous = Quaterniond.Log(rotations[index].Inverse() * rotations[index - 1]) *
                       (-1d / previousSpan);
        var next = Quaterniond.Log(rotations[index].Inverse() * rotations[index + 1]) *
                   (1d / nextSpan);
        return ((previous * nextSpan) + (next * previousSpan)) / (previousSpan + nextSpan);
    }

    private static CameraSample WithQuaternionAngles(CameraSample sample, Quaterniond raw)
    {
        const double degrees = 180d / Math.PI;
        var value = raw.Normalized();
        var matrix11 = (2 * ((value.W * value.W) + (value.X * value.X))) - 1;
        var matrix12 = 2 * ((value.X * value.Y) + (value.W * value.Z));
        var matrix13 = 2 * ((value.X * value.Z) - (value.W * value.Y));
        var matrix23 = 2 * ((value.Y * value.Z) + (value.W * value.X));
        var matrix33 = (2 * ((value.W * value.W) + (value.Z * value.Z))) - 1;
        return sample with
        {
            Pitch = Math.Clamp(Math.Asin(Math.Clamp(-matrix13, -1, 1)) * degrees, -89, 89),
            Yaw = NormalizeAngle(Math.Atan2(matrix12, matrix11) * degrees),
            Roll = NormalizeAngle(Math.Atan2(matrix23, matrix33) * degrees),
        };
    }

    private readonly record struct Vector3d(double X, double Y, double Z)
    {
        public static Vector3d operator +(Vector3d left, Vector3d right) =>
            new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        public static Vector3d operator *(Vector3d value, double amount) =>
            new(value.X * amount, value.Y * amount, value.Z * amount);
        public static Vector3d operator /(Vector3d value, double amount) =>
            new(value.X / amount, value.Y / amount, value.Z / amount);
    }

    private readonly record struct Quaterniond(double W, double X, double Y, double Z)
    {
        public static Quaterniond FromCamera(CameraSample camera)
        {
            var halfRoll = camera.Roll * Math.PI / 360d;
            var halfPitch = camera.Pitch * Math.PI / 360d;
            var halfYaw = camera.Yaw * Math.PI / 360d;
            var sr = Math.Sin(halfRoll);
            var cr = Math.Cos(halfRoll);
            var sp = Math.Sin(halfPitch);
            var cp = Math.Cos(halfPitch);
            var sy = Math.Sin(halfYaw);
            var cy = Math.Cos(halfYaw);
            return new Quaterniond(
                (cr * cp * cy) + (sr * sp * sy),
                (sr * cp * cy) - (cr * sp * sy),
                (cr * sp * cy) + (sr * cp * sy),
                (cr * cp * sy) - (sr * sp * cy)).Normalized();
        }

        public Quaterniond Normalized()
        {
            var length = Math.Sqrt(Math.Max(Dot(this, this), 1e-24));
            return this * (1d / length);
        }

        public Quaterniond Inverse()
        {
            var lengthSquared = Math.Max(Dot(this, this), 1e-24);
            return new Quaterniond(W / lengthSquared, -X / lengthSquared,
                -Y / lengthSquared, -Z / lengthSquared);
        }

        public static double Dot(Quaterniond left, Quaterniond right) =>
            (left.W * right.W) + (left.X * right.X) +
            (left.Y * right.Y) + (left.Z * right.Z);

        public static Vector3d Log(Quaterniond raw)
        {
            var value = raw.Normalized();
            var vectorLength = Math.Sqrt((value.X * value.X) +
                                         (value.Y * value.Y) + (value.Z * value.Z));
            if (vectorLength < 1e-12)
                return default;
            var scale = Math.Atan2(vectorLength, value.W) / vectorLength;
            return new Vector3d(value.X * scale, value.Y * scale, value.Z * scale);
        }

        public static Quaterniond Exp(Vector3d value)
        {
            var angle = Math.Sqrt((value.X * value.X) +
                                  (value.Y * value.Y) + (value.Z * value.Z));
            if (angle < 1e-12)
                return new Quaterniond(1, value.X, value.Y, value.Z).Normalized();
            var scale = Math.Sin(angle) / angle;
            return new Quaterniond(Math.Cos(angle), value.X * scale,
                value.Y * scale, value.Z * scale);
        }

        public static Quaterniond Slerp(Quaterniond from, Quaterniond to, double rawAmount)
        {
            from = from.Normalized();
            to = to.Normalized();
            var cosine = Dot(from, to);
            if (cosine < 0)
            {
                to *= -1;
                cosine = -cosine;
            }
            var amount = Math.Clamp(rawAmount, 0, 1);
            if (cosine > 0.9995)
            {
                return new Quaterniond(
                    Lerp(from.W, to.W, amount), Lerp(from.X, to.X, amount),
                    Lerp(from.Y, to.Y, amount), Lerp(from.Z, to.Z, amount)).Normalized();
            }
            var angle = Math.Acos(Math.Clamp(cosine, -1, 1));
            var divisor = Math.Sin(angle);
            var left = Math.Sin((1 - amount) * angle) / divisor;
            var right = Math.Sin(amount * angle) / divisor;
            return ((from * left) + (to * right)).Normalized();
        }

        public static Quaterniond operator +(Quaterniond left, Quaterniond right) =>
            new(left.W + right.W, left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        public static Quaterniond operator *(Quaterniond value, double amount) =>
            new(value.W * amount, value.X * amount, value.Y * amount, value.Z * amount);
        public static Quaterniond operator *(Quaterniond left, Quaterniond right) => new(
            (left.W * right.W) - (left.X * right.X) - (left.Y * right.Y) - (left.Z * right.Z),
            (left.W * right.X) + (left.X * right.W) + (left.Y * right.Z) - (left.Z * right.Y),
            (left.W * right.Y) - (left.X * right.Z) + (left.Y * right.W) + (left.Z * right.X),
            (left.W * right.Z) + (left.X * right.Y) - (left.Y * right.X) + (left.Z * right.W));
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
