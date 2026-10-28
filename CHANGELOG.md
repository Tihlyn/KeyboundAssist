# Changelog

Notable changes are recorded here using the Keep a Changelog format and semantic versioning.
`tools/release.ps1` dates the Unreleased section when it cuts a version.

## Unreleased

## [0.1.0] - 2026-10-28

### Added

- Typing assist for the Gold Saucer duty Keybound Brawler: reads every enemy's word from
  `KTGEWord`, types it with real key presses at a configurable human pace (log-normal key timing,
  per-word drift, reaction delay; capped at 154 WPM) and presses Enter only when the box equals a
  word that is on screen at that moment.
- Words taken by another player mid-typing are carried over to a word sharing the typed prefix,
  or erased with Backspace; Esc is never pressed (it also quits).
- Gaze casts ("Cursed Gaze", "Dynamic Sensory Jammer"): words are only started if they can be
  submitted before the cast ends, and no key is pressed for 5 s after the bar disappears. The hold
  grows if a stun still follows; a stun after another single cast adds that cast to the list.
- Stuns ("Inputs disabled!"): waits 7 s, wakes the box with one plain letter, erases what was left
  and carries on.
- Recovers instead of stopping: lost keys are pressed again, a swallowed Enter is retried, and
  anything else (or 6 s without progress) re-reads the box and continues. Yields to the player
  whenever they type.
- Status window (`/kba`), settings (`/kba config`), dry run (on by default); always loads disarmed.
