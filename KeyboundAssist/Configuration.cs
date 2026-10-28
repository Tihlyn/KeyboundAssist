using Dalamud.Configuration;
using Dalamud.Plugin;

namespace KeyboundAssist;

public sealed class Configuration : IPluginConfiguration
{
    // Hard ceiling for WordsPerMinute: the point is a human pace, never a race.
    public const int MaxWordsPerMinute = 154;

    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 2;

    // Plan words and timing and show them, but send no keys. On by default.
    public bool DryRun { get; set; } = true;

    // Average typing speed (5 characters per word).
    public int WordsPerMinute { get; set; } = 60;

    // Spread of key-to-key intervals (log-normal sigma).
    public float Jitter { get; set; } = 0.30f;

    // Delay between picking a word and the first key.
    public int ReactionMinMs { get; set; } = 300;

    public int ReactionMaxMs { get; set; } = 750;

    // Lowercase is accepted (matching ignores case) and saves Shift presses.
    public bool TypeLowercase { get; set; } = true;

    // Favor words that have been on screen longest instead of picking at random.
    public bool PreferOldestWord { get; set; } = true;

    // After you type something yourself, stand back this long once the box is empty again.
    public float YieldSeconds { get; set; } = 3f;

    public bool ShowStatusWindow { get; set; } = true;

    // Casts that stun ("Inputs disabled!") anyone whose box is not empty when they resolve.
    // Learned automatically when a stun follows another cast.
    public List<string> DangerCasts { get; set; } = ["Cursed Gaze", "Dynamic Sensory Jammer"];

    // Observed cast bar duration of those casts (5 of 5 at 4.68 s).
    public int GazeCastMs { get; set; } = 4680;

    // No keys this long after the bar disappears: any key from ~+0.8 s up to at least +2.83 s after
    // it stunned (11 casts, sessions 2–6). Raised automatically if a later key still gets stunned.
    public int GazeResolveMs { get; set; } = 5000;

    // Pause after a stun starts before typing again.
    public float StunWaitSeconds { get; set; } = 7f;

    public static Configuration Load(IDalamudPluginInterface pluginInterface)
    {
        var config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.pluginInterface = pluginInterface;
        config.WordsPerMinute = Math.Clamp(config.WordsPerMinute, 20, MaxWordsPerMinute);
        // The deserializer appends to initialized lists, so defaults repeat on every load.
        config.DangerCasts = config.DangerCasts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (config.Version < 2)
        {
            // v1 kept the box clear only 2 s after a gaze; session 6 was stunned at +2.75 s.
            config.GazeResolveMs = Math.Max(config.GazeResolveMs, 5000);
            config.Version = 2;
        }

        return config;
    }

    public void Save() => this.pluginInterface?.SavePluginConfig(this);
}
