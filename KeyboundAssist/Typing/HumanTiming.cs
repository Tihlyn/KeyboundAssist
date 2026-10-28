namespace KeyboundAssist.Typing;

// Delays that look like a person typing: log-normal key intervals around the configured WPM, a
// per-word speed drift, slower reaches for Shift/punctuation, a beat after spaces, rare hesitations.
public sealed class HumanTiming
{
    private readonly Configuration config;
    private readonly Random rng = new();
    private double wordFactor = 1;

    public HumanTiming(Configuration config)
    {
        this.config = config;
    }

    public void BeginWord() => this.wordFactor = Math.Clamp(1 + (0.08 * this.Gaussian()), 0.8, 1.25);

    public long Reaction()
    {
        var lo = Math.Max(80, this.config.ReactionMinMs);
        var hi = Math.Max(lo + 1, this.config.ReactionMaxMs);
        return (long)Math.Clamp(this.LogNormal((lo + hi) / 2.0, 0.25), lo, hi);
    }

    public long KeyInterval(char previous, char next)
    {
        var wpm = Math.Clamp(this.config.WordsPerMinute, 20, Configuration.MaxWordsPerMinute);
        var meanMs = 60000.0 / (wpm * 5);
        var sigma = Math.Clamp(this.config.Jitter, 0.05, 0.8);
        // Median such that the mean of the log-normal stays on the configured speed.
        var ms = this.LogNormal(meanMs * this.wordFactor / Math.Exp(sigma * sigma / 2), sigma);
        if (previous == ' ')
            ms += this.Uniform(30, 110);
        if (char.IsUpper(next) || char.IsPunctuation(next) || char.IsSymbol(next))
            ms += this.Uniform(25, 90);
        if (this.rng.NextDouble() < 0.02)
            ms += this.Uniform(180, 450);
        return (long)Math.Clamp(ms, 45, 1200);
    }

    // Box clears ~200–300 ms after Enter (server verdict); budget the worst pre-submit pause too.
    public const long SubmitOverheadMs = 220 + 350 + 250;

    public double MeanKeyMs => 60000.0 / (Math.Clamp(this.config.WordsPerMinute, 20, Configuration.MaxWordsPerMinute) * 5);

    // Conservative: mean interval plus the extra beats after spaces and before Shift/punctuation.
    public long EstimateTyping(int chars) => (long)(chars * this.MeanKeyMs * 1.25);

    public static long EstimateErase(int chars) => (chars * 125) + 150;

    public long KeyHold() => (long)this.Uniform(45, 95);

    public long BeforeSubmit() => (long)this.Uniform(70, 220);

    public long AfterWord() => (long)this.Uniform(120, 380);

    public long Erase() => (long)this.Uniform(65, 125);

    private double Uniform(double lo, double hi) => lo + (this.rng.NextDouble() * (hi - lo));

    private double LogNormal(double median, double sigma) => median * Math.Exp(sigma * this.Gaussian());

    private double Gaussian()
    {
        // Box-Muller.
        var u1 = 1.0 - this.rng.NextDouble();
        var u2 = this.rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
