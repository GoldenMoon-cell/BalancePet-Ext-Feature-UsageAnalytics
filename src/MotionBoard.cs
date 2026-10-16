using System.Windows.Media;

namespace BalancePet.UsageAnalytics;

/// <summary>
/// Drives motion off the compositor's own frame callback, one property per lane.
///
/// This is the prototype's own board (<c>experiments/usage-ui-mockup/MotionBoard.cs</c>),
/// kept because of what it buys rather than for its own sake: a lane that is a
/// plain function of elapsed time can be <see cref="Seek"/>ed, so the frame a
/// screenshot claims to be is the frame it is. An authored
/// <see cref="System.Windows.Media.Animation.DoubleAnimation"/> cannot say that —
/// its progress is only observable while the clock runs — and the prototype
/// measured WPF's storyboard seek landing one tick late, which photographs the
/// first frame while the filename names another.
///
/// The easing curves are the design's: <c>CubicEase EaseOut</c> for anything
/// that is a layout change, and <c>BackEase EaseOut</c> for the small
/// settle-past-and-return on the opening row.
/// </summary>
public sealed class MotionBoard
{
    private readonly List<Lane> _lanes = [];
    private long _started = -1;
    private bool _running;

    /// <summary>The design's easing: <c>CubicEase EaseOut</c>.</summary>
    public static readonly Func<double, double> CubicOut = progress =>
        1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);

    /// <summary>
    /// <c>BackEase EaseOut</c>: overshoots past its target and returns.
    ///
    /// The amplitude is multiplied by 1.5 because that is the scaling WPF's own
    /// <see cref="System.Windows.Media.Animation.BackEase"/> applies. The design's
    /// measured curve is the prototype's: it used amplitude 0.25 with that scaling,
    /// and its probe read a peak of 1.080 — which is what this returns, and what
    /// <see cref="Overshoot"/> predicts.
    /// </summary>
    public static Func<double, double> BackOut(double amplitude = 0.25)
    {
        var a = amplitude * 1.5;
        return progress =>
        {
            var t = Math.Clamp(progress, 0, 1) - 1;
            return t * t * ((a + 1) * t + a) + 1;
        };
    }

    /// <summary>
    /// <c>PowerEase EaseInOut</c>, the design's curve for the interface chrome:
    /// the account rows, the client filter chips and the quota card's fold.
    ///
    /// The closed form rather than an instance of WPF's
    /// <see cref="System.Windows.Media.Animation.PowerEase"/> so that these
    /// transitions can be seeked like every other lane on this board — the design
    /// asks for "PowerEase(EaseInOut, Power=2), 250 ms", and a lane driven by this
    /// function is that same curve, not a second curve with the same name.
    /// WPF's own implementation is <c>0.5·(2t)^p</c> up to the midpoint and
    /// <c>1 − 0.5·(2−2t)^p</c> after it, which is what this reproduces.
    /// </summary>
    public static Func<double, double> PowerInOut(double power = 2) => progress =>
    {
        var t = Math.Clamp(progress, 0, 1);
        return t < 0.5
            ? 0.5 * Math.Pow(2 * t, power)
            : 1 - 0.5 * Math.Pow(2 - 2 * t, power);
    };

    /// <summary>
    /// How far past its target this curve reaches, as a fraction of its span.
    ///
    /// With <c>u = p - 1</c> the curve is <c>B(u) = u³ + a·u² + 1</c>, so
    /// <c>B'(u) = u(3u + 2a)</c> is zero at <c>u = 0</c> and at <c>u = -2a/3</c>,
    /// and the second one is the peak. Only a peak inside the sweep counts: at or
    /// before <c>u = -1</c> the function is still rising when the sweep starts, and
    /// the largest value it reaches is its endpoint — 1, no overshoot at all.
    ///
    /// The first version of this ported the prototype's closed form without
    /// checking it against the curve it describes, and reported 50% for a curve
    /// that peaks at 8%: at that amplitude the peak it solved for sits exactly on
    /// the start of the sweep, where the formula is not the maximum. The value it
    /// should return is <c>4a²/27</c>, which this computes.
    /// </summary>
    public static double Overshoot(double amplitude = 0.25)
    {
        var a = amplitude * 1.5;
        // The peak sits at u = -2a/3, and the sweep is u in -1..0: 2a/3 must not
        // reach past 2, or the curve is still rising at the end and 1 is its maximum.
        if (2 * a / 3 > 2) return 0;
        return 4 * a * a / 27;
    }

    /// <summary>One animated property: where it starts, where it ends, when, how.</summary>
    private sealed class Lane
    {
        public required string Name { get; init; }
        public required Action<double> Write { get; init; }
        public required double From { get; init; }
        public required double To { get; init; }
        public required double BeginMs { get; init; }
        public required double DurationMs { get; init; }
        public required Func<double, double> Ease { get; init; }

        /// <summary>The raw progress through this lane, before easing, 0..1.</summary>
        public double Linear(double elapsed)
            => elapsed <= BeginMs ? 0 : Math.Clamp((elapsed - BeginMs) / DurationMs, 0, 1);

        /// <summary>Where the lane is at an instant, easing included.</summary>
        public double Value(double elapsed) => From + (To - From) * Ease(Linear(elapsed));
    }

    /// <summary>True when nothing has been added, so a plain frame is a still frame.</summary>
    public bool IsEmpty => _lanes.Count == 0;

    public void Add(string name, Action<double> write, double from, double to, double beginMs, double durationMs,
        Func<double, double>? ease = null)
        => _lanes.Add(new Lane
        {
            Name = name,
            Write = write,
            From = from,
            To = to,
            BeginMs = beginMs,
            DurationMs = Math.Max(1, durationMs),
            Ease = ease ?? CubicOut,
        });

    /// <summary>Starts every lane from zero and keeps writing frames until it ends.</summary>
    public void Start()
    {
        _started = Environment.TickCount64;
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Freezes every lane at one instant, for a reproducible capture.</summary>
    public void Seek(double elapsedMs)
    {
        Stop();
        foreach (var lane in _lanes) lane.Write(lane.Value(elapsedMs));
    }

    /// <summary>Jumps every lane to its end state.</summary>
    public void Settle()
    {
        Stop();
        foreach (var lane in _lanes) lane.Write(lane.To);
    }

    /// <summary>
    /// Stops writing frames without touching the values it last wrote. Needed
    /// because two running boards writing one property is a race rather than a
    /// frame: a capture that seeks a row while the row's own opening is still
    /// running photographs whichever board wrote last.
    /// </summary>
    public void Stop()
    {
        CompositionTarget.Rendering -= OnFrame;
        _running = false;
        _started = -1;
    }

    /// <summary>This board's clock, or null when it is not running.</summary>
    public long? ElapsedMs => _started < 0 ? null : Environment.TickCount64 - _started;

    /// <summary>What one lane holds at an instant, without writing anything.</summary>
    public double? Peek(string name, double elapsedMs)
        => _lanes.FirstOrDefault(value => value.Name == name)?.Value(elapsedMs);

    /// <summary>Every lane's opening and closing value, so an overshoot is visible as a number.</summary>
    public string Range(double upToMs)
    {
        var report = new List<string>();
        const int steps = 640;
        foreach (var lane in _lanes)
        {
            var min = double.MaxValue;
            var max = double.MinValue;
            for (var step = 0; step <= steps; step++)
            {
                var value = lane.Value(upToMs * step / steps);
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
            var span = lane.To - lane.From;
            report.Add($"    {lane.Name,-16} from {lane.From:0.###} to {lane.To:0.###}"
                + $"  min {min:0.####}  max {max:0.####}"
                + (span > 0 ? $"  peak {max:0.####} = {(max - lane.From) / Math.Abs(span) * 100:0.00}% of the way" : ""));
        }
        return string.Join(Environment.NewLine, report);
    }

    /// <summary>Every lane's value at one instant, for a probe that has to explain a frame.</summary>
    public string Sample(double elapsedMs)
        => "    " + string.Join("  ",
            _lanes.Select(lane => $"{lane.Name}={lane.Value(elapsedMs):0.###}(lin {lane.Linear(elapsedMs):0.###})"));

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_started < 0) return;
        var elapsed = Environment.TickCount64 - _started;

        var complete = true;
        foreach (var lane in _lanes)
        {
            if (elapsed < lane.BeginMs + lane.DurationMs) complete = false;
            lane.Write(lane.Value(elapsed));
        }

        if (!complete) return;
        CompositionTarget.Rendering -= OnFrame;
        _running = false;
    }
}
