# Connectors

A connector teaches Shellvis to talk to a system it does not know: an internal REST API, a
ticket system, whatever your organisation runs. It is a directory with one YAML file in it.
No build, no plugin API, no code.

    %USERPROFILE%\.shellvis\connectors\
        my-thing\
            connector.yaml

Drop the directory there and the tools appear the next time Shellvis starts. `connector_list`
in a conversation shows what is installed and whether it works; `connector_install <path>`
copies a package in from somewhere else and asks first.

Shellvis ships two of them, [`atlassian-jira`](../connectors/atlassian-jira/connector.yaml)
and [`atlassian-confluence`](../connectors/atlassian-confluence/connector.yaml). They are
ordinary packages with no privileges of their own — read them as worked examples.

---

## A connector from nothing

Say there is an internal service at `https://status.example.com` that answers
`GET /api/incidents` with:

```json
{ "count": 2,
  "incidents": [
    { "id": "INC-14", "title": "mail is slow", "severity": "major", "opened": "2026-08-29" },
    { "id": "INC-15", "title": "vpn flaps",    "severity": "minor", "opened": "2026-08-28" }
  ] }
```

Create `%USERPROFILE%\.shellvis\connectors\status\connector.yaml`:

```yaml
name: status
kind: http
title: Incident status
description: The incidents the status service is tracking.

baseUrl: ${STATUS_URL}

auth:
  scheme: bearer
  secret: STATUS_TOKEN

tools:
  - name: incidents
    method: GET
    path: /api/incidents
    effect: read
    description: >
      The open incidents. Pass severity=major to see only the ones that matter.
    params:
      - name: severity
        in: query
        description: major, minor, or leave empty for all.
    result:
      items: incidents
      total: count
      line: "{id}  [{severity}]  {title}  (since {opened})"
      empty: "nothing is broken right now."
```

Then, once:

```powershell
setx STATUS_URL   https://status.example.com
setx STATUS_TOKEN <the token>
```

Restart Shellvis. The model now has a `status_incidents` tool, it runs without asking
because it is a GET, and its answer reads:

    2 result(s):
      INC-14  [major]  mail is slow  (since 2026-08-29)
      INC-15  [minor]  vpn flaps  (since 2026-08-28)

That is the whole exercise. Everything below is detail.

---

## The manifest, field by field

### The package

| Field | Meaning |
|---|---|
| `name` | Short key. Prefixes every tool name, so keep it short and stable. |
| `kind` | `http` for a declared REST API, `mcp` for an MCP server. |
| `title`, `description` | Human text, for the connector list and the install prompt. |
| `baseUrl` | Where the API lives. `${VAR}` is expanded when the connector starts. |
| `headers` | Sent on every call. `${VAR}` is expanded. |

### Authentication

```yaml
auth:
  scheme: basic          # none | basic | bearer | header
  userVar: THING_USER    # the NAME of a variable holding the user
  secret: THING_PASSWORD # the NAME the secret is looked up under
  headerName: X-Api-Key  # for scheme: header
```

Every field here holds a **name**, never a value. The secret is resolved as an environment
variable first and from the DPAPI secret store second — so exporting it for a whole shell
keeps working, and a stored value never silently overrides a variable somebody can see.

A connector whose secret is missing registers **no tools at all**, and `connector_list`
says which variable is missing. That is deliberate: tools that fail on first use are worse
than tools that are not there, because the model plans around them.

### Tools

```yaml
tools:
  - name: incidents        # the model sees status_incidents
    method: GET            # GET | POST | PUT | PATCH | DELETE
    path: /api/incidents/{id}
    effect: read           # read | write
    preview: id            # which argument the approval dialog shows
    description: >
      What it is for, in the words the model needs to decide when to call it.
    params: [...]
    headers: { X-ExperimentalApi: "true" }
    result: {...}
```

The description is the whole interface as far as the model is concerned. Write what the
tool is *for* and when to reach for it, not what it does mechanically. Rules that were
learned the hard way belong here — "use `statusCategory != Done`, the status names are
localised" is a sentence that prevents a class of failure.

### Parameters

```yaml
params:
  - name: jql              # what the model passes
    in: query              # query | path | body
    required: true         # refused locally if missing, before any request
    default: "25"          # used when the model leaves it out
    send: fields.summary   # the name the server wants, dotted for a nested body field
    description: ...
```

`in: path` fills `{name}` in the path. A placeholder nobody filled is caught before the
request goes out and named in the error, rather than becoming a puzzling 404.

`in: body` builds JSON. `send: fields.summary` nests it:

```json
{ "fields": { "summary": "..." } }
```

A value that begins with `{` or `[` is parsed as JSON rather than sent as a string, which
is what lets a manifest declare a structured field:

