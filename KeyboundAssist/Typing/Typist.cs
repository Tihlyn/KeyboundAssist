using KeyboundAssist.Ktg;

namespace KeyboundAssist.Typing;

public enum Phase
{
    Off,
    Waiting,
    Idle,
    Reacting,
    Typing,
    BeforeSubmit,
    AwaitingVerdict,
    Erasing,
    Yielding,
    Holding,
    Stunned,
    Recovering,
    Reactivating,
}

public sealed class TypistStats
{
    public int Submitted { get; set; }

    public int Hits { get; set; }

    public int Misses { get; set; }

    // Target taken by another player while we were on it.
    public int Stolen { get; set; }

    // Typed prefix carried over to another word after the target vanished.
    public int Retargeted { get; set; }

    public int Erased { get; set; }

    // Lost keys pressed again / Enter pressed again.
    public int Retries { get; set; }

    public int Recoveries { get; set; }

    public int Stuns { get; set; }

    // Words dropped or not started because a gaze cast would end first.
    public int GazeAborts { get; set; }
}

// Human-paced typist. Rules that make it error-free:
// - Enter is pressed only when the box equals (ignoring case) a word that is on screen right now.
// - A target that disappears mid-word is carried over to another word sharing the typed prefix,
//   otherwise erased with Backspace — never submitted.
// - The box is empty whenever a gaze cast resolves (GazeGuard), so those casts cannot stun us.
// - Every key must show up in the box (echo); a lost key is pressed again, then the typist recovers
//   (re-reads the box, continues or erases) instead of stopping.
// - Anything typed by the player wins: the typist stands back until the box is empty again.
// One Tick per framework update.
public sealed class Typist
{
    private const long EchoTimeoutMs = 450;
    private const long VerdictTimeoutMs = 1500;
    private const long MinWordAgeMs = 250;
    private const long StallMs = 6000;
    private const int MaxKeyRetries = 2;
    private const string NotFocused = "game window not in front — paused";
    private const string InputsDisabled = "inputs disabled — waiting";

    private readonly Configuration config;
    private readonly KtgReader reader;
    private readonly HumanTiming timing;
    private readonly GazeGuard gaze;
    private readonly Action<string> notify;
    private readonly Random rng = new();
    private readonly Dictionary<uint, (string Word, long Since)> slotSeen = new();

    // Dry run types nothing, so a "submitted" word stays up; treat it as done until its slot changes.
    private readonly HashSet<(uint Slot, string Word)> drySubmitted = new();

    private string targetText = string.Empty;
    private string echoFrom = string.Empty;
    private bool awaitingEcho;
    private KeyStroke? lastStroke;
    private int keyRetries;
    private long sentAt;
    private long dueAt;
    private KeyStroke? held;
    private long keyUpAt;
    private long submittedAt;
    private bool verdictCounted;
    private int enterRetries;
    private long yieldUntil;
    private bool inputsDisabled;
    private long stunnedAt;
    private long resumeAt;
    private int consecutiveRecoveries;
    private long lastProgressAt;
    private Phase lastPhase;
    private long lastKeyAt;

    // Reactivation: after a stun the box ignores Backspace until a plain letter key wakes it
    // (KTGInput op 5); the letter is erased along with the leftover text (session 6).
    private long activationSentAt;
    private string activationFrom = string.Empty;
    private int activationTries;

    // Wake-ups since the last key that landed: each one adds a letter, so cap the cycle.
    private int reactivations;

    // Until a key lands after a stun/recovery, start words with an unshifted key (a Shift key may
    // not wake the box: the only post-stun key lost besides Backspace was '.', Shift on AZERTY).
    private bool needsPlainStart;

    public Typist(Configuration config, KtgReader reader, Action<string> notify)
    {
        this.config = config;
        this.reader = reader;
        this.notify = notify;
        this.timing = new HumanTiming(config);
        this.gaze = new GazeGuard(config);
    }

