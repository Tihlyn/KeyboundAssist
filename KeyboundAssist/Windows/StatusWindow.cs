using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using KeyboundAssist.Ktg;
using KeyboundAssist.Typing;

namespace KeyboundAssist.Windows;

// Live view: arm/dry-run switches, what the typist is doing, and what it sees.
public sealed class StatusWindow : Window
{
    private static readonly Vector4 Green = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly Plugin plugin;

    public StatusWindow(Plugin plugin)
        : base("Keybound Assist##status", ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        this.Size = new Vector2(380, 300);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var typist = this.plugin.Typist;
        var config = this.plugin.Configuration;
        var snap = this.plugin.LastSnapshot;

        var armed = typist.Armed;
        if (ImGui.Checkbox("Armed", ref armed))
            typist.Arm(armed);
        ImGui.SameLine();
        var dry = config.DryRun;
        if (ImGui.Checkbox("Dry run (no keys)", ref dry))
        {
            config.DryRun = dry;
            config.Save();
            if (typist.Armed)
                typist.Arm(true);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            this.plugin.ToggleConfig();

        var color = typist.Phase switch
        {
            Phase.Off => Grey,
            Phase.Stunned or Phase.Recovering or Phase.Reactivating => Red,
            Phase.Yielding or Phase.Waiting or Phase.Holding => Amber,
            _ => Green,
        };
        ImGui.TextColored(color, $"{typist.Phase}{(config.DryRun && typist.Armed ? " (dry run)" : string.Empty)}");
        if (typist.GazeReason is { } gaze)
            ImGui.TextColored(Red, $"GAZE: {gaze}");
        if (snap.Stunned)
            ImGui.TextColored(Red, $"Inputs disabled: {snap.StunCountdown}");
        ImGui.TextWrapped(typist.LastAction);

        ImGui.Separator();
        ImGui.TextUnformatted($"Target: {typist.Target}");
        ImGui.TextUnformatted($"Typed:  {typist.Typed}");
        ImGui.TextUnformatted($"Box:    {snap.InputText}   {(snap.InMatch ? (snap.InputEnabled ? "(enabled)" : "(disabled)") : "(no match)")}");

        var st = typist.Stats;
        ImGui.TextDisabled($"submitted {st.Submitted} · hits {st.Hits} · misses {st.Misses} · taken {st.Stolen} · carried over {st.Retargeted} · erased {st.Erased}");
        ImGui.TextDisabled($"retries {st.Retries} · recoveries {st.Recoveries} · stuns {st.Stuns} · gaze holds {st.GazeAborts}");

        ImGui.Separator();
        ImGui.TextDisabled($"{snap.Words.Count} word(s) on screen");
        foreach (var w in snap.Words)
        {
            var cast = snap.Casts.FirstOrDefault(c => c.SlotNodeId == w.SlotNodeId);
            ImGui.TextColored(w.WordShown ? Vector4.One : Grey, $"{w.SlotNodeId,6}  {w.Word}");
            if (cast is not null)
            {
                ImGui.SameLine();
                ImGui.TextColored(config.DangerCasts.Contains(cast.Name, StringComparer.OrdinalIgnoreCase) ? Red : Grey, $"  casting {cast.Name}");
            }
        }
    }
}
