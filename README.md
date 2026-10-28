# Keybound Assist

Dalamud plugin (API 15) for the Gold Saucer duty **Keybound Brawler**: types the words shown over
enemies at a human pace and never submits a wrong word, so a mistype can't cost the group a wave
or trigger a penalty.

> Third-party software, not affiliated with Square Enix. It sends keystrokes to the game, which
> is automation: self-distributed only, never for the official Dalamud repository. Don't discuss
> it in game.

## How it stays error-free and human

- Reads each enemy's current word from the `KTGEWord` slots and the input box from `KTGInput`.
- Types at a configurable WPM (capped at 154) with log-normal key timing, per-word drift, slower
  Shift/punctuation reaches, and a reaction delay before each word.
- Presses Enter only when the box equals (ignoring case) a word that is on screen at that moment.
- If another player takes the word mid-typing, carries the prefix over to a matching word or erases
  it with Backspace. It never presses Esc, which in this duty also quits.
- Sends real key presses (`SendInput`, Shift resolved through your keyboard layout), and only while
  the game window is in front; alt-tabbing pauses it. Words needing AltGr characters are skipped.
- Gaze casts ("Cursed Gaze", "Dynamic Sensory Jammer"): only starts a word it can finish and
  submit before the cast ends, erases otherwise, and presses no key for 5 s after the bar
  disappears (any key in that window stuns). If one of its own keys still gets stunned after a
  gaze, the hold grows to cover it. A stun after any other single cast adds that cast to the list.
- Stunned ("Inputs disabled!"): waits 7 s from the stun, wakes the box with one plain letter
  (it ignores Backspace after a stun), erases whatever was left, and carries on.
- Steps aside whenever you type yourself; a partial word left idle in the box is finished or cleared.
- Never gives up on a hiccup: a lost key is pressed again (twice), a swallowed Enter once, and
  anything else (or 6 s without progress) triggers a recovery that re-reads the box and continues.
  It warns in chat after 5 failed recoveries in a row and keeps trying; only a burst of internal
  errors disarms it.

Remaining risk: a word another player submits in the ~250 ms before ours reaches the server can
still count as a miss. The status window counts misses so this stays visible.

## Use

- `/kba`: status window · `/kba on` / `off`: arm / disarm · `/kba dry on|off` · `/kba config`
- Always loads **disarmed**, and **dry run** is on by default: it plans and shows every word and
  keystroke timing without sending keys.

## Install

Add the custom repository in `/xlsettings` → Experimental → Custom Plugin Repositories:

```
https://raw.githubusercontent.com/Tihlyn/KeyboundAssist/main/repo.json
```

## Build

```sh
dotnet build KeyboundAssist/KeyboundAssist.csproj -c Debug
```

Add `KeyboundAssist/bin/Debug/KeyboundAssist.dll` under `/xlsettings` → Experimental → Dev Plugin
Locations, then enable it in `/xlplugins` → Dev Tools.

## Releases

The version lives only in `Directory.Build.props`. From a clean `main` that matches `origin/main`:

```powershell
tools/release.ps1 -Bump patch        # or minor / major, or -Version 1.2.3; -DryRun to preview
```

It builds, runs the repository-tool tests, dates the `Unreleased` section of `CHANGELOG.md`,
commits `chore(release): vX.Y.Z`, tags it and pushes both. The Release workflow then builds,
publishes `latest.zip` on the GitHub release and updates `repo.json` on `main`.
A `testing-X.Y.Z.W` tag publishes a pre-release to the testing channel instead.
CI builds every push to `main` and every pull request.

## Research

`re/NOTES.md` holds the reverse-engineering notes (addons, opcodes, agent layout, gimmicks,
injection test). Raw captures in `re/captures/` contain player names; keep them local.
