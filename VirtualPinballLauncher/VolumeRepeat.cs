namespace VirtualPinballLauncher;

/// <summary>
/// The timing of volume changes while a volume key is held: one step on the press,
/// a pause, then steady repeats that speed up the longer the key stays down until
/// they reach a ceiling. Kept free of UI so the schedule can be checked directly.
/// </summary>
public static class VolumeRepeat
{
    /// <summary>How long the key must stay down after the first step before repeating starts.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>How long, after repeating starts, the rate takes to climb from slow to fast.</summary>
    public static readonly TimeSpan RampDuration = TimeSpan.FromMilliseconds(1200);

    public const double SlowStepsPerSecond = 6;
    public const double FastStepsPerSecond = 100;

    /// <summary>
    /// The shortest wait between ticks. A UI timer cannot fire reliably more often than
    /// this, so rates above it are reached by taking several steps per tick.
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>The rate once repeating has begun, climbing linearly from slow to fast across the ramp and then holding.</summary>
    public static double StepsPerSecond(TimeSpan held)
    {
        var progress = Math.Clamp((held - InitialDelay) / RampDuration, 0.0, 1.0);
        return SlowStepsPerSecond + (FastStepsPerSecond - SlowStepsPerSecond) * progress;
    }

    /// <summary>The wait before the next tick, given how long the key has been held so far.</summary>
    public static TimeSpan Interval(TimeSpan held)
    {
        if (held < InitialDelay) return InitialDelay;
        var interval = TimeSpan.FromSeconds(1.0 / StepsPerSecond(held));
        return interval < MinInterval ? MinInterval : interval;
    }

    /// <summary>
    /// How many whole steps are due on a tick. The rate times the time since the last
    /// tick is added to <paramref name="owed"/>, and the whole steps are taken out of it,
    /// so fractional steps carry over rather than being lost at high rates.
    /// </summary>
    public static int StepsDue(TimeSpan held, TimeSpan sinceLastTick, ref double owed)
    {
        owed += StepsPerSecond(held) * sinceLastTick.TotalSeconds;
        var steps = (int)Math.Floor(owed);
        owed -= steps;
        return steps;
    }
}
