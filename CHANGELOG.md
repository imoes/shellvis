# Changelog

New features and systemic changes only. Fixes, refactors and test work are in the commit
history, where they are recorded in full — repeating them here would stop this being a
readable summary of what changed for you.

## 0.8.0

- **Set up a schedule by saying so.** `cron_list`, `cron_add`, `cron_remove` and
  `cron_enable` replace hand-editing a JSON file and restarting. Every write asks first, in
  every permission mode, because a scheduled job is the one thing here that acts unattended,
  on a timer, indefinitely — and a scheduled run cannot create jobs at all.
- **A schedule is a real Windows task.** Shellvis registers it under `\Shellvis` in Task
  Scheduler, calling itself with `--job <name>`. It therefore fires whether or not Shellvis
  is open, it is visible and editable in a tool you already have, and a briefing due at
  eight still happens if the machine was restarted at seven. Schedules Windows cannot
  express exactly — a step, a list of hours, a day of the month — are refused rather than
  approximated: those stay on the scheduler inside Shellvis, and it says so when it happens.
- **Anything can ask Shellvis a question.** `Shellvis.Shell.exe --prompt "what is due today"`
  lands in the running conversation, without raising a window over what you are doing. If a
  Shellvis is already open the parameter is handed to *that* instance rather than starting a
  second one — otherwise the answer would appear in a window you are not looking at. The
  channel is a named pipe restricted to your own account.
- **The docked bar has the history button.** It was missing, which meant undocking, finding
  the conversation, and docking again. The bar grew by one button; the input field did not.

## 0.7.0

- **A desktop alert when something is worth knowing.** Bottom right, above the tray, the way
  Outlook's has worked for twenty years: it never takes the focus, so the keystroke you were
  typing still lands where you meant it; it leaves on its own after seven seconds, so
  ignoring it costs nothing; and clicking it opens the message window with the full report.
  It waits, along with everything else, while Windows says you are presenting, in a call or
  away — the console says which of those it is.
- **A scheduled run decides whether it is worth interrupting you for.** Most runs say
  nothing at all and only leave their line in the console: a routine result that raises an
  alert teaches you to ignore the next one, and then the one that mattered is lost with it.
  A run that did not finish always says so, because a scheduled task that quietly stops
  working looks exactly like a machine on which nothing is happening.

## 0.6.0

- **Shellvis learns an API from a YAML file.** A connector is one directory holding a
  `connector.yaml` that describes a REST API — its endpoints, their arguments, and how an
  answer should read. Drop it in `%USERPROFILE%\.shellvis\connectors` and its tools are
  there on the next start; no build, no plugin, no code. `connector_list` says what is
  installed and which variable a connector is still waiting for. A credential is never in
  the package: the manifest names a variable, and one that holds a value instead is
  refused rather than quietly cleaned up. [How to build one](docs/connectors.md).
- **Jira, the IT service desk and Confluence come with it.** Search issues, read one,
  see your open ones, work the service desk queues and — the part your own scripts never
  reached — read the SLA clocks, which is what answers "what runs out today". Commenting,
  moving an issue and creating one ask first, every time. Confluence is read-only on
  purpose.
- **The docked bar looks for a free spot on the taskbar** instead of sitting at a fixed
  offset from the right. It used to cover the app icons once enough windows were open,
  because Windows 11 grows the centred cluster outwards; now it measures the strip and
  tucks in beside Start, where the space does not move when you open something.

## 0.4.0

The release that turns Shellvis from something that operates a machine into something that
keeps your desk.

- **It asks instead of guessing.** When an answer is genuinely yours to give, Shellvis puts
  two to four options in front of you, each with a line saying what it costs, and you can
  always write something else. No answer is not a refusal: a dismissed question or a
  scheduled run means "decide yourself and say what you assumed", so it carries on rather
  than stopping.
- **Answers have links and tables.** A mail Shellvis mentions is a link you can click, and it
  opens that message in Outlook -- starting Outlook if it was not running. Tables render as
  real tables rather than arriving as raw pipes.
- **It reads mail in context.** The whole conversation a message belongs to, from your inbox
  and your sent items both, and the recent correspondence with one person in either
  direction. A suggested reply is written in the register you already use with them. It still
  cannot send: replies are drafts, and there is no send function to waive.
- **Your Outlook task list.** List what is open and what is overdue, add a task when a mail
  leaves you owing something, mark one done. Your own list, in the application you already
  look at, rather than a second one in here.
- **It keeps notes about people and dates.** What someone prefers, what you promised, when it
  falls due. They come back attached to the mail and calendar results that mention the
  person, so they surface when they matter instead of being remembered. Kept in their own
  database, never in the prompt.
- **Reminders and a daily briefing.** Scheduled, read-only, and each thing said exactly once
  before it happens -- a job every five minutes stays silent unless something is genuinely
  new. Two jobs are written into `jobs.json` on first run, switched off, so they can be
  turned on rather than typed out.
- **Sticky notes on the desktop.** The Windows Vista behaviour: a frameless note per window,
  five colours, dragged anywhere, resized from the edges, saved without asking, and back
  where you left it after a restart. No taskbar button and no Alt-Tab entry. Shellvis can
  write one for you.
- **Teams.** Open a chat with the message already written but unsent, and join the meeting on
  a calendar entry -- calendar lines now say `[Teams]` when there is one. Through the links
  Teams registers with Windows, so it needs no sign-in and no app registration.
- **Windows PowerShell 5.1 and background processes.** `powershell_run_winps` reaches the
  older engine for modules that will not load under 7. `process` starts something and lets go
  of it, so a build no longer blocks the conversation for four minutes.
- **The console shows what came back.** A tool call is a card now: worked or did not, what
  ran, what it was given, how long it took, and the full output one click away -- which was
  previously not reachable anywhere in the interface. The status line says what is happening
  ("Reading the mail...") rather than only that something is.
- **An assistant persona, shipped as a skill.** Triage before speaking, three things rather
  than thirty, look ahead rather than back, nothing dropped, write as they write, and say
  where every claim came from. Loaded only when the role is wanted.
- **An overview page.** <https://imoes.github.io/shellvis/>

## 0.2.8

- **Hosted speech recognition as an option.** `voice.engine: azure` or `voice.engine: google`
  recognises through that service — more accurate than the largest local model and faster than
  the smallest. **This is the one place where audio leaves the machine**, it is never reached
  by the default `auto`, and while it is in use the console says so at the start of every
  dictation. Keys go in the DPAPI secret store or in `AZURE_SPEECH_KEY` / `GOOGLE_SPEECH_KEY`,
  never in `config.yaml`. The README's previous claim that no cloud path exists in the code has
  been corrected rather than quietly left standing.

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
