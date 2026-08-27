# Changelog

New features and systemic changes only. Fixes, refactors and test work are in the commit
history, where they are recorded in full — repeating them here would stop this being a
readable summary of what changed for you.

## 0.2.7

- **Shellvis steps out of the way of full-screen and remote sessions.** It still floats above
  ordinary windows, but no longer sits on top of a remote desktop connection, a presentation
  or any window that covers a whole monitor. It stays exactly where it is and stays visible —
  it simply stops being above everything — and activating it, by hotkey or tray icon, puts it
  back in front immediately. Which windowed applications to yield to is configurable through
  `window.yieldTo` in `config.yaml`, defaulting to the Microsoft remote desktop clients.

## 0.2.5

- **Hold the space bar to talk.** While Shellvis has the focus, holding space opens the
  microphone and releasing it transcribes — real push-to-talk, so the recording lasts exactly
  as long as your hand says it does. A tap still types a space; the two are told apart by
  holding it past 400 ms.

## 0.2.4

- **Dictation recognises with a local Whisper model.** Markedly better than the recogniser
  built into Windows, and still entirely on this machine — no cloud path exists in the code.
  The model is chosen during setup (`tiny` 74 MB through `medium` 1.5 GB, or none) and
  downloaded once; until it is present, dictation falls back to the Windows engine rather
  than being unavailable. Changeable afterwards with `voice.whisperModel`, and switchable off
  entirely with `voice.engine: sapi`.

## 0.2.2

- **Automatic microphone gain.** A quiet microphone is brought up inside Shellvis' own
  capture chain rather than by changing the Windows recording level, which is shared with
  every other application — turning it up would have changed how you sound in Teams.

## 0.2.0

- First public release. MSI installers in two variants: per-user, needing no elevation, and
  machine-wide with an optional privileged broker service for elevated operations.
