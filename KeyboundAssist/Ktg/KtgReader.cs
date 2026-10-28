using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace KeyboundAssist.Ktg;

public sealed record WordSlot(uint SlotNodeId, string Word, bool WordShown);

// An enemy cast bar on screen (KTGEnemyCastBar, same slot ids as KTGEWord).
public sealed record CastSlot(uint SlotNodeId, string Name);

public enum Verdict
{
    None,
    Hit,
    Miss,
}

public sealed class KtgSnapshot
{
    // KTGInput and KTGEWord are loaded and visible: a match is on screen.
    public bool InMatch { get; init; }

    // Text currently in the duty's input box.
    public string InputText { get; init; } = string.Empty;

    // KTGInput op 6 [2]: false during the start countdown and "Inputs disabled!".
    public bool InputEnabled { get; init; }

    // KTGInput op 6 [4]: the 7 → 0 countdown of "Inputs disabled!" (0 outside a stun).
    public int StunCountdown { get; init; }

    public bool Stunned => !this.InputEnabled && this.StunCountdown > 0;

    public IReadOnlyList<WordSlot> Words { get; init; } = [];

    public IReadOnlyList<CastSlot> Casts { get; init; } = [];
}

// Reads the Keybound Brawler UI (internal prefix KTG). Layout and opcodes are runtime observations,
// see re/NOTES.md. Framework thread only.
public sealed unsafe class KtgReader : IDisposable
{
    // KTGEWord: one component per enemy slot (NodeId 2, 20001..20019); word text at <slot>/4/19/20.
    private static readonly uint[] WordTextPath = [4, 19, 20];

    // KTGInput: text of the AtkComponentTextInput at 1/2/7, inner text node 16.
    private static readonly uint[] InputTextPath = [2, 7, 16];

    // KTGEnemyCastBar: cast name at <slot>/2/4 (2/5 countdown, 2/6 fill, 2/3 "Interrupted!").
    private static readonly uint[] CastNamePath = [2, 4];

    private readonly IAddonLifecycle lifecycle;
    private bool inputEnabled;
    private int stunCountdown;

    public KtgReader(IAddonLifecycle lifecycle)
    {
        this.lifecycle = lifecycle;
        this.lifecycle.RegisterListener(AddonEvent.PostRefresh, "KTGInput", this.OnInputRefresh);
        this.lifecycle.RegisterListener(AddonEvent.PreFinalize, "KTGInput", this.OnInputFinalize);
    }

    public Verdict LastVerdict { get; private set; }

    // Environment.TickCount64 of the last op 7/8.
    public long LastVerdictAt { get; private set; }

    // Last KTGInput op 5 (`[5, s, s]`): the box (re)activating on a key press.
    public long LastActivationAt { get; private set; }

    public void Dispose()
    {
        this.lifecycle.UnregisterListener(AddonEvent.PostRefresh, "KTGInput", this.OnInputRefresh);
        this.lifecycle.UnregisterListener(AddonEvent.PreFinalize, "KTGInput", this.OnInputFinalize);
    }

    public KtgSnapshot Read()
    {
        var input = Find("KTGInput");
        var words = Find("KTGEWord");
        if (input == null || words == null || !input->IsVisible || !words->IsVisible)
            return new KtgSnapshot { InputEnabled = this.inputEnabled, StunCountdown = this.stunCountdown };

        var inputNode = Resolve(input->RootNode, InputTextPath);
        var inputText = inputNode != null && inputNode->Type == NodeType.Text ? Decode(&((AtkTextNode*)inputNode)->NodeText) : string.Empty;

        var slots = new List<WordSlot>();
        foreach (var (slotId, text, _) in SlotTexts(words, WordTextPath))
        {
            var word = Decode(&((AtkTextNode*)text)->NodeText).Trim();
            // "Phrases partially obscured!" only blinks the node; the text stays whole.
            if (word.Length > 0)
                slots.Add(new WordSlot(slotId, word, ((AtkResNode*)text)->IsVisible()));
        }

        var casts = new List<CastSlot>();
        var castBar = Find("KTGEnemyCastBar");
        if (castBar != null && castBar->IsVisible)
        {
            // A cast is on screen while its slot component is visible (the slot hides when the cast ends).
            foreach (var (slotId, text, _) in SlotTexts(castBar, CastNamePath))
            {
                var name = Decode(&((AtkTextNode*)text)->NodeText).Trim();
                if (name.Length > 0 && ((AtkResNode*)text)->IsVisible())
                    casts.Add(new CastSlot(slotId, name));
            }
        }

        return new KtgSnapshot
        {
            InMatch = true,
            InputText = inputText,
            InputEnabled = this.inputEnabled,
            StunCountdown = this.stunCountdown,
            Words = slots,
            Casts = casts,
        };
    }

