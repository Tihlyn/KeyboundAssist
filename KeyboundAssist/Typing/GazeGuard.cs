using KeyboundAssist.Ktg;

namespace KeyboundAssist.Typing;

// Tracks the casts that stun anyone with text in the box when they resolve ("Cursed Gaze",
// "Dynamic Sensory Jammer"; session 2+5: 5 of 5 stuns, bar shown 4.68 s, stun 0.9–1.45 s after it
// disappears). Answers "by when must the box be empty" and "is it still unsafe to type".
public sealed class GazeGuard
{
    private readonly Configuration config;
    private readonly Dictionary<uint, (string Name, long Since)> active = new();
    private readonly List<(string Name, long EndedAt)> ended = [];
    private long resolveUntil;

    public GazeGuard(Configuration config)
    {
        this.config = config;
    }

    // Short description for the status window / log, null when nothing is pending.
    public string? Reason { get; private set; }

    // When the last dangerous cast bar disappeared (0 = none yet).
    public long LastDangerEndAt { get; private set; }

    public void Update(KtgSnapshot s, long now)
    {
        var seen = new HashSet<uint>();
        foreach (var c in s.Casts)
        {
            seen.Add(c.SlotNodeId);
            if (!this.active.TryGetValue(c.SlotNodeId, out var a) || a.Name != c.Name)
            {
                if (a.Name is not null)
                    this.End(a.Name, now);
                this.active[c.SlotNodeId] = (c.Name, now);
            }
        }

        foreach (var slot in this.active.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            this.End(this.active[slot].Name, now);
            this.active.Remove(slot);
        }

        this.ended.RemoveAll(e => now - e.EndedAt > 10_000);
        this.Reason = this.Describe(now);
    }

    // Time by which the box must be empty: now while a dangerous cast is resolving, otherwise the
    // projected end of the earliest dangerous cast on screen; long.MaxValue when none.
    public long EmptyBy(long now)
    {
        if (now < this.resolveUntil)
            return now;
        var end = long.MaxValue;
        foreach (var (name, since) in this.active.Values)
        {
            if (this.IsDangerous(name))
                end = Math.Min(end, Math.Max(now, since + this.config.GazeCastMs));
        }

        return end;
    }

    public bool Resolving(long now) => now < this.resolveUntil;

    // Casts that ended in [now - maxAgo, now - minAgo]: the suspects when a stun lands.
    public IEnumerable<string> EndedBetween(long now, long minAgo, long maxAgo)
        => this.ended.Where(e => now - e.EndedAt >= minAgo && now - e.EndedAt <= maxAgo).Select(e => e.Name).Distinct();

    public bool IsDangerous(string name) => this.config.DangerCasts.Contains(name, StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        this.active.Clear();
        this.ended.Clear();
        this.resolveUntil = 0;
        this.Reason = null;
    }

    private void End(string name, long now)
    {
        this.ended.Add((name, now));
        if (this.IsDangerous(name))
        {
            this.resolveUntil = Math.Max(this.resolveUntil, now + this.config.GazeResolveMs);
            this.LastDangerEndAt = now;
        }
    }

    private string? Describe(long now)
    {
        if (now < this.resolveUntil)
            return $"gaze active — no keys for {(this.resolveUntil - now) / 1000.0:0.0}s";
        foreach (var (name, since) in this.active.Values)
        {
            if (this.IsDangerous(name))
                return $"\"{name}\" ends in {Math.Max(0, since + this.config.GazeCastMs - now) / 1000.0:0.0}s";
        }

        return null;
    }
}