    public bool Armed { get; private set; }

    public Phase Phase { get; private set; } = Phase.Off;

    // The word as displayed over the enemy.
    public string Target { get; private set; } = string.Empty;

    // What we believe is in the box (in dry run: what would be).
    public string Typed { get; private set; } = string.Empty;

    public string LastAction { get; private set; } = string.Empty;

    // Pending gaze cast, if any (status window).
    public string? GazeReason => this.gaze.Reason;

    public TypistStats Stats { get; } = new();

    public void Arm(bool on)
    {
        this.ReleaseKeys();
        this.Armed = on;
        this.consecutiveRecoveries = 0;
        this.Reset(on ? Phase.Waiting : Phase.Off);
        this.LastAction = on ? (this.config.DryRun ? "armed (dry run: no keys sent)" : "armed") : "disarmed";
    }

    // Releases a key still held down (also Shift). Safe to call any time; Plugin.Dispose calls it.
    // `held` is only ever set by a real press, so release regardless of the dry-run setting.
    public void ReleaseKeys()
    {
        if (this.held is { } k)
        {
            KeySender.Up(k);
            this.held = null;
        }
    }

    // Back off, re-read the box, then carry on (used for lost keys, failed Enter, stalls, errors).
    public void Recover(string reason)
    {
        var now = Environment.TickCount64;
        this.ReleaseKeys();
        this.awaitingEcho = false;
        this.keyRetries = 0;
        this.Stats.Recoveries++;
        this.consecutiveRecoveries++;
        var backoff = Math.Min(5000, 750L << Math.Min(this.consecutiveRecoveries - 1, 4));
        this.Phase = Phase.Recovering;
        this.dueAt = now + backoff;
        this.lastProgressAt = now;
        this.needsPlainStart = true;
        this.LastAction = $"recovering in {backoff / 1000.0:0.0}s ({reason})";
        if (this.consecutiveRecoveries == 5)
            this.notify($"having trouble: {reason} — still retrying (/kba off to stop)");
    }