    // Visible, on-screen enemy slots (NodeId 2, 20001..) of a KTG per-enemy addon, with the text
    // node at `path` inside each.
    private static List<(uint SlotId, nint Text, nint Slot)> SlotTexts(AtkUnitBase* addon, uint[] path)
    {
        var result = new List<(uint, nint, nint)>();
        var guard = 0;
        for (var n = addon->RootNode == null ? null : addon->RootNode->ChildNode; n != null && guard++ < 256; n = n->PrevSiblingNode)
        {
            if ((ushort)n->Type < 1000 || (n->NodeId != 2 && n->NodeId < 20001))
                continue;
            // Unused slots are parked off-screen at about -10000.
            if (!n->IsVisible() || n->ScreenX < -1000 || n->ScreenY < -1000)
                continue;
            var text = Resolve(n, path);
            if (text != null && text->Type == NodeType.Text)
                result.Add((n->NodeId, (nint)text, (nint)n));
        }

        return result;
    }

    private void OnInputRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRefreshArgs a || a.AtkValueCount == 0 || a.AtkValues == 0)
            return;
        var values = (AtkValue*)a.AtkValues;
        if (values[0].Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int)
            return;
        switch (values[0].Int)
        {
            case 6 when a.AtkValueCount > 2 && values[2].Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool:
                this.inputEnabled = values[2].Bool;
                this.stunCountdown = a.AtkValueCount > 4 && values[4].Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int ? values[4].Int : 0;
                break;
            case 5:
                this.LastActivationAt = Environment.TickCount64;
                break;
            case 7:
                this.LastVerdict = Verdict.Hit;
                this.LastVerdictAt = Environment.TickCount64;
                break;
            case 8:
                this.LastVerdict = Verdict.Miss;
                this.LastVerdictAt = Environment.TickCount64;
                break;
        }
    }

    private void OnInputFinalize(AddonEvent type, AddonArgs args)
    {
        this.inputEnabled = false;
        this.stunCountdown = 0;
    }

    private static AtkUnitBase* Find(string name)
    {
        var mgr = RaptureAtkUnitManager.Instance();
        if (mgr == null)
            return null;
        ref var list = ref mgr->AllLoadedUnitsList;
        for (var i = 0; i < list.Count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit != null && unit->NameString == name)
                return unit;
        }

        return null;
    }

    // Follows NodeIds the way Cartographer paths are built: through a component's own UldManager
    // root siblings, otherwise through the child list.
    private static AtkResNode* Resolve(AtkResNode* node, uint[] path)
    {
        foreach (var id in path)
        {
            if (node == null)
                return null;
            AtkResNode* first;
            if ((ushort)node->Type >= 1000)
            {
                var comp = ((AtkComponentNode*)node)->Component;
                first = comp == null ? null : comp->UldManager.RootNode;
            }
            else
            {
                first = node->ChildNode;
            }

            AtkResNode* match = null;
            var guard = 0;
            for (var n = first; n != null && guard++ < 512; n = n->PrevSiblingNode)
            {
                if (n->NodeId == id)
                {
                    match = n;
                    break;
                }
            }

            node = match;
        }

        return node;
    }

    private static string Decode(Utf8String* s)
    {
        var span = s->AsSpan();
        if (span.IndexOf((byte)0x02) < 0)
            return Encoding.UTF8.GetString(span);
        try { return SeString.Parse(span.ToArray()).TextValue; }
        catch { return Encoding.UTF8.GetString(span); }
    }
}
