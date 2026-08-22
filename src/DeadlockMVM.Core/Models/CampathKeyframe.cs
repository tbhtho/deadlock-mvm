namespace DeadlockMVM.Core.Models;

/// <summary>One deterministic camera composition at a demo-timeline tick.</summary>
public sealed record CampathKeyframe(long DemoTick, CameraSample Camera)
{
    public bool IsValid => DemoTick is >= 0 and <= int.MaxValue && Camera.IsValid;
}

/// <summary>The initial milestone's deliberately small two-keyframe linear path.</summary>
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