    public void Tick(KtgSnapshot s)
    {
        var now = Environment.TickCount64;
        var dry = this.config.DryRun;

        // Key-up always goes out, even after a disarm.
        if (this.held is not null && now >= this.keyUpAt)
            this.ReleaseKeys();

        this.TrackSlots(s, now);
        this.gaze.Update(s, now);

        // Any phase change counts as progress for the watchdog, including waits (stun, recovery).
        if (this.Phase != this.lastPhase)
        {
            this.lastPhase = this.Phase;
            this.lastProgressAt = now;
        }

        if (!this.Armed)
        {
            this.Phase = Phase.Off;
            return;
        }

        if (!s.InMatch)
        {
            if (this.Phase != Phase.Waiting)
            {
                this.Reset(Phase.Waiting);
                this.gaze.Reset();
                this.LastAction = "waiting for a Keybound Brawler match";
            }

            return;
        }

        var input = dry ? this.Typed : s.InputText;

        // "Inputs disabled!": wait StunWaitSeconds from its start, then pick up where we were.
        if (s.Stunned)
        {
            if (this.Phase != Phase.Stunned)
                this.OnStun(now);
            this.awaitingEcho = false;
            if (!dry)
                this.Typed = input;
            return;
        }

        if (!s.InputEnabled)
        {
            // Start countdown or a wave transition: keys would be dropped. Re-sync and wait.
            if (!this.inputsDisabled)
                this.LastAction = InputsDisabled;
            this.inputsDisabled = true;
            this.awaitingEcho = false;
            if (!dry)
                this.Typed = input;
            return;
        }

        if (this.inputsDisabled)
        {
            this.inputsDisabled = false;
            if (this.Phase is Phase.Reacting or Phase.Typing or Phase.BeforeSubmit or Phase.Erasing)
            {
                this.Phase = Phase.Recovering;
                this.dueAt = now + this.timing.Reaction();
            }
        }

        if (this.Phase == Phase.Stunned)
        {
            if (now >= this.resumeAt)
                this.AfterStun(input, now, dry);
            return;
        }

        if (this.Phase == Phase.Recovering)
        {
            if (now >= this.dueAt)
                this.Resync(s, input, now);
            return;
        }

        if (this.Phase == Phase.Reactivating)
        {
            this.TickReactivating(input, now, dry);
            return;
        }

        if (this.Phase == Phase.Yielding)
        {
            this.TickYielding(s, input, now);
            return;
        }

        if (!dry && input != this.Typed)
        {
            if (this.awaitingEcho && input == this.echoFrom)
            {
                if (now - this.sentAt <= EchoTimeoutMs || this.held is not null)
                    return;
                // A lost Backspace means the box went inactive (after a stun): wake it with a letter.
                if (this.lastStroke == KeySender.Backspace && input.Length > 0)
                {
                    this.Typed = input;
                    this.BeginReactivation(now, "Backspace not reaching the box — waking it");
                    return;
                }

                // Key lost (seen on the first key of a word and right after stuns): press it again.
                if (this.keyRetries >= MaxKeyRetries || this.lastStroke is not { } k)
                {
                    if (input.Length > 0)
                    {
                        this.Typed = input;
                        this.BeginReactivation(now, "keys not reaching the box — waking it and clearing");
                    }
                    else
                    {
                        this.Recover("keys are not reaching the input box");
                    }
                }
                else if (KeySender.GameFocused && this.Press(k, now))
                {
                    this.keyRetries++;
                    this.Stats.Retries++;
                    this.sentAt = now;
                    this.LastAction = $"key lost — retry {this.keyRetries}";
                }

                return;
            }

            this.awaitingEcho = false;
            if (input.Length > 0)
            {
                // Not ours: the player is typing.
                this.Phase = Phase.Yielding;
                this.Typed = input;
                this.Target = string.Empty;
                this.yieldUntil = now + (long)(this.config.YieldSeconds * 1000);
                this.LastAction = "you are typing — standing back";
                return;
            }

            if (this.Phase != Phase.AwaitingVerdict)
            {
                // The game emptied the box on its own.
                this.Typed = string.Empty;
                this.Target = string.Empty;
                this.Phase = Phase.Idle;
                this.dueAt = now + this.timing.Reaction();
                this.LastAction = "input cleared by the game";
                return;
            }
        }
        else if (this.awaitingEcho)
        {
            this.awaitingEcho = false;
            this.keyRetries = 0;
            this.lastProgressAt = now;
            this.needsPlainStart = false;
            this.reactivations = 0;
        }

        this.Step(s, input, now, dry);

        if (this.Phase != this.lastPhase)
        {
            this.lastPhase = this.Phase;
            this.lastProgressAt = now;
        }
        else if (this.Phase is (Phase.Reacting or Phase.Typing or Phase.BeforeSubmit or Phase.AwaitingVerdict or Phase.Erasing)
                 && now - this.lastProgressAt > StallMs && this.held is null && (dry || KeySender.GameFocused))
        {
            // Watchdog; an alt-tab pause is deliberate and does not count.
            this.Recover($"no progress in {this.Phase} for {StallMs / 1000}s");
        }
    }

