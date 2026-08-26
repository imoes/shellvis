# Shellvis

[![version](https://img.shields.io/badge/version-0.2.2-blue)](https://github.com/imoes/shellvis/releases)
[![licence](https://img.shields.io/badge/licence-AGPL--3.0-green)](LICENSE)

A native Windows AI agent: a floating command bar with a console beneath it, wired to
PowerShell, the desktop, Office, Outlook, a browser and anything else on the machine.

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

**Remembers.** Skills for procedures, a bounded memory store for facts, both written by the
agent itself: after a turn that used tools, one extra tool-less model call asks whether
anything was learned worth keeping, and Shellvis writes it. Sessions persist to SQLite with
full-text search and survive being killed.

**Speaks and listens.** Push-to-talk dictation with on-device recognition — no cloud path
exists in the code. Recognition uses a local **Whisper** model, because the recogniser built
into Windows is not good enough to dictate with: on this machine it turned *"Welche Termine
liegen diese Woche an"* into *"Dänische Termine legen diese Woche an"*, while Whisper returns
it verbatim. Windows does expose a much better DNN engine, but only to WinRT, whose
free-form dictation goes through Microsoft's online service — so it is not an option for a
local-only feature.

The model is chosen **during setup** and downloaded once, to
`%LOCALAPPDATA%\Shellvis\Models`: `tiny` 74 MB, `base` 141 MB, **`small` 465 MB
(recommended)**, `medium` 1.5 GB, or none. It is deliberately not shipped inside the
installer — it would be paid for by everyone including those who never dictate, and it
exceeds GitHub's 100 MB asset limit. Until a model is present, dictation falls back to the
Windows engine rather than being unavailable, and the console says which one it is using.
Change it later with `voice.whisperModel` in `config.yaml`, or switch Whisper off entirely
with `voice.engine: sapi`.

### 76 tools

| Area | Tools |
|---|---|
| Desktop | `window_list` `window_focus` `desktop_analyze` `ui_click` `ui_set_text` `ui_send_keys` `ui_read_text` `screen_capture` `program_open` |
| Shell | `powershell_run` `powershell_modules_list` `powershell_module_import` `powershell_cmdlets_search` `powershell_cmdlet_help` |
| WSL | `wsl_distros` `wsl_run` `wsl_path` |
| Remoting | `remote_connect` `remote_run` `remote_sessions` `remote_disconnect` `remote_copy` |
| Gallery | `psgallery_search` `psgallery_info` `psgallery_install` `psgallery_installed` |
| Office (headless) | `word_create` `sheet_create` `slides_create` and four readers |
| Office (live) | `office_open_documents` `office_read_open` `office_export_pdf` |
| Outlook | `mail_list` `mail_read` `mail_reply_draft` `mail_compose_draft` `calendar_list` `contacts_find` |
| Thunderbird | five `mail_*` tools behind the same abstraction |
| Browser | 15 `browser_*` tools |
| Home Assistant | `ha_list_entities` `ha_get_state` `ha_list_services` `ha_call_service` |
| Skills & memory | `skills_list` `skill_view` `skill_manage` `memory` |
| Privileged | six `broker_*` tools, only when the service is installed |

Home Assistant appears only when a token is configured, and the broker tools only when the
service is actually listening. A tool that is offered is a promise; one that fails on first
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

Download an installer from [Releases](https://github.com/imoes/shellvis/releases). There
are two, and which one you want depends on whether the privileged broker should run on the
machine.

| | `-user.msi` | `-machine.msi` |
|---|---|---|
| Installs into | `%LOCALAPPDATA%\Programs\Shellvis` | `%ProgramFiles%\Shellvis` |
| Administrator rights | not needed | required |
| Broker service | no | selectable feature |
| Elevated tools | report as unavailable | available when the feature is installed |
| Start Menu entry, autostart | yes | yes |

**Why two files rather than one with a switch.** Windows Installer fixes the install scope
when the package is *built*: a package is either per-user or per-machine and nothing about
it can be changed while it runs. So the choice cannot be a checkbox inside one `.msi`. Both
are built from the same `install/Shellvis.wxs` with one preprocessor switch, so they cannot
drift apart.

Inside the machine-wide package the broker genuinely *is* optional — the application works
without it and says so — which is what a Windows Installer feature is for. Pick it in the
feature tree, or from the command line:

```powershell
# Everything, including the service. From an elevated prompt.
msiexec /i Shellvis-0.2.0-machine.msi ADDLOCAL=Application,BrokerService

# Application only, service left out.
msiexec /i Shellvis-0.2.0-machine.msi ADDLOCAL=Application

# Silent, with a log worth reading if it goes wrong.
msiexec /i Shellvis-0.2.0-user.msi /qn /l*v install.log
```

The packages contain no custom actions. The one thing that would have needed native code is
the installing user's SID, which the broker's pipe ACL grants: Windows Installer exposes the
user's *name* as `[LogonUser]` and never their SID, so the broker resolves the name itself.
A package with no custom actions cannot fail in a way that leaves a half-installed machine
behind.

### Building the installers

```powershell
dotnet tool install --global wix --version 5.*
wix extension add -g WixToolset.UI.wixext/5.0.2

# Publish all four executables into one folder, which is the layout they expect.
foreach ($p in 'src/Shellvis.Shell','src/Shellvis.Broker','src/Shellvis.Setup','src/Shellvis.Thunderbird.Host') {
    dotnet publish $p -c Release -r win-x64 --self-contained false -o artifacts/stage
}

wix build install/Shellvis.wxs -arch x64 -d Version=0.2.0 -d Stage="$PWD/artifacts/stage" `
    -bindpath "$PWD/install" -ext WixToolset.UI.wixext -d PerUser=1 `
    -o artifacts/Shellvis-0.2.0-user.msi

wix build install/Shellvis.wxs -arch x64 -d Version=0.2.0 -d Stage="$PWD/artifacts/stage" `
    -bindpath "$PWD/install" -ext WixToolset.UI.wixext `
    -o artifacts/Shellvis-0.2.0-machine.msi
```

**WiX 5, not 7.** Version 6 and later require accepting an Open Source Maintenance Fee
EULA before the toolset will build anything. WiX 5 produces the same packages under the
MS-RL and needs no such acceptance, so that is what the workflow pins.

Each package is about 105 MB, nearly all of it the PowerShell SDK. That is over GitHub's
100 MB per-file limit, so the installers are never committed — `.github/workflows/release.yml`
builds them on a `v*` tag and attaches them to the release.

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
| **Ctrl+Alt+D** | start and stop dictation |
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
ext/thunderbird-bridge/        the MailExtension
tools/
  Shellvis.DesktopProbe/       20 verification harnesses
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
- **No file tools yet.** `read_file`, `patch`, `search_files`, `web_search` and the meta
  tools are not built; files are reachable through PowerShell.
- **The `smart` permission mode is not implemented.** It is absent rather than aliased onto
  another mode, because a mode that silently behaves like a different one is worse than a
  missing one.
- **Markdown rendering covers a subset**: headings, lists, bold, italic, inline and fenced
  code, strikethrough. Tables, images and links are not rendered, and the model is told so.

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
