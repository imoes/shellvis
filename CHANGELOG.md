# Changelog

New features and systemic changes only. Fixes, refactors and test work are in the commit
history, where they are recorded in full — repeating them here would stop this being a
readable summary of what changed for you.

## 0.9.9

- **Das Vorzimmer shows the state of the desk.** Unread mail, how much of it came from a
  person rather than from a system, ticket post, meeting requests, what is left today and
  what is overdue. A badge marks what has grown since you last opened it — never the total,
  and never a number that fell: a notice that appears when you have just tidied up is one
  nobody believes twice. The numbers are counted, not sorted, and the page says so — which
  mail needs an answer today is decided by reading it.

## 0.9.8

- **The rules your desk is kept by are on one page.** What gets sorted before anything is
  said, when you are interrupted, and what is never sent — in a window of its own, behind a
  button beside the answer. Not the browser: the page belongs to the application, and a
  reference you have to leave the application to read is one nobody reads.

## 0.9.6

- **Ask about a ticket and get a summary, with the link.** The answer used to be the comment
  thread verbatim — a wall of Wiki markup, which is what the ticket itself is for. It is now
  two or three sentences of what the newest comment decided or asked for, and the ticket key
  as a link so you can go and read the rest yourself. The link is carried through from the
  tool result and cannot be invented: the address of your installation is configuration and
  is never shown to the model.

## 0.9.5

- **A fenced code block wraps instead of scrolling sideways.** A long line used to run off
  the edge of the answer window with no way to see the end of it.

## 0.9.4

- **A ticket notification is a reason to read the ticket, not to summarise the mail.** When a
  Jira or service desk mail arrives, Shellvis fetches the issue and its newest comments and
  answers from those. The mail is almost entirely template; the one sentence that matters is
  a comment, and the ticket also holds everything that happened after the mail was sent.
- **Outlook is watched, and speaks up when it matters.** Four things now produce a
  notification of their own accord: an appointment about to start, a new mail worth
  interrupting you for, a meeting request, and a mail about a ticket. Silence is the normal
  answer — the question is put to the model, and most of the time nothing is worth saying.

## 0.9.3

- **Forward a mail with a comment.** Your note goes above the original text, which is carried
  into the forward rather than referred to.
- **Reply to everyone, or to one particular person.** Either a name or an address works; a
  name is resolved against the address book. The original mail flows into the reply, so an
  answer reads as an answer.
- **Create an appointment, with a Teams meeting.** Subject, time, a comment in the body, and
  attendees; ask for it and the meeting link is added. An appointment with attendees is saved
  as an unsent meeting and opened for you — the invitation goes out when you press Send, and
  not before. Nothing here sends anything: replies, forwards and new mail all become drafts.

## 0.9.2

- **The speech model is fetched during installation.** Dictation used to wait for a 74 MB
  download the first time you asked for it. The installer now offers to fetch it at the end,
  so the first thing you dictate works straight away.

## 0.9.1

- **A connector can be configured the moment the window is up.** The settings menu used to
  show "still starting..." where Jira and the service desk belong, and configuring one
  answered "try again in a moment" — on a freshly installed copy for long enough to walk
  into. Nothing about listing or configuring a connector needs the conversation to exist, so
  it no longer waits for one. Your stored settings were never affected.

## 0.9.0

- **The release is signed, once you have a certificate.** Signing is now part of building:
  the four executables and the installer are signed and timestamped, so a signature outlives
  the certificate that made it. Nothing is signed yet — that needs a certificate, and a
  self-signed one is no help because the issuer has to be trusted where the software runs.
  An internal PKI certificate covers your own machines; a public one covers the releases.
  Until then the build says "unsigned" and carries on rather than pretending.

## 0.8.9

- **One installer, and it asks who the installation is for.** There were two downloads,
  because the old belief was that Windows Installer fixes the scope when the package is
  built. It does not. There is now a single `.msi` with a page between the licence and the
  feature tree: into your profile without administrator rights, or into `%ProgramFiles%`
  with the privileged broker service as a selectable feature. In the first case the service
  is not merely unselected, it is absent — it cannot exist there.