    private void Step(KtgSnapshot s, string input, long now, bool dry)
    {
        switch (this.Phase)
        {
            case Phase.Waiting:
                this.Phase = Phase.Idle;
                this.dueAt = now + this.timing.Reaction();
                break;

            case Phase.Idle:
            case Phase.Holding:
            {
                if (now < this.dueAt)
                    break;
                if (this.Typed.Length > 0)
                {
                    this.Phase = Phase.Erasing;
                    break;
                }

                var reaction = this.timing.Reaction();
                var pick = this.Pick(s, now, now + reaction);
                if (pick is null)
                {
                    if (this.gaze.Reason is { } why && s.Words.Count > 0 && this.Phase != Phase.Holding)
                    {
                        this.Phase = Phase.Holding;
                        this.Stats.GazeAborts++;
                        this.LastAction = $"holding: {why}";
                    }

                    break;
                }

                this.SetTarget(pick);
                this.timing.BeginWord();
                this.enterRetries = 0;
                this.Phase = Phase.Reacting;
                this.dueAt = now + reaction;
                this.LastAction = $"picked \"{pick}\"";
                break;
            }

            case Phase.Reacting:
                if (!Present(s, this.Target))
                {
                    this.LastAction = $"\"{this.Target}\" taken before starting";
                    this.Target = string.Empty;
                    this.Phase = Phase.Idle;
                    this.dueAt = now + this.timing.AfterWord();
                }
                else if (!this.FitsBeforeGaze(now, Math.Max(this.dueAt, now)))
                {
                    this.AbortForGaze(now);
                }
                else if (now >= this.dueAt)
                {
                    this.Phase = Phase.Typing;
                    this.dueAt = now;
                }

                break;

            case Phase.Typing:
            {
                if (!Present(s, this.Target) && !this.Retarget(s))
                {
                    this.Abandon(now);
                    break;
                }

                if (!this.FitsBeforeGaze(now, now))
                {
                    this.AbortForGaze(now);
                    break;
                }

                if (now < this.dueAt || this.held is not null)
                    break;
                if (this.Typed.Length >= this.targetText.Length)
                {
                    this.Phase = Phase.BeforeSubmit;
                    this.dueAt = now + this.timing.BeforeSubmit();
                    break;
                }

                if (!dry && !KeySender.GameFocused)
                {
                    this.LastAction = NotFocused;
                    break;
                }

                var c = this.targetText[this.Typed.Length];
                if (!this.SendChar(c, now, dry))
                    break;
                this.dueAt = this.Typed.Length < this.targetText.Length
                    ? now + this.timing.KeyInterval(c, this.targetText[this.Typed.Length])
                    : now;
                break;
            }

            case Phase.BeforeSubmit:
                if (!Present(s, this.Target))
                {
                    this.Abandon(now);
                    break;
                }

                if (now < this.dueAt)
                    break;
                // Enter must clear the box (~300 ms server round trip) before a gaze resolves.
                if (this.gaze.EmptyBy(now) - now < HumanTiming.SubmitOverheadMs - 220)
                {
                    this.AbortForGaze(now);
                    break;
                }

                if (!string.Equals(input, this.Target, StringComparison.OrdinalIgnoreCase))
                {
                    this.Phase = Phase.Erasing;
                    this.LastAction = "box does not match the word — erasing";
                    break;
                }

                if (!dry && (this.held is not null || !KeySender.GameFocused))
                {
                    if (this.held is null)
                        this.LastAction = NotFocused;
                    break;
                }

                // Verified: the box holds exactly a word that is on screen right now.
                this.Submit(s, now, dry);
                break;

            case Phase.AwaitingVerdict:
                if (!this.verdictCounted && this.reader.LastVerdictAt >= this.submittedAt)
                {
                    this.verdictCounted = true;
                    if (this.reader.LastVerdict == Verdict.Miss)
                    {
                        this.Stats.Misses++;
                        this.notify($"miss on \"{this.Target}\" (likely taken by another player in flight)");
                    }
                    else
                    {
                        this.Stats.Hits++;
                        this.consecutiveRecoveries = 0;
                    }
                }

                if (input.Length == 0)
                {
                    this.Typed = string.Empty;
                    this.Target = string.Empty;
                    this.Phase = Phase.Idle;
                    this.dueAt = now + this.timing.AfterWord();
                }
                else if (now - this.submittedAt > VerdictTimeoutMs && this.held is null)
                {
                    // Enter swallowed: press it again once if everything still checks out.
                    var retry = this.enterRetries < 1 && Present(s, this.Target) && string.Equals(input, this.Target, StringComparison.OrdinalIgnoreCase)
                                && KeySender.GameFocused && this.gaze.EmptyBy(now) - now > HumanTiming.SubmitOverheadMs;
                    if (!retry)
                    {
                        this.Recover("Enter did not submit");
                    }
                    else if (this.Press(KeySender.Enter, now))
                    {
                        this.enterRetries++;
                        this.Stats.Retries++;
                        this.submittedAt = now;
                        this.LastAction = $"Enter lost — pressed again for \"{this.Target}\"";
                    }
                }

                break;

            case Phase.Erasing:
                if (this.Typed.Length == 0)
                {
                    this.Target = string.Empty;
                    this.Phase = Phase.Idle;
                    this.dueAt = now + this.timing.Reaction();
                    break;
                }

                if (now < this.dueAt || this.held is not null)
                    break;
                if (!dry && !KeySender.GameFocused)
                {
                    this.LastAction = NotFocused;
                    break;
                }

                this.SendBackspace(now, dry);
                this.dueAt = now + this.timing.Erase();
                break;
        }
    }