```yaml
- name: project
  in: body
  send: fields.project
  default: '{"key":"IMIT"}'
```

Everything is a string in the schema the model sees. A REST call sends text either way, and
a declared type would be one more thing that can disagree with the API.

### Results

```yaml
result:
  items: incidents   # the property holding the list; omit for a single object
  total: count       # where the server reports the full count, when this is a page
  line: "{id}  [{severity}]  {title}"
  empty: "nothing is broken right now."
```

`line` is a template over dotted paths: `{fields.status.name}` reaches into nested objects.
An unresolved placeholder renders as nothing rather than surviving into the answer.

**Only these three things are declarable, and that is the point.** The rules that make a
result readable are enforced centrally, where no package can opt out of them:

- the count comes before the content — *"40 of 340 result(s):"* tells the reader to narrow
  the search before they have read a line
- a truncation announces itself — a list that silently stops looks complete
- the id leads the line, because it is the argument every follow-up call takes
- nothing found is said in words, with the next step named

The last one is not fussiness. An empty result that reads as a failure invites the model to
fill it in, and this application has produced a calendar of six imaginary appointments
exactly that way.

If those rules cannot express a good answer for some API, that is a finding about the
format and worth reporting — the fix is a native tool for that one operation, with the
reason written in the code. It is not a per-package escape hatch.

### MCP connectors

```yaml
name: something
kind: mcp
command: npx
args: ["-y", "@example/mcp-server"]
```

An MCP connector is handed to the existing MCP host rather than run by the connector
runtime: the host already has stdio and Streamable HTTP, the tool namespacing, the
injection check and a scrubbed child environment, and a second implementation of any of
that would be a second set of security rules to keep in step. Configure it under
`mcpServers` in `config.yaml`.

---

## The rules a package cannot talk its way out of

A connector package is *content*. It can be downloaded, copied from a colleague or written
by a model, and everything in it lands somewhere privileged: descriptions go into the
system prompt, tool names into the model's vocabulary, paths into requests carrying your
credentials. So four rules are enforced by the loader, and no manifest can waive them.

**A manifest holding a credential is refused whole.** Not stripped — quietly removing part
of a file leaves something that still looks complete and teaches nobody. What is caught: a
literal value under `password`, `token`, `apiKey` and friends; a ready-made
`Authorization: Basic ...` header; a `https://user:password@host` url; and a value rather
than a name in `auth.secret`, caught by its shape, because names do not contain
punctuation.

**Only a GET can be read-only.** `effect: read` on a POST is a package describing its own
harmlessness, which is exactly what a malicious one would do. It is floored to *mutating* —
so it asks before every call — and the claim is reported rather than swallowed. Same
reasoning as MCP's `trustReadOnly` living in the local config instead of the server.

**A built-in tool always wins.** Tool names are prefixed with the connector, and a
collision is skipped rather than overwritten. No package can put itself in front of
`powershell_run`.

**Descriptions run through the injection check**, the same list of markers the MCP host
uses — "ignore previous", "do not tell the user", "without asking". A description lands in
the system prompt exactly as a remote tool's does, so a suspicious one is refused rather
than logged.

A connector that fails any of these is reported and skipped. One bad package never stops
the others from loading.

`connector_install` asks before it does anything, every time, and names the directory it is
about to copy — the manifest is a file you can open and read before you answer. It refuses a
package carrying a credential *before* the copy rather than after, so a refused package
never ends up sitting in the connectors directory waiting for the next start. There is
deliberately no "install from a url": fetching a package is a judgement about a source, and
this application has no way
to say anything true about a source. Installing from a directory you can open and read
leaves that judgement with the person who can make it.

---

## When it does not work

`connector_list` first. It distinguishes the three cases that look alike from outside:

| What it says | What it means |
|---|---|
| `not configured: set the X environment variable` | the package is fine, the machine is not set up |
| `refused: ...` | a security rule caught something; the sentence names it |
| `is not valid YAML at line N` | a typo, with the line |
| `no list at 'items'. It holds: ...` | the `result.items` path is wrong; the names that *were* there are listed |

A 401 names the variable whose value was rejected, rather than only the status code — the
difference between checking a setting and reading code.

---

## Checking your work

    probe connectors

runs the framework's own harness: the refusals, the read-only flooring, the shadowing rule,
the display rules, every shipped manifest, and a stub server speaking real Jira shapes to
check what actually leaves on the wire — the query, the headers, the body nesting. It needs
no network and no credentials.

It deliberately does **not** read your own connectors: it runs against a temporary home so
that one machine's setup cannot change the result. To check a manifest you wrote, install it
with `connector_install` and read what `connector_list` then says about it.
