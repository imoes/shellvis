---
name: jira
description: >-
  Work a self-hosted Jira and IT service desk. An open TASK is a ticket, so "my tickets"
  means every open issue assigned to the user, of every type. Use jira_my_open for that,
  never a hand-written assignee clause.
requires_tools:
  - jira_search
  - jira_issue
---

## "My tickets" means every open issue assigned to me

Read this before answering any question about the user's own work.

**An open task is a ticket.** So is a bug, a service request, a change, and anything else the
workflow calls an issue. There is no type worth excluding, and filtering by type is how a
list of "my tickets" quietly loses half of them. When the user asks what is open for them,
they mean everything with their name on it that is not finished.

**Use `jira_my_open`.** It is scoped to the configured account and the scope cannot be
changed from a tool call — deliberately, because the earlier version let a JQL be passed in
and the answer came back listing everybody's tickets. Do not use `jira_search` with an
assignee clause of your own; do not pass a JQL to `jira_my_open`.

**Both systems.** Jira and the service desk are separate products on separate hosts. "My
tickets" spans both: call `jira_my_open` *and* `servicedesk_my_open`, and say which came from
where. Answering from one of them is answering half the question.

### How to present a list of tickets

Always a table, always these columns, always in this order:

| Ticket | Status | Titel | Zusammenfassung |
|---|---|---|---|
| [JCUE-5915](https://example/browse/JCUE-5915) | In Arbeit | Massive CUE-Performanceprobleme | Seit dem Update auf 3.4 braucht der Seitenaufbau über 20 s; betrifft die ganze Redaktion, Workaround ist der alte Client. |

Not a bullet list. A list of sixteen tickets with the status and priority folded into
parentheses is unreadable and cannot be scanned down one column, which is the entire reason
to show a list at all. The shape above is what the reader asked for; do not improvise
another.

**Titel and Zusammenfassung are two different fields, and this is the mistake to avoid.**
Jira's `summary` is the *title* — one line, usually typed in a hurry by whoever raised the
ticket. Repeating it under the heading "Zusammenfassung" tells the reader nothing they could
not read one column to the left. The summary is written from the **description**, which the
tools now return as `about:` on each line:

- **Titel** is the `title:` part, verbatim.
- **Zusammenfassung** is *your* one or two sentences from the `about:` part: what is actually
  wrong or wanted, and for whom. Condense it — do not paste the wiki markup back out, and do
  not simply cut the description off mid-word.
- If a ticket has no description at all, the Zusammenfassung is a dash. Say nothing rather
  than inventing a plausible sentence; a made-up summary of somebody's ticket is worse than
  an empty cell.

Everything else about the shape:

- **The key is a link, and the tools already made it one.** They return
  `[JCUE-5915](https://…/browse/JCUE-5915)`; put that in the Ticket column exactly as it came.
  Never retype a key as plain text, never drop the brackets, and never invent a URL for one
  the tools did not give you. A ticket number that cannot be opened makes the reader search
  for it by hand, which is most of the work the list was supposed to save.
- **A missing value is a dash**, not an empty cell and not the word "null".
- **Escape any `|` inside a cell as `\|`**, or the row breaks into the wrong columns.
- Priority and due date are worth a sentence under the table when they matter — overdue and
  `Kritisch` first — rather than two more columns that squeeze the summary to nothing.
- Group by status with a heading and a count above each table when there are more than about
  ten, and say the total first.

# Working Jira and the service desk

These rules are not style. Each one is here because the obvious approach fails against a
self-hosted Jira, and every failure below has actually happened.

## Searching

**Never filter on a status name.** Status names are localised and workflow-specific: what
is "Done" in one project is "Erledigt", "Abgeschlossen" or "Geschlossen" in another, and a
query naming the wrong one returns nothing while looking perfectly valid. Filter on the
category instead:

    statusCategory != Done

**Never use `=` on a summary.** Subjects carry colons, and a colon in an `=` comparison is
a JQL syntax error, so the search comes back 400 and the ticket looks absent:

    summary ~ "printer"        not   summary = "printer: not working"

**Ask for the fields you need and no more.** Every extra field is a longer answer that
crowds out the one line that mattered.

A search that returns nothing has answered. It means no issue matches, not that something
went wrong — say so and stop. If the result surprises you, widen the query rather than
concluding the system is broken.

## A mail about a ticket: go and read the ticket

A Jira or service desk notification is almost entirely template. The one sentence that
matters is a comment, and that comment is already in the ticket — along with everything that
happened after the mail was sent. So a notification is a **trigger**, not a source.

1. Take the key from the **subject**. It is there in brackets, and the subject is where it
   means "this mail is about that ticket"; a notification body also names related issues,
   footers and links.
2. `jira_issue` for the state: status, priority, assignee, due date.
3. `jira_comments` for the story. Newest first, five is usually enough.
4. Then answer in this shape, and in this order:
   - **the ticket, as a link** — `[JCUE-5915](https://…/browse/JCUE-5915)` with its title
   - **what changed** — the newest comment, in your own words, and who wrote it
   - **where the ticket stands now** — status, assignee, anything overdue
   - **what is being waited for**, if the comment says so

Do not summarise the mail. The mail says a comment was added; the question is what it said.

**A summary, and always the link.** Never reproduce the comments verbatim — a wall of Wiki
markup is what the ticket is for. Two or three sentences of what it means, and the link so
the reader can go and read it themselves. The link comes out of the tool result and cannot
be written from memory: the address of the installation is configuration and is never in
front of you. So carry it through exactly as it came, and if a result has no link in it, say
the key and do not invent a URL.

**A person writing about a ticket is a different case.** When a colleague mentions
`IMIT-1234` in an ordinary mail, read the mail — that is what they wrote. Fetch the ticket
only if the answer needs it, and never in place of what they said.

## Reading

`jira_issue` gives the full text of one issue. The description is Wiki markup, not
Markdown: `*bold*`, `_italic_`, `{{code}}`, `{code}...{code}` blocks, `* ` for a list item.
Read it as such, and write it as such.

Prefer one `jira_issue` over ten `jira_search` results when the question is about a
specific ticket. The search line is a summary; the issue is the substance.

## The service desk

`servicedesk_queues` names the queues and their ids; `servicedesk_queue_issues` takes an id from that
call. Do not guess an id.

`servicedesk_sla` is the one worth reaching for unprompted. It answers "what runs out today",
which is the question behind most requests about the service desk even when nobody phrases
it that way. `breached=true` on a running clock is worth saying first, before anything
else about the ticket.

## Before creating anything

**Search first, every time.** Two or three `jira_search` calls with different wordings cost
seconds; a duplicate ticket costs somebody a merge and splits the conversation across two
issues. Search the summary, then the reporter's other open issues, then the obvious
keyword.

If a plausible duplicate exists, say so and ask rather than creating a second one.

Use `Serviceanfrage` as the issue type. The alternative on this installation requires seven
further mandatory fields, and a create that misses one fails with a field id rather than a
name.

Priority names on this installation are `Niedrig`, `Normal`, `Hoch`, `Kritisch`. Normal is
the default and is right unless somebody has said otherwise; do not raise a priority on
your own judgement.

## Changing anything

`jira_comment`, `jira_transition` and `jira_create` all ask before they run, every time.
That is deliberate and not a nuisance to work around: a comment is visible to everybody
watching the issue, and a transition moves work in somebody else's queue.

Call `jira_transitions` before `jira_transition`. The ids differ per workflow, and a
guessed id either fails or moves the issue somewhere nobody expected.

Write comments as the person would: what changed, what is needed, who is waiting. Not
"Investigating." — that tells the reader nothing they did not already assume.