    private void Submit(KtgSnapshot s, long now, bool dry)
    {
        this.Stats.Submitted++;
        this.submittedAt = now;
        this.verdictCounted = false;
        if (dry)
        {
            foreach (var w in s.Words)
            {
                if (string.Equals(w.Word, this.Target, StringComparison.OrdinalIgnoreCase))
                    this.drySubmitted.Add((w.SlotNodeId, w.Word));
            }

            this.Stats.Hits++;
            this.LastAction = $"(dry run) would submit \"{this.Target}\"";
            this.Typed = string.Empty;
            this.Target = string.Empty;
            this.Phase = Phase.Idle;
            this.dueAt = now + this.timing.AfterWord();
            return;
        }

        if (!this.Press(KeySender.Enter, now))
            return;
        this.Phase = Phase.AwaitingVerdict;
        this.LastAction = $"submitted \"{this.Target}\"";
    }

    private void OnStun(long now)
    {
        this.stunnedAt = now;
        this.resumeAt = now + (long)(this.config.StunWaitSeconds * 1000) + this.timing.Reaction();
        this.Stats.Stuns++;
        this.Phase = Phase.Stunned;
        this.LastAction = $"stunned — waiting {this.config.StunWaitSeconds:0.#}s";

        // Our own key stunned us after a gaze ended: the gaze lasts longer than the hold. Extend it
        // to that key's offset + 1.5 s (keys reach the server ~0.05–0.15 s before the stun shows).
        var gazeEnd = this.gaze.LastDangerEndAt;
        if (!this.config.DryRun && gazeEnd > 0 && now - gazeEnd < 12_000 && now - this.lastKeyAt < 400)
        {
            var offset = this.lastKeyAt - gazeEnd;
            var needed = (int)Math.Min(12_000, offset + 1500);
            if (needed > this.config.GazeResolveMs)
            {
                this.config.GazeResolveMs = needed;
                this.config.Save();
                this.notify($"stunned by a key {offset / 1000.0:0.0}s after a gaze ended — holding {needed / 1000.0:0.0}s after gazes from now on");
            }
        }

        // The cast that ended 0.5–2.5 s ago is the culprit. Learn it only when the stun is unexplained
        // and there is a single suspect: common casts like "Burst" end all the time.
        var suspects = this.gaze.EndedBetween(now, 500, 2500).ToList();
        if (suspects.Count == 0 || suspects.Any(this.gaze.IsDangerous))
            return;
        if (suspects.Count == 1)
        {
            this.config.DangerCasts.Add(suspects[0]);
            this.config.Save();
            this.notify($"learned: \"{suspects[0]}\" stuns when it resolves — the box will be kept empty for it");
        }
        else
        {
            this.notify($"unexplained stun; casts that just ended: {string.Join(", ", suspects)} — add the culprit in /kba config");
        }
    }

