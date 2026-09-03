# Shellvis

[![version](https://img.shields.io/badge/version-0.9.3-blue)](https://github.com/imoes/shellvis/releases)
[![licence](https://img.shields.io/badge/licence-AGPL--3.0-green)](LICENSE)
[![changelog](https://img.shields.io/badge/changelog-CHANGELOG.md-lightgrey)](CHANGELOG.md)
[![build](https://github.com/imoes/shellvis/actions/workflows/build.yml/badge.svg)](https://github.com/imoes/shellvis/actions/workflows/build.yml)
[![installers](https://img.shields.io/github/v/release/imoes/shellvis?label=installers&color=orange)](https://github.com/imoes/shellvis/releases/latest)
[![site](https://img.shields.io/badge/overview-imoes.github.io%2Fshellvis-8957e5)](https://imoes.github.io/shellvis/)

A native Windows AI agent: a floating command bar with a console beneath it, wired to
PowerShell, the desktop, Office, Outlook, Teams, a browser and anything else on the machine.

**[What it is, on one page →](https://imoes.github.io/shellvis/)**

Shellvis does not wrap a terminal. It hosts PowerShell 7 in-process, drives the desktop
through UI Automation, talks to Office over COM, and shows every command and tool call it
makes while it makes them. The point is an agent that can actually operate a Windows
machine — and one you can watch doing it.

*Shellvis has entered the building.*

---

## What it does

**Runs the shell.** PowerShell 7 in one persistent runspace per session, so a variable set
in one turn is still there in the next. Modules can be searched, installed from the
PowerShell Gallery and imported, and the newly available cmdlets come back in the tool
result — so the model can use a module it installed a second ago without ten thousand
cmdlet names sitting in its prompt. WSL is reachable the same way.

**Drives the desktop.** Not screenshots and coordinates: `desktop_analyze` returns the UI
Automation tree of any window with a short reference for every element (`@e47`), and clicks,
text entry and key presses address those references. A coordinate is wrong the moment the
window moves; a reference either still resolves or fails loudly. Actions prefer UIA patterns
over synthetic input, so an invoke does not need the window in the foreground and cannot be
intercepted by whatever is under the cursor.

**Administers other machines.** PowerShell Remoting over WinRM (Kerberos, no file copy) or
over SSH, with persistent sessions — the supported replacement for PsExec, which worked by
dropping a service binary on the target's ADMIN$ share and is now treated as an attack by
every endpoint protection product for exactly that reason.

**Reads and writes documents.** Word, Excel and PowerPoint headless through OpenXML, and the
running instances through COM: read the document that is open, export to PDF, list what is
loaded. Outlook for mail, calendar, contacts and tasks — and Thunderbird through a native
messaging bridge for the same mail operations.

**Browses.** Chrome DevTools Protocol directly, no Node.js and no driver process. Pages come
back as a reference tree rather than an HTML dump; clicks are dispatched as trusted input
after a hit test, so a button behind a cookie banner is refused rather than clicked through.

**Extends itself.** MCP servers over stdio and Streamable HTTP; their tools land in the same
registry as the built-in ones and the agent loop cannot tell them apart. Skills are
progressive-disclosure instruction files. Hooks intercept tool calls. Cron runs unattended
jobs.

**Schedules itself, through Windows.** *"Every morning at eight, the day's appointments and
anything falling due"* is the whole setup — there is no dialog. Shellvis writes the job and
registers a real task under `\Shellvis` in Task Scheduler that calls itself with
`--job <name>`, so it fires whether or not Shellvis is open and is visible and editable in a
tool you already have. Ask *"which jobs are set up"* to see them with their prompts, their
next run and which scheduler owns the timing. Schedules Windows cannot express exactly — a
step in the minutes, a list of hours, a day of the month — are refused rather than
approximated and stay on the loop inside Shellvis, which says so when it happens. Every write
asks first in every permission mode, and a scheduled run cannot create jobs at all: a timer
that can add timers grows while nobody is looking.

**Sticky notes, by asking.** *"Stick a note on the desktop: call Weber back, 0151…"* or *"a
green note with the shopping list"*. Frameless, five colours, dragged anywhere, saved without
asking, and back where you left them after a restart. A line or two — longer text is refused
and goes into the notes database instead, because a sticky note is a reminder and not a
document. *"What is on the desktop"* lists what is already up. There is no button for them,
which is a real gap in discoverability rather than a design decision.

**Anything can ask it a question.** `Shellvis.Shell.exe --prompt "what is due today"` lands in
the conversation you already have open, without raising a window over what you are doing; a
Windows task uses `--job <name>` the same way. If an instance is already running the parameter
goes to *that* one rather than starting a second — otherwise the answer appears in a window
nobody is looking at, and a job run headless cannot raise its alert at all. The channel is a
named pipe whose ACL grants exactly the account that created it.

**Tells you, without interrupting you.** A scheduled run that finds something worth knowing
raises a desktop alert like Outlook's: bottom right, no focus taken, gone in seven seconds,
and a click opens the message window with the report. Most runs raise nothing — a run has to
say for itself that something matters, and the default is silence, because an alert for a
routine result teaches you to dismiss the next one unread. Everything waits while Windows
says you are presenting, in a full-screen call or away; `SHQueryUserNotificationState` is the
documented way to ask that, and the console says which of them it was.

**Remembers.** Skills for procedures, a bounded memory store for facts, both written by the
agent itself: after a turn that used tools, one extra tool-less model call asks whether
anything was learned worth keeping, and Shellvis writes it. Sessions persist to SQLite with
full-text search and survive being killed.

**Speaks and listens.** Push-to-talk dictation, on-device by default. Recognition uses a local
**Whisper** model, because the recogniser built
into Windows is not good enough to dictate with: on this machine it turned *"Welche Termine
liegen diese Woche an"* into *"Dänische Termine legen diese Woche an"*, while Whisper returns
it verbatim. Windows does expose a much better DNN engine, but only to WinRT, whose
free-form dictation goes through Microsoft's online service — so it is not an option for a
local-only feature.

The model is chosen **during setup** and downloaded once, to
`%LOCALAPPDATA%\Shellvis\Models`: `tiny` 74 MB, `base` 141 MB, `small` 465 MB,
**`medium` 1.5 GB (recommended)**, or none. It is deliberately not shipped *inside* the
installer — it would be paid for by everyone including those who never dictate, and it
exceeds GitHub's 100 MB asset limit — but the installer **fetches** it: a tick box on the
last page, on by default, downloads it as the last thing the installation does.

That runs *after* the transaction rather than inside it, and for a reason worth knowing: the
model belongs to a person, not to a machine. A step inside the install would run as
LocalSystem or as whoever elevated it, and on a managed desktop that is often not the person
about to dictate — so the file would land in a profile nobody uses. It also detaches, so a
download over a slow link is a console window you can close rather than a progress bar the
installer cannot explain.

Skip the box, or install silently, and nothing is lost: Shellvis fetches the model the first
time you dictate. `Shellvis.Setup.exe --fetch-model` does it on demand, which is also the way
to have it ready before travelling. It is idempotent — with the model already present it says
so and downloads nothing. Until a model is there, dictation falls back to the
Windows engine rather than being unavailable, and the console says which one it is using.
Change it later with `voice.whisperModel` in `config.yaml`, or switch Whisper off entirely
with `voice.engine: sapi`.

**Hosted recognition, if you want it.** `voice.engine: azure` or `voice.engine: google` sends
each recording to that service instead. Both are more accurate than the largest local model
and faster than the smallest, which is a combination no local model offers today: `medium`
measured 11.5 seconds for three seconds of speech on this machine against `small`'s 3.8 --
and `medium` is the default because `small` guesses German word endings, turning "diese
Woche noch an" into "diese Woche nach dem".

**This is the one place where audio leaves the machine, and it is off by default.** `auto`
never reaches it — a hosted provider has to be named explicitly, and a key has to be present.
When one is in use the console says so when it is picked up and again at the start of every
dictation, and the sentence "recognition runs on this machine" is not printed. Keys live in
the DPAPI secret store or in `AZURE_SPEECH_KEY` / `GOOGLE_SPEECH_KEY`, never in `config.yaml`.
Azure also needs `voice.azureRegion`, because its speech endpoint is per-region.

### 106 tools

| Area | Tools |
|---|---|
| Desktop | `window_list` `window_focus` `desktop_analyze` `ui_click` `ui_set_text` `ui_send_keys` `ui_read_text` `screen_capture` `program_open` |
| Shell | `powershell_run` `powershell_run_winps` `process` `powershell_modules_list` `powershell_module_import` `powershell_cmdlets_search` `powershell_cmdlet_help` |
| WSL | `wsl_distros` `wsl_run` `wsl_path` |
| Remoting | `remote_connect` `remote_run` `remote_sessions` `remote_disconnect` `remote_copy` |
| Gallery | `psgallery_search` `psgallery_info` `psgallery_install` `psgallery_installed` |
| Office (headless) | `word_create` `sheet_create` `slides_create` and four readers |
| Office (live) | `office_open_documents` `office_read_open` `office_export_pdf` |
| Outlook | `mail_list` `mail_search` `mail_read` `mail_open` `mail_thread` `mail_history` `mail_reply_draft` `mail_forward_draft` `mail_compose_draft` `calendar_list` `calendar_create` `contacts_find` |
| Tasks | `task_list` `task_create` `task_complete` |
| Teams | `teams_chat_open` `teams_meeting_join` |
| Assistant | `agenda_due` `agenda_today` `note_add` `note_search` `note_due` `note_close` `note_stick` `note_stickies` |
| Thunderbird | five `mail_*` tools behind the same abstraction |
| Browser | 15 `browser_*` tools |
| Home Assistant | `ha_list_entities` `ha_get_state` `ha_list_services` `ha_call_service` |
| Scheduler | `cron_list` `cron_add` `cron_edit` `cron_remove` `cron_enable` — each write registers or updates a real Windows task |
| Connectors | `connector_list` `connector_install`, plus whatever the installed packages add |
| Skills & memory | `skills_list` `skill_view` `skill_manage` `memory` |
| Asking | `clarify` |
| Privileged | six `broker_*` tools, only when the service is installed |

**Connectors** are how the list grows without a rebuild. A connector is one directory
holding a `connector.yaml` that declares a REST API — endpoints, arguments, and how an
answer should read. Drop it in `%USERPROFILE%\.shellvis\connectors` and its tools are there
on the next start. Credentials are named, never held; only a `GET` can run without asking;
and no package can shadow a built-in tool. Three ship with Shellvis: a self-hosted Jira,
its service desk, and Confluence. **[How to build one →](docs/connectors.md)**

Eighty-seven of them on a machine with no Home Assistant, no privileged service and no
Thunderbird. Home Assistant appears only when a token is configured, and the broker tools
only when the service is actually listening. A tool that is offered is a promise; one that fails on first
use costs a round and reads as a broken agent rather than as an unconfigured integration.

---

## Permissions

Four things decide whether an action asks first.

**The tool's declaration** is a ceiling: read-only, mutating, or always-ask.

**The mode**, on the chip in the bar and in `config.yaml`:

| Mode | Behaviour |
|---|---|
| `ask` | everything that is not provably a read asks, shell queries included |
| `auto-read` | **default.** Provable reads run silently, changes ask |
| `yolo` | nothing asks — except always-ask tools |

**The classifier** may lower a specific call. `powershell_run` has to be declared mutating
because it can do anything, but most real calls are queries; prompting for every
`Get-CimInstance` teaches people to click Allow without reading, which is worse than not
asking. Three independent signals must agree — verb taxonomy with a noun exception table
(`Format-Table` reads, `Format-Volume` destroys a disk), AST analysis for redirection,
provider-path assignment and dynamic invocation, and command shape — and NFKC normalisation
runs first against homoglyph evasion. The burden of proof is on "read": there is no
"probably harmless".

**Always-ask is never waived**, in any mode. Gallery installs, privileged broker calls,
arbitrary script in a logged-in browser page, and any write on a remote machine. For the
last three the difference is not what they do but where they run.

Scheduled runs refuse every approval. At three in the morning there is nobody to answer, and
a gate that allowed instead would mean an unattended agent running `Remove-Item -Recurse`
because a model decided to.

---

## Requirements

| | |
|---|---|
| Windows | 10 build 17763 or newer; developed on 11 25H2 (26200) |
| .NET SDK | 10.0.400 |
| Windows App Runtime | 2.4.0 — install from Microsoft if absent |
| A model endpoint | any OpenAI-compatible URL, or one of 19 catalogued providers |

No Node.js, no Python, no external browser driver. Office and Outlook features need Office
installed; everything else degrades to a clear message rather than a failure.

---

## Build

```powershell
git clone https://github.com/imoes/shellvis.git
cd shellvis
dotnet build Shellvis.slnx
```

Run it straight from the build:

```powershell
.\src\Shellvis.Shell\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Shellvis.Shell.exe
```

Shellvis runs **unpackaged** against the installed Windows App Runtime. Starting as a
packaged app would require Developer Mode, which is an HKLM policy and needs administrator
rights; unpackaged also matches the per-user install below.

### A build detail worth knowing

`Microsoft.PowerShell.SDK` ships its modules under `runtimes/<rid>/lib/<tfm>/Modules`, and
the engine looks for them next to `System.Management.Automation.dll`. Any build with a
`RuntimeIdentifier` — which every WinUI build has — flattens the assemblies into the output
root and leaves `Modules` behind, so the two halves end up in different places.
`Directory.Build.targets` copies the folder into place. Without it `Get-Module` works while
`Out-String` and `Get-CimInstance` fail, which reads like a broken installation rather than a
layout problem.

---

## Install

**➜ [Download the installer](https://github.com/imoes/shellvis/releases/latest)** — one
package, attached to every release and built on the tag by
[`.github/workflows/release.yml`](.github/workflows/release.yml) rather than uploaded by
hand, so what you download is what the tag contains.

**The choice is a page in the installer**, between the licence and the feature tree:

| | Just for me | For everyone on this machine |
|---|---|---|
| Installs into | `%LOCALAPPDATA%\Programs\Shellvis` | `%ProgramFiles%\Shellvis` |
| Administrator rights | not needed | required once |
| Broker service | not offered at all | selectable feature |
| Elevated tools | report as unavailable | available when the feature is installed |
| Start Menu entry, autostart | yes | yes |

Either way your settings, notes and sessions live in `%USERPROFILE%\.shellvis` and are
untouched by installing or uninstalling.

**This used to be two downloads, and the reason given was wrong.** The README said Windows
Installer fixes the install scope when the package is *built*, so the choice could not be a
page inside one `.msi`. MSI 4.5 has *Single Package Authoring*: with `ALLUSERS=2` and
`MSIINSTALLPERUSER` the same package installs either way, and the scope page sets it. The
package is now built once with `Scope="perUserOrMachine"`, which puts exactly those two
properties in it.

**On a managed desktop, pick "for everyone".** Not for the service — because otherwise
Shellvis may not start at all. Microsoft Defender's rule *"block executable files from
running unless they meet a prevalence, age, or trusted list criterion"* refuses an unsigned
binary under a user-writable path, and `%LOCALAPPDATA%` is one. Measured on a managed
machine with a **byte-identical** executable: allowed from a fixed path, blocked from
`%LOCALAPPDATA%\Programs\Shellvis`, for the user and for `SYSTEM` both, with Defender event
1121 naming rule `01443614-CD74-433A-B99E-2ECDC07BFC25`. `%ProgramFiles%` is not
user-writable and is outside what that rule looks at.

### Signing

**The releases are not signed yet, and it is the release step that is waiting, not the
build.** [`install/Sign.ps1`](install/Sign.ps1) signs the four executables and the package,
timestamps them (RFC 3161, so a signature outlives the certificate), and the release
workflow calls it twice: once before packaging, because the executables inside are what
Windows checks when they start, and once after, because the signature on the `.msi` is what
a user sees in the UAC prompt. Verified end to end with a throwaway certificate — an `.exe`
and an `.msi` both came away with a real signature and a DigiCert timestamp.

What is missing is a certificate, and the kind matters:

- **Self-signed does not help.** The signature is written and no machine trusts the issuer,
  so nothing that refuses an unsigned binary is any happier. The script reports that state
  as exactly that rather than as success.
- **An internal PKI certificate** is the answer for software distributed inside one
  organisation: the domain already trusts its own root, so every member machine sees a
  trusted publisher. It needs a Code Signing template published on the issuing CA and
  enrolment permission.
- **A public certificate** is the answer for the GitHub releases. An OV certificate builds
  reputation over weeks; an EV certificate or Azure Trusted Signing carries it immediately.

Configure it with `SHELLVIS_SIGN_THUMBPRINT` for a certificate in a store, or
`SHELLVIS_SIGN_PFX` and `SHELLVIS_SIGN_PFX_PASSWORD` for a file; in Actions the secrets are
`SIGN_THUMBPRINT`, `SIGN_PFX_BASE64` and `SIGN_PFX_PASSWORD`. With none of them set the
build says the files are unsigned and carries on, which is how the public CI works. With
one of them set the step *requires* a valid signature, so a pipeline that means to sign
fails rather than shipping a package that only looks signed.

The broker genuinely is optional inside a machine-wide install — the application works
without it and says so, which is what a Windows Installer feature is for. Pick it in the
feature tree, or from the command line:

```powershell
# Machine-wide with the service. From an elevated prompt: a silent install
# cannot ask for elevation, so it has to start elevated.
msiexec /i Shellvis-VERSION.msi ALLUSERS=1 MSIINSTALLPERUSER="" `
    ADDLOCAL=Application,BrokerService

# Machine-wide, service left out.
msiexec /i Shellvis-VERSION.msi ALLUSERS=1 MSIINSTALLPERUSER="" ADDLOCAL=Application

# Just for me, silent, with a log worth reading if it goes wrong.
msiexec /i Shellvis-VERSION.msi /qn /l*v install.log

# The speech model, answered without the page appearing.
msiexec /i Shellvis-VERSION.msi WHISPERMODEL=medium
```

`MSIINSTALLPERUSER=""` is what a silent install needs and the page sets for you; passing
`ALLUSERS=1` alone leaves the package in per-user mode, because that property stays at 2 by
design and `MSIINSTALLPERUSER` is the switch.

**No code of ours runs during an installation.** The three custom actions in the package are
all `SetProperty`: they write a property and nothing else. The one thing that would have
needed real code is the installing user's SID, which the broker's pipe ACL grants: Windows
Installer exposes the user's *name* as `[LogonUser]` and never their SID, so the broker
resolves the name itself. A package that only sets properties cannot fail in a way that
leaves a half-installed machine behind.

### Building the installer

```powershell
dotnet tool install --global wix --version 5.*
wix extension add -g WixToolset.UI.wixext/5.0.2

# Publish all four executables into one folder, which is the layout they expect.
foreach ($p in 'src/Shellvis.Shell','src/Shellvis.Broker','src/Shellvis.Setup','src/Shellvis.Thunderbird.Host') {
    dotnet publish $p -c Release -r win-x64 --self-contained false -o artifacts/stage
}

# One build. There is no scope switch any more: the package carries both.
wix build install/Shellvis.wxs -arch x64 -d Version=0.9.2 -d Stage="$PWD/artifacts/stage" `
    -bindpath "$PWD/install" -ext WixToolset.UI.wixext `
    -o artifacts/Shellvis-0.9.2.msi
```

**WiX 5, not 7.** Version 6 and later require accepting an Open Source Maintenance Fee
EULA before the toolset will build anything. WiX 5 produces the same packages under the
MS-RL and needs no such acceptance, so that is what the workflow pins.

The package is about 108 MB, nearly all of it the PowerShell SDK. That is over GitHub's
100 MB per-file limit, so the installer is never committed. It is built instead:

| Workflow | Runs on | Produces |
|---|---|---|
| [`build.yml`](.github/workflows/build.yml) | every push and pull request to `main` | nothing — it compiles and runs the harnesses, as a gate |
| [`release.yml`](.github/workflows/release.yml) | a `v*` tag, or by hand | both `.msi` files, attached to the release and kept as a run artefact for 14 days |

**Why packaging only happens on a tag.** Two 108 MB files per commit is storage and minutes
spent on an output nobody asked for, while what a push needs is a fast answer about whether
the thing still builds. `workflow_dispatch` covers the case where you want the packages
without cutting a release.

**Why the version comes from `Directory.Build.props` and not the tag.** So the number in the
package, in the file name and in the application cannot disagree. A tag that does not match
fails the build rather than being reconciled silently — whichever of the two is wrong,
shipping a package labelled with the other makes the version useless.

The runner has no Office, no VPN and no live desktop, so the harnesses that need those are
deliberately not part of the gate. What runs there is listed in the workflow; the full
twenty-two are a local `probe` sweep.

### Without an installer

The console installer is still there, and it is the only way to register the service
without an MSI:

```powershell
.\Shellvis.Setup.exe --mode user      # no administrator rights
.\Shellvis.Setup.exe --mode service   # elevated; registers the broker
.\Shellvis.Setup.exe --status
.\Shellvis.Setup.exe --uninstall user
```

Uninstalling by either route leaves configuration and history in place — reinstalling is the
commonest reason to uninstall.

The broker's named pipe grants exactly two identities, the installing user and the local
Administrators group, and denies `NETWORK`. A permissive pipe served by a LocalSystem
process is a privilege-escalation service with a friendly name.

## Using it

| | |
|---|---|
| **Ctrl+Alt+Space** | bring the bar to the front and focus the input |
| **Ctrl+Alt+D** | start and stop dictation (a toggle, works from anywhere) |
| **Hold Space** | push-to-talk while Shellvis has focus: hold to listen, release to transcribe. A tap still types a space — what tells them apart is holding it past 400 ms |
| Tray icon | left click shows, right click opens the menu |
| Chevron on the bar | open and close the console |
| Chip on the bar | permission mode |
| Model name in the console header | switch provider and model |
| Minimise | dock the bar onto the taskbar strip |

The hotkey is not a convenience. Windows refuses `SetForegroundWindow` to any process that
does not already own the foreground, so a hotkey handler is the only place the bar can raise
itself from.

Drag either surface by its blank part to move the window.

---

## Configuration

`%USERPROFILE%\.shellvis\config.yaml`, created with comments on first run.

```yaml
model:
  provider: openai          # or any of 19, or a name from providers: below
  model: gpt-5

agent:
  maxIterations: 30
  stream: true
  requestTimeoutSeconds: 300
  stallTimeoutSeconds: 300
  learnFromTurns: true      # the post-turn reflection

approvals:
  mode: auto-read           # ask | auto-read | yolo

# Override a built-in provider field by field, or define a new one.
providers:
  work-gateway:
    name: Company Gateway
    baseUrl: gateway.example.com              # https:// and /v1 are added for you
    apiKeyEnvVar: WORK_LLM_KEY
    defaultModel: gpt-4o

mcpServers:
  filesystem:
    transport: stdio
    command: npx
    args: ['-y', '@modelcontextprotocol/server-filesystem', 'D:\work']
```

**Windows paths go in single quotes or none.** In double-quoted YAML a backslash is an
escape, so `"C:\Users\..."` fails to parse on `\U` and takes the whole file down with it.

**`${VAR}` references stay literal when Shellvis rewrites the file.** Interpolation happens
on read so a key never sits in the file, and the file is rewritten whenever a setting
changes. A naive round-trip would write the resolved value — one such write would copy every
referenced secret in clear text into a file people treat as harmless.

API keys can also be entered in the provider dialog, where they are stored DPAPI-encrypted
under `%USERPROFILE%\.shellvis\secrets` and never written to the config. An environment
variable, when set, always wins: someone who exported a key for their whole shell expects
that to be the key in use.

---

## Layout

```
src/
  Shellvis.Core/       agent loop, tools, providers, skills, memory, sessions  (no UI)
  Shellvis.Shell/      the WinUI 3 bar and console
  Shellvis.Broker/     LocalSystem Windows service, privileged operations only
  Shellvis.Contracts/  IPC DTOs
  Shellvis.Setup/      single-file installer
  Shellvis.Thunderbird.Host/   native messaging relay
connectors/            declarative API packages, read in place beside the executable
skills/                the skills that ship with the product, likewise
ext/thunderbird-bridge/        the MailExtension
tools/
  Shellvis.DesktopProbe/       41 verification harnesses
  Shellvis.TestMcpServer/      a deliberately hostile MCP server
```

`Shellvis.Core` has no UI dependency. The agent loop yields events rather than returning an
answer, because the interesting part of an agent turn is what happens during it.

---

## Verification

There are no unit tests. There are harnesses that drive the real thing:

```powershell
$probe = ".\tools\Shellvis.DesktopProbe\bin\Debug\net10.0-windows10.0.26100.0\Shellvis.DesktopProbe.exe"

& $probe classify      # the read-only classifier, 30 cases
& $probe config        # config round-trip; a referenced secret must not reach the file
& $probe skills        # progressive disclosure; skill bodies must not reach the prompt
& $probe sessions      # persistence, FTS5 against real punctuation, lineage
& $probe compaction    # never orphans a tool call
& $probe reflect       # the post-turn reflection, without a model
& $probe launch        # unusable URIs refused before the shell
& $probe endpoint      # a bare host becomes a working API root
& $probe remote        # PowerShell Remoting against a real listener
& $probe audiobridge   # the dictation audio bridge, compared against a wave file
& $probe whisper       # local Whisper, measured against the Windows engine
& $probe mic [seconds]  # which capture device Windows hands over, and what it hears
                       #   --fetch also downloads the configured model
& $probe browser       # a real Chromium against a real page
& $probe broker        # the pipe protocol and every guard
& $probe hooks $probe cron $probe mcp $probe hass $probe providers
& $probe office $probe officelive $probe outlook $probe voice $probe stream
```

They test properties rather than implementations. `compaction` does not check indices, it
checks the invariant the provider enforces — every tool call has its result and every result
its call — and walks a tool exchange across the boundary where a naive "keep the last N
messages" breaks. `config` writes a real secret-shaped value and asserts it is absent from
the file. `mcp` runs against an MCP server built to violate all three trust boundaries.

The desktop harnesses need a live desktop; `agent` needs a reachable endpoint.

---

## Known limitations

Stated rather than discovered later.

- **The privileged service is untested.** The development machine grants no administrator
  rights, so the pipe protocol, its ACL and every guard are verified with the broker running
  as a console process in the user context. Session 0, LocalSystem and autostart are not --
  and neither is the machine-wide MSI, for the same reason. The per-user MSI is verified end
  to end: installed, 450 files and the right version in place, shortcut and autostart
  created, then uninstalled with nothing left behind.
- **Remote sessions to a live host are untested.** The transport, the failure messages and
  the risk classification are; loopback remoting needs `Enable-PSRemoting`, which needs
  administrator rights.
- **The Thunderbird extension's JavaScript is untested.** The framing and the relay are
  verified from both sides; the extension itself needs Thunderbird installed.
- **CDP cannot attach to your existing browser profile.** Chrome and Edge have refused
  remote debugging on the default profile since version 136, silently — the port simply does
  not open. Shellvis brings its own persistent profile; sign in there once.
- **Anthropic and Gemini go through their OpenAI-compatible endpoints**, not the native
  APIs. That costs prompt caching and thinking blocks.
- **No file tools yet.** `read_file`, `patch`, `search_files` and `web_search` are not
  built; files are reachable through PowerShell. `clarify` is.
- **The `smart` permission mode is not implemented.** It is absent rather than aliased onto
  another mode, because a mode that silently behaves like a different one is worse than a
  missing one.
- **Markdown rendering covers a subset**: headings, lists, bold, italic, inline and fenced
  code, strikethrough, links and GitHub-style tables. Images and block quotes are not
  rendered, and the model is told exactly which subset it has.
- **Teams goes through deep links, not Microsoft Graph.** That covers opening a chat with
  the message written but unsent, and joining the meeting on a calendar entry. Reading chats
  and presence would need an app registration in your own tenant, which in most
  organisations is a request rather than a setting.

---

## Design notes

Four decisions that explain most of the code.

**The window is always full height.** Expanding the console does not resize it; it grows the
panel and widens a `SetWindowRgn` clipping region. Resizing a borderless always-on-top
window every frame tears and fights the compositor. The region also defines the hit-test
area, which is why a collapsed console cannot be clicked.

**The agent loop drives tool calling itself** rather than using the auto-invoking chat
client. That wrapper runs every tool the model asks for with no chance to intervene, which is
incompatible with an approval gate. Owning the loop is what makes "ask before writing"
possible at all.

**Tool failures are text, not exceptions.** A model that gets a readable error corrects
itself on the next round; one that gets an exception sees a dead tool. Ambiguity returns the
candidate list rather than a guess.

**How a tool result is formatted is part of its API.** `WindowInfo.ToString` once produced
`Editor - Notepad`, the model read it as one title, and the turn cost two extra rounds. An
ambiguous string costs money exactly like a wrong schema.

---

## Versioning

The version lives in `Directory.Build.props` and nowhere else, so the shell, the broker,
the installer and the probe always report the same one. It is read back at runtime from
the assembly rather than written down twice.

It is shown in three places on purpose: beside the greeting in the console, in the tray
tooltip, and in `Shellvis.Setup --status`. This project has lost time twice to running
against a stale binary, and a visible version turns that from an invisible problem into an
obvious one.

Raise it with every change set -- patch for fixes, minor for new tools or capabilities --
and tag the commit `vX.Y.Z`. Still 0.x deliberately: see Known limitations.

## Licence

GNU Affero General Public License v3.0 — see [LICENSE](LICENSE).

Worth knowing what the "Affero" part adds to the GPL: if you modify Shellvis and let other
people use it **over a network**, you have to offer them the source of your modified version.
For a desktop agent that mostly does not arise, but the broker service and the MCP surface
are network interfaces, so it can.
