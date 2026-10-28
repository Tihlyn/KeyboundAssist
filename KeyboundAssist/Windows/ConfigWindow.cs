using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace KeyboundAssist.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private string newCast = string.Empty;

    public ConfigWindow(Configuration config)
        : base("Keybound Assist settings##config", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
    }

    public override void Draw()
    {
        var changed = false;
        var c = this.config;

        var wpm = c.WordsPerMinute;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.SliderInt("Words per minute", ref wpm, 20, Configuration.MaxWordsPerMinute))
        {
            c.WordsPerMinute = wpm;
            changed = true;
        }

        var jitter = c.Jitter;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.SliderFloat("Rhythm variation", ref jitter, 0.10f, 0.60f, "%.2f"))
        {
            c.Jitter = jitter;
            changed = true;
        }

        var rmin = c.ReactionMinMs;
        var rmax = c.ReactionMaxMs;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.DragIntRange2("Reaction (ms)", ref rmin, ref rmax, 5f, 120, 2000))
        {
            c.ReactionMinMs = rmin;
            c.ReactionMaxMs = Math.Max(rmin + 10, rmax);
            changed = true;
        }

        var yield = c.YieldSeconds;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.SliderFloat("Stand back after you type (s)", ref yield, 0.5f, 10f, "%.1f"))
        {
            c.YieldSeconds = yield;
            changed = true;
        }

        var lower = c.TypeLowercase;
        if (ImGui.Checkbox("Type in lowercase", ref lower))
        {
            c.TypeLowercase = lower;
            changed = true;
        }

        var oldest = c.PreferOldestWord;
        if (ImGui.Checkbox("Prefer words that have been up longest", ref oldest))
        {
            c.PreferOldestWord = oldest;
            changed = true;
        }

        var show = c.ShowStatusWindow;
        if (ImGui.Checkbox("Show status window", ref show))
        {
            c.ShowStatusWindow = show;
            changed = true;
        }

        var stunWait = c.StunWaitSeconds;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.SliderFloat("Wait after a stun (s)", ref stunWait, 5f, 12f, "%.1f"))
        {
            c.StunWaitSeconds = stunWait;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"≈ {c.WordsPerMinute * 5 / 60.0:0.0} characters per second on average.");

        ImGui.Separator();
        ImGui.TextUnformatted("Gaze casts (box kept empty when they resolve):");
        string? remove = null;
        foreach (var name in c.DangerCasts)
        {
            ImGui.BulletText(name);
            ImGui.SameLine();
            if (ImGui.SmallButton($"remove##{name}"))
                remove = name;
        }

        if (remove is not null)
        {
            c.DangerCasts.Remove(remove);
            changed = true;
        }

        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("##newcast", ref this.newCast, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add cast") && this.newCast.Trim().Length > 0)
        {
            if (!c.DangerCasts.Contains(this.newCast.Trim(), StringComparer.OrdinalIgnoreCase))
                c.DangerCasts.Add(this.newCast.Trim());
            this.newCast = string.Empty;
            changed = true;
        }

        if (changed)
            c.Save();
    }
}