    // Stun over: whatever was in the box is stale; clear it (waking the box first) and start fresh.
    private void AfterStun(string input, long now, bool dry)
    {
        this.awaitingEcho = false;
        this.keyRetries = 0;
        this.lastProgressAt = now;
        this.Target = string.Empty;
        this.targetText = string.Empty;
        if (!dry)
            this.Typed = input;
        if (this.Typed.Length == 0)
        {
            this.needsPlainStart = true;
            this.Phase = Phase.Idle;
            this.dueAt = now + this.timing.Reaction();
            this.LastAction = "stun over — resuming";
            return;
        }

        this.BeginReactivation(now, $"stun over — clearing \"{this.Typed}\"");
    }

    private void BeginReactivation(long now, string why)
    {
        if (++this.reactivations > 3)
        {
            this.Recover("the box keeps rejecting Backspace");
            return;
        }

        this.awaitingEcho = false;
        this.keyRetries = 0;
        this.activationSentAt = 0;
        this.activationTries = 0;
        this.Target = string.Empty;
        this.targetText = string.Empty;
        // Dry run has no real box to wake.
        this.Phase = this.config.DryRun ? Phase.Erasing : Phase.Reactivating;
        this.dueAt = now;
        this.LastAction = why;
    }

    // Press one plain letter so the box accepts keys again, then erase everything.
    private void TickReactivating(string input, long now, bool dry)
    {
        if (input.Length == 0)
        {
            this.Typed = string.Empty;
            this.needsPlainStart = true;
            this.Phase = Phase.Idle;
            this.dueAt = now + this.timing.Reaction();
            return;
        }

        if (this.held is not null)
            return;

        if (this.activationSentAt > 0)
        {
            // Awake once the letter shows up, or the box reports activation (op 5) and ate the key.
            if (input != this.activationFrom || this.reader.LastActivationAt >= this.activationSentAt)
            {
                this.activationSentAt = 0;
                this.Typed = input;
                this.Phase = Phase.Erasing;
                this.dueAt = now + this.timing.Erase();
                this.LastAction = $"box awake — clearing \"{input}\"";
                return;
            }

            if (now - this.activationSentAt <= EchoTimeoutMs)
                return;
            this.activationSentAt = 0;
            if (++this.activationTries >= 4)
            {
                this.Recover("the box would not wake up");
                return;
            }

            this.dueAt = now + (250L * this.activationTries);
        }

        if (now < this.dueAt)
            return;
        if (!dry && !KeySender.GameFocused)
        {
            this.LastAction = NotFocused;
            return;
        }

        if (ActivationKey is not { } k || !this.Press(k, now))
            return;
        this.activationSentAt = now;
        this.activationFrom = input;
    }

    // An unshifted letter on the active layout ('a' unless the layout is unusual).
    private static KeyStroke? ActivationKey
        => "aeiouxzq".Select(KeySender.Resolve).FirstOrDefault(k => k is { Shift: false });

    private void TickYielding(KtgSnapshot s, string input, long now)
    {
        if (input != this.Typed)
        {
            this.Typed = input;
            this.yieldUntil = now + (long)(this.config.YieldSeconds * 1000);
            return;
        }

        if (now < this.yieldUntil)
            return;
        if (input.Length == 0)
        {
            this.Phase = Phase.Idle;
            this.dueAt = now + this.timing.Reaction();
            this.LastAction = "resuming";
            return;
        }

        // Text left untouched for the whole yield: finish it if it is a word's start, else clear it,
        // so a stray partial word cannot block typing.
        this.LastAction = "taking over the idle box";
        this.Resync(s, input, now);
    }

