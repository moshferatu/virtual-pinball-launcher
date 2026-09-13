using VirtualPinballLauncher;

internal static class VolumeRepeatChecks
{
    public static void Run()
    {
        var slow = TimeSpan.FromSeconds(1.0 / VolumeRepeat.SlowStepsPerSecond);
        var rampEnd = VolumeRepeat.InitialDelay + VolumeRepeat.RampDuration;

        Check(VolumeRepeat.Interval(TimeSpan.Zero) == VolumeRepeat.InitialDelay,
            "The first repeat waits for the initial delay");
        Check(VolumeRepeat.Interval(VolumeRepeat.InitialDelay) == slow
            && VolumeRepeat.StepsPerSecond(VolumeRepeat.InitialDelay) == VolumeRepeat.SlowStepsPerSecond,
            "Repeating starts at the slow rate");
        Check(VolumeRepeat.StepsPerSecond(rampEnd) == VolumeRepeat.FastStepsPerSecond
            && VolumeRepeat.StepsPerSecond(TimeSpan.FromMinutes(1)) == VolumeRepeat.FastStepsPerSecond,
            "The ramp ends at the fast rate and never exceeds it");
        Check(VolumeRepeat.Interval(rampEnd) == VolumeRepeat.MinInterval,
            "Ticks never come faster than a UI timer can deliver");

        var owed = 0.0;
        var stepsOverOneSecond = 0;
        for (var i = 0; i < 40; i++)
            stepsOverOneSecond += VolumeRepeat.StepsDue(rampEnd, VolumeRepeat.MinInterval, ref owed);
        Check(stepsOverOneSecond == VolumeRepeat.FastStepsPerSecond,
            "At the ceiling, several steps per tick add up to the fast rate over a second");
        owed = 0;
        Check(VolumeRepeat.StepsDue(VolumeRepeat.InitialDelay, TimeSpan.FromMilliseconds(100), ref owed) == 0
            && VolumeRepeat.StepsDue(VolumeRepeat.InitialDelay, TimeSpan.FromMilliseconds(100), ref owed) == 1,
            "Fractional steps carry over between ticks instead of being lost");

        var previous = VolumeRepeat.Interval(TimeSpan.Zero);
        var monotonic = true;
        for (var held = TimeSpan.Zero; held < VolumeRepeat.InitialDelay + VolumeRepeat.RampDuration * 2; held += TimeSpan.FromMilliseconds(10))
        {
            var interval = VolumeRepeat.Interval(held);
            if (interval > previous) monotonic = false;
            previous = interval;
        }
        Check(monotonic, "Repeats never slow down while the key stays held");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }
}
