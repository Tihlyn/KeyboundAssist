# Keybound Brawler (KTG) — field notes

Runtime observations from Cartographer captures. Nothing here is statically verified; treat every
meaning as *observed*. Raw data: `captures/session1/` (local only — contains player names/IDs).

Game 2026.10.16 · Dalamud 15.0.3.9 · CS 7.57.0.9414 · internal prefix **KTG**.

## Session 1 (2026-10-28, 13:28–13:34 UTC)

Flow: `KTGEnterDialogue` → Register → duty finder pop (`ContentsFinderConfirm`, 4 players) →
instance → `_KTGLocationTitle` → wave 1 "Defeat 25 enemies" (13:28:49–13:30:32, `_KTGClear`) →
wave 2 "Defeat all enemies" (13:30:41) → `_TextGimmickHint` "A powerful foe marches forth..."
(13:32:14) → `_KTGGameover` (13:34:21) → `KTGResult` → exit.

### Agents
| Id | Name | Notes |
|---|---|---|
| 397 | KTGHud | never reported active; likely owns the match state — trace + mem watch next |
| 398 | Unk398 | resolves to `AgentEmote` vtable (suspect fingerprint miss); sits inside the KTG block |
| 399 | KTGEnterDialogue | active only while the entry dialog is open |
| 400 | Unk400 | base `AgentInterface`; inside the KTG block |
| 401 | KTGResult | active while the result window is open |

None of the GoldSaucer / GoldSaucerHud / GoldSaucerMiniGame / Minigame / GoldSaucerReward agents saw traffic.

### Addons (refresh-driven: `values[0]` is an opcode)
| Addon | Role | Observed protocol |
|---|---|---|
| KTGEWord | words over enemies | 20 slots (node 2, 20001–20019); word text at `<slot>/4/19/20`; `<slot>/4/5/6`, `/4/5/7`, `/4/21/22` hidden texts (typed-prefix overlay?). Refresh op 2 `[objId]` ×131, op 3 `[objId]` ×39 (defeat?), op 100 `[0/1]` show/hide, op 200 init. objIds 0x4001xxxx (battle NPCs) |
| KTGEnemyCastBar | enemy casts | 20 slots, "Interrupted!" text per slot |
| KTGIGaugeHp | enemy HP | 20 slots |
| KTGInput | typing box | `AtkComponentTextInput` at node `1/2/7`; display text `1/2/6`. FireCallback `[1]` ×831 (per keystroke?). Refresh op 7 ×70 (word hit?), op 8 ×18 (mistype?), op 5 `[null,null]` ×91 (clear?), op 6 `[str, bool, bool, int 0..7]` per frame |
| KTGHudScore | score | op 1 `[score, 0, ?0..6136, 10000, 10, 0, 100, flag]` per frame; "BONUS x1.0" multiplier text |
| KTGHudTodo | objective | op 1 `[0, killed 0..24, 25]` in wave 1; op 2 `[1]` later |
| KTGHudGaugeHp | team HP | op 1 `[hp 0..40000, 40000]`; reached 0 → game over |
| KTGHudLog | event log | op 2 `[17837..17840, 5347/5348]` ×10 (sheet row ids?) |
| KTGPlayerName | 4 player tags | op 0 `[idx, name, isSelf]`, op 2 `[idx, x, y, 3.0]` every frame |
| KTGScreenTextFever / KTGPlayGuide | fever banner / guide | only per-frame op 3 layout seen |
| _KTGWave / _KTGClear / _KTGGameover | banners | setup `[69/71/73, 3, 12855x, "...", ?, objective, 122109, -, 100017x, ?]` |
| KTGResult | results | 78 values: `[1]` players=4, per-player stride 8 from `[6]` (score at +4), names `[61..64]`, rewards (841 MGP, 106 MGC, "Keybound Coffer") |
| KTGEnterDialogue | entry | `[3, MGC balance, ?, 112663, "MGC"]`; callback `[0]` = Register, `[-2]` = close |

Every KTG addon also gets an op-3 `[x, y, 3.0]` layout refresh every frame, which floods the
timeline (~460k refreshes in 6 minutes; the 60k ring kept only the last minute).

## Session 2 (2026-10-28, 13:54–14:05 UTC, victory)