    // Continue from whatever is in the box: same target, another word sharing the prefix, or erase.
    private void Resync(KtgSnapshot s, string input, long now)
    {
        this.awaitingEcho = false;
        this.keyRetries = 0;
        if (!this.config.DryRun)
            this.Typed = input;
        if (this.Typed.Length == 0)
        {
            this.Target = string.Empty;
            this.Phase = Phase.Idle;
            this.dueAt = now + this.timing.Reaction();
            return;
        }

        if (this.Target.Length > 0 && Present(s, this.Target) && this.Target.StartsWith(this.Typed, StringComparison.OrdinalIgnoreCase))
        {
            this.targetText = this.Typed + this.Expected(this.Target)[this.Typed.Length..];
            this.Phase = Phase.Typing;
            this.dueAt = now + (this.timing.Reaction() / 2);
            return;
        }

        if (this.Retarget(s))
        {
            this.Phase = Phase.Typing;
            this.dueAt = now + (this.timing.Reaction() / 2);
            return;
        }

        this.Target = string.Empty;
        this.Phase = Phase.Erasing;
        this.dueAt = now + 200;
    }

    // Can the current word still be finished and submitted before the next gaze resolves?
    private bool FitsBeforeGaze(long now, long startAt)
    {
        var emptyBy = this.gaze.EmptyBy(now);
        if (emptyBy == long.MaxValue)
            return true;
        var remaining = Math.Max(0, this.targetText.Length - this.Typed.Length);
        return startAt + this.timing.EstimateTyping(remaining) + HumanTiming.SubmitOverheadMs < emptyBy;
    }

    private void AbortForGaze(long now)
    {
        this.Stats.GazeAborts++;
        this.LastAction = $"dropping \"{this.Target}\": {this.gaze.Reason}";
        this.Target = string.Empty;
        // Erase right away: the box has to be empty when the cast resolves.
        this.Phase = this.Typed.Length > 0 ? Phase.Erasing : Phase.Holding;
        this.dueAt = now;
    }

    private bool Press(KeyStroke k, long now)
    {
        if (!KeySender.Down(k))
        {
            this.Recover("SendInput failed");
            return false;
        }

        this.held = k;
        this.keyUpAt = now + this.timing.KeyHold();
        this.lastProgressAt = now;
        this.lastKeyAt = now;
        return true;
    }

    private bool SendChar(char c, long now, bool dry)
    {
        if (!dry)
        {
            if (KeySender.Resolve(c) is not { } k)
            {
                // Pick filters these; if one slips through, drop the word rather than mistype it.
                this.LastAction = $"'{c}' cannot be typed on this layout — dropping \"{this.Target}\"";
                this.Target = string.Empty;
                this.Phase = this.Typed.Length > 0 ? Phase.Erasing : Phase.Idle;
                return false;
            }

            if (!this.Press(k, now))
                return false;
            this.lastStroke = k;
        }

        this.echoFrom = this.Typed;
        this.Typed += c;
        this.awaitingEcho = !dry;
        this.sentAt = now;
        return true;
    }

    private void SendBackspace(long now, bool dry)
    {
        if (!dry)
        {
            if (!this.Press(KeySender.Backspace, now))
                return;
            this.lastStroke = KeySender.Backspace;
        }

        this.echoFrom = this.Typed;
        this.Typed = this.Typed[..^1];
        this.awaitingEcho = !dry;
        this.sentAt = now;
        this.Stats.Erased++;
    }

    // Target vanished (another player got it): erase after a human beat.
    private void Abandon(long now)
    {
        this.Stats.Stolen++;
        this.LastAction = $"\"{this.Target}\" taken — erasing";
        this.Phase = this.Typed.Length > 0 ? Phase.Erasing : Phase.Idle;
        this.dueAt = now + (this.timing.Reaction() / 2);
    }