- **On a managed desktop, choose "for everyone".** Not for the service: Defender's rule
  against unfamiliar executables refuses an unsigned program under a user-writable path, and
  your profile is one. Measured with the identical file — allowed from one location, blocked
  from the other, for you and for the system account both.

## 0.8.8

- **Ask about a period and get the period.** `mail_list` takes `since` and `until` — `7d`,
  `36h`, `2w`, `today`, or a date — instead of only a number of messages. Asked what
  happened last week it used to hand over the newest twenty, which in a busy folder is two
  days; the answer still said "last week" and nothing contradicted it.
- **A mail answer says how far it reached.** The header now gives how many matched, how many
  are shown and which span they actually cover, and says when older ones were left out. An
  empty result says whether the folder is empty or only the filter is.
- **Find mail by what it says.** `mail_search` looks through the inbox, its subfolders and
  sent mail, with an optional date window. It uses the Windows search index and, when that
  has nothing, reads the newest messages and compares them directly — and the answer says
  which of the two found it, so "nothing found" means both looked.

## 0.8.7

- **Shellvis lives on the taskbar's level.** It used to decide for itself when to get out of
  the way: every seven tenths of a second it measured the foreground window, asked Windows
  about presentations, and compared process names against a list. All three were guesses,
  and they were wrong in both directions — the bar vanished behind ordinary windows and
  still sat in front of a remote desktop session. It is now registered with Windows as a
  desktop toolbar and does what the shell tells it: visible exactly when the taskbar is,
  behind a full-screen application at the moment the taskbar goes too. It also says when it
  comes back, not only when it steps aside.
- **`window.yieldTo` is no longer read.** There is nothing left for a list of process names
  to correct. An older `config.yaml` still loads.

## 0.8.6

- **A ticket list has a summary, not the title twice.** Jira's "summary" field is the
  headline, so a column labelled *Zusammenfassung* that repeated it said nothing. The
  connectors now fetch the description as well and the list carries Ticket, Status, Titel and
  a real one-sentence summary of what the ticket is about.
- **The table fills the window.** Ticket lists stopped two thirds of the way across, with
  the last column wrapped to one word per line.

## 0.8.5

- **The service desk is its own product.** Jira and Jira Service Management are separate
  connectors on separate addresses, with their own tools — `servicedesk_queues`,
  `servicedesk_sla`, `servicedesk_my_open` and the rest — rather than one connector
  pretending both live in the same place. An installation where they *are* the same Jira
  enters the password once.
- **"My tickets" cannot be widened.** The account filter on `jira_my_open` and
  `servicedesk_my_open` is fixed rather than a default, because a default was overridable:
  the model passed a query of its own and the answer came back listing everybody's tickets.
  An open task counts as a ticket, which it previously did not.

## 0.8.4

- **The gear is in the console, next to the model.** It was on the bar, where it competed
  with the six buttons that do the work, and the connectors were listed under their product
  names with nothing saying they were connectors — so the report was that they were missing.

## 0.8.3

- **A settings button.** Connectors, the scheduler and the sticky notes were reachable only
  by saying the right sentence to the model, which means reachable only by somebody who
  already knew they were there. The menu makes nothing new possible; it makes it findable.
- **Your own open tickets, in one call.** `jira_my_open` is scoped to the configured account
  and needs no query written for it.

## 0.8.2

- **Connectors are configured in a dialog, not in the environment.** A connector that was
  present but unconfigured registered no tools and was therefore invisible; the only way to
  set it up was `setx` and a restart. Now the settings menu lists each one with its state and
  asks for the address, the account and the password. The password goes from the keyboard to
  the encrypted store and nowhere else — never through the model, never into `config.yaml`,
  because the conversation is kept on disk and searchable.

## 0.8.1

- **A schedule can be changed rather than rebuilt.** `cron_edit` alters an existing job's
  prompt, timing or name in place, instead of removing it and adding it back.

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