Raw data: `captures/session2/journal.jsonl` (every timeline entry incl. per-frame text changes).
Only the journal and the agent's final memory survived; a Cartographer rebuild auto-reloaded the
plugin before the in-memory views were saved.

Match structure:
| Phase | Objective | Notes |
|---|---|---|
| Wave 1 | "Defeat 25 enemies" | `KTGHudTodo` counter n/25 (+1 per kill) |
| Wave 2 | "Defeat all enemies" | gimmicks: *Inputs disabled!* / *re-enabled!* (~7 s); `_TextGimmickHint` "A powerful foe marches forth..." then "Reinforce the barrier to withstand more powerful attacks!" → *Barrier reinforced!* / *The reinforcements fade...* (~10 s) |
| Warning | `_KTGWarning` (setup op 72) | before the boss |
| Wave 3 | "Defeat the airbuster" (boss) | adds *Phrases partially obscured!* / *unobscured!* (~30 s) |
| End | `_KTGClear` → `KTGResult` | victory reward 1,476 MGP + 186 MGC (loss in session 1: 841 + 106) |

Typing pipeline (observed):
1. Each keystroke → `KTGInput` FireCallback `[1]` (no character in the payload) →
   `AgentKTGHud.ReceiveEvent(kind=1, [1])` (`Unk398` receives the same) → typed text appears in
   `KTGInput` text node `1/2/7/16`.
2. Submit = **Enter** (`KTGPlayGuide`: "[Enter]: Submit Text  [Esc]: Delete Text/Quit"). Enter
   produces no FireCallback/agent ReceiveEvent; the verdict arrives ~230–300 ms later.
3. Matching is global (confirmed in play): typed text is compared with every enemy's current word.
4. **Hit**: input clears; `KTGInput` refresh op 7; `KTGEWord` refresh op 2 `[objId]`; that slot's
   word is replaced by the enemy's next word (enemies carry several words). Other players' hits show
   the same op 2 with their target's objId.
5. **Kill**: `KTGEWord` op 3 `[objId]` (50 in session 2); `KTGHudTodo` counter +1.
6. **Miss**: input clears; `KTGInput` refresh op 8.

Rewards are proportional to the player's own score, and the rate is the same win or lose:
`MGP = floor(score × 0.046)`, `MGC = floor(score × 0.0058)` (session 1, loss: 18302 → 841 / 106;
session 2, victory: 32092 → 1476 / 186). Words are a shared pool: the first player to type a word
gets the hit, and the enemy's next word replaces it for everyone.

Vocabulary: 148 distinct FF7-themed words/phrases in one match (e.g. "Tseng", "Zack Fair",
"Come on! Let's push it past the redline!") — likely a sheet; not located yet.

`AgentKTGHud` (397), size **0xF50** (next allocation is another agent):
| Offset | Observed |
|---|---|
| +0x40 + n·0x88 (n = 0..3) | player entry: name `Utf8String` + screen position floats |
| +0x258 | ushort, changed 150× (2200 at end) — last points gained? |
| +0x260 | own score (32092 at end = result screen) |
| +0x26C | per-frame counter (mute when watching) |
| +0x2AC | timer (mute when watching) |
| +0x274..+0x2A8 | 4-byte-spaced flags, flipped twice per match |
No per-enemy word table inside the agent.

Gimmick mechanics (observed in session 2):
- *Inputs disabled!*: `KTGInput` refresh op 6 `[6, str, enabled:bool, ?, countdown:int]` — index 2
  goes `false`, index 4 counts 7 → 0 (shown in `KTGInput` node `1/2/3`); `true` again on re-enable.
- *Phrases partially obscured!*: purely visual — each `KTGEWord` word node (`<slot>/4/19/20`) blinks
  hidden/shown every ~1.2–1.5 s, but its text stays the full word the whole time.
- Matching ignores case ("rosa" hit "Rosa").
- Misses only happen when the input is submitted (Enter); typed text can be erased with no penalty.

## Session 3 (2026-10-28, 14:38–14:41 UTC, Keybound Assist dry run, loss in wave 1 at 19/25)

Raw data: `captures/session3/` (journal, layouts, agent traces, memory watches of `AgentKTGHud`,
`AddonKTGEWord`, `AddonKTGInput`, closed trees). Reward formula confirmed a third time:
score 3500 → 161 MGP / 20 MGC.