    private bool Retarget(KtgSnapshot s)
    {
        if (this.Typed.Length == 0)
            return false;
        foreach (var w in s.Words)
        {
            if (!w.Word.StartsWith(this.Typed, StringComparison.OrdinalIgnoreCase) || w.Word.Length <= this.Typed.Length || !KeySender.CanType(this.Expected(w.Word)))
                continue;
            var old = this.Target;
            this.Target = w.Word;
            // Keep what is already typed; continue with the new word's remainder.
            this.targetText = this.Typed + this.Expected(w.Word)[this.Typed.Length..];
            this.Stats.Retargeted++;
            this.LastAction = old.Length > 0 ? $"\"{old}\" taken — continuing as \"{w.Word}\"" : $"continuing \"{this.Typed}\" as \"{w.Word}\"";
            return true;
        }

        return false;
    }

    // Words that can be typed and submitted before the next gaze resolves, oldest first (mostly).
    private string? Pick(KtgSnapshot s, long now, long startAt)
    {
        var emptyBy = this.gaze.EmptyBy(now);
        var candidates = s.Words
            .Where(w => this.slotSeen.TryGetValue(w.SlotNodeId, out var seen) && now - seen.Since >= MinWordAgeMs)
            .Where(w => !this.drySubmitted.Contains((w.SlotNodeId, w.Word)))
            // Skip words with a character this layout only reaches through AltGr.
            .Where(w => KeySender.CanType(this.Expected(w.Word)))
            .Where(w => emptyBy == long.MaxValue || startAt + this.timing.EstimateTyping(w.Word.Length) + HumanTiming.SubmitOverheadMs < emptyBy)
            .Select(w => (w.Word, Since: this.slotSeen[w.SlotNodeId].Since))
            .ToList();
        if (candidates.Count == 0)
            return null;
        if (this.needsPlainStart && !this.config.DryRun)
        {
            var plain = candidates.Where(c => KeySender.Resolve(this.Expected(c.Word)[0]) is { Shift: false }).ToList();
            if (plain.Count > 0)
                candidates = plain;
        }

        if (!this.config.PreferOldestWord)
            return candidates[this.rng.Next(candidates.Count)].Word;

        candidates.Sort((a, b) => a.Since.CompareTo(b.Since));
        // Mostly the oldest, sometimes the next ones: a person does not always pick perfectly.
        var roll = this.rng.NextDouble();
        var index = roll < 0.6 ? 0 : roll < 0.85 ? 1 : 2;
        return candidates[Math.Min(index, candidates.Count - 1)].Word;
    }

    private void TrackSlots(KtgSnapshot s, long now)
    {
        var present = new HashSet<uint>();
        foreach (var w in s.Words)
        {
            present.Add(w.SlotNodeId);
            if (!this.slotSeen.TryGetValue(w.SlotNodeId, out var seen) || seen.Word != w.Word)
                this.slotSeen[w.SlotNodeId] = (w.Word, now);
        }

        foreach (var slot in this.slotSeen.Keys.Where(k => !present.Contains(k)).ToList())
            this.slotSeen.Remove(slot);
        // A dry "kill" lasts until the slot shows another word or empties.
        this.drySubmitted.RemoveWhere(d => !this.slotSeen.TryGetValue(d.Slot, out var seen) || seen.Word != d.Word);
    }

    private void SetTarget(string word)
    {
        this.Target = word;
        this.targetText = this.Expected(word);
    }

    private string Expected(string word) => this.config.TypeLowercase ? word.ToLowerInvariant() : word;

    private void Reset(Phase phase)
    {
        this.Phase = phase;
        this.Target = string.Empty;
        this.targetText = string.Empty;
        this.Typed = string.Empty;
        this.awaitingEcho = false;
        this.keyRetries = 0;
        this.inputsDisabled = false;
        this.dueAt = 0;
        this.drySubmitted.Clear();
    }

    private static bool Present(KtgSnapshot s, string word)
    {
        foreach (var w in s.Words)
        {
            if (string.Equals(w.Word, word, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
