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