Dry-run check against Cartographer's independent text-watch record: 45 of 45 would-be submissions
were on screen at that moment; keying pace ≈ 64 WPM (setting 60), ≈ 40 WPM including reaction and
pauses; 5 words taken by other players were abandoned correctly. Fixed afterwards: dry run kept
re-picking the same word, since nothing is really typed.

## Session 5 (2026-10-28, 15:09–15:20 UTC, Keybound Assist live)

177 words submitted by the assist, 0 misses (game: 178 hits / 0 misses for the match); score 41942
→ 1929 MGP / 243 MGC (formula holds). Issues: a lost first key twice (it stopped), two stuns.

**Gaze mechanic (sessions 2 + 5, 5 of 5 stuns):** enemy cast bars live in `KTGEnemyCastBar`, one per
`KTGEWord` slot (`<slot>/2/4` cast name, `<slot>/2/5` countdown 4 → 0, `<slot>/2/6` fill,
`<slot>/2/3` "Interrupted!"; the slot component hides when the cast ends). "Cursed Gaze" (wave 2)
and "Dynamic Sensory Jammer" (boss) show for 4.68 s; 0.90–1.45 s after the bar hides, anyone
whose input box is not empty gets *Inputs disabled!* (`KTGInput` op 6 `[2]=false, [4]=7`) for
~7 s. Other casts (Burst, Big Bomber, Tankbuster, Self-destruct, …) never stunned. The box keeps
its text through the stun.

## Session 6 (2026-10-28, 15:33–15:45 UTC, Keybound Assist live) — gaze model corrected

The gaze punishes **key presses**, not a non-empty box at one instant: across 11 gaze casts
(sessions 2–6) every stun landed 0.04–0.16 s after a key, keys up to ~+0.8 s after the bar
disappears were safe, and keys from +0.78 s up to at least +2.83 s stunned. The assist held 2 s
after the bar, typed at +2.6–2.75 s and was stunned three times. The end of the active window is
still unknown (no key was ever pressed later than +2.83 s without being stunned); no flag for it
was found in `AgentKTGHud` (0xF50), `AddonKTGEWord` (0x1800) or `AddonKTGInput` (0x800).

Other memory observations (watches with counters muted):
- `AgentKTGHud`+0x30: live enemy count; +0x2E8: stun (`-1` ↔ `4`).
- `AddonKTGInput`+0x238: input state `0x11` normal, `0x66` stunned, `0x14` coming back.
- `AddonKTGEWord`+0x2E4 / +0x2E8 (per slot, stride ~0xC8): `5` while that enemy casts.

After a stun the box keeps its text but goes inactive: Backspace is ignored until a plain letter key
(or a click) wakes it — `KTGInput` op 5, with `[1]`/`[2]` going null → "" on the click path. The
player's letters after a stun always landed; the assist's Backspaces were all lost, and its one
lost letter was '.', which needs Shift on AZERTY.

## Input injection (tested 2026-10-28 on the chat box, same `AtkComponentTextInput`)

`PostMessageW(gameHwnd, …)` works with the game window in the background:
- `WM_CHAR` (one per character) types into the focused text input; never triggers keybinds.
- `WM_KEYDOWN`/`WM_KEYUP` `VK_BACK` (scan 0x0E) deletes one character.
- `WM_KEYDOWN`/`WM_KEYUP` `VK_RETURN` (scan 0x1C) submits.
**Does not work inside KTG (session 4, 2026-10-28 14:50 UTC):** a bare `WM_CHAR` never reaches
`KTGInput`; per the player the text went to the chat box. The duty reacts to key presses: the first
physical key of every word triggers `KTGInput` refresh op 5 (input activation) before the character
appears, and Enter submits. Keybound Assist therefore sends real key presses with `SendInput`
(scan codes, Shift resolved per character through the game thread's layout via `VkKeyScanW`),
only while the game window is in the foreground.

## Open questions
- Where the per-enemy state lives (slot ↔ objId ↔ word list): `AddonKTGEWord` itself, or a duty
  director. Session 3 watches the addon memory.
- How Enter reaches the game (no callback seen); who sends the submit to the server.
- `Unk398` receives every keystroke too — which agent is it really?
- KTGHudLog op 2 `[17837..17840, 5347/5348]` → which sheet; word sheet name.
- Fever ("TYPING FEVER!") trigger; score formula (BONUS multiplier).
