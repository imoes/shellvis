---
name: confluence
description: >-
  Find and read Confluence pages: CQL that matches what you meant, and what to do with a
  page body full of macros.
requires_tools:
  - confluence_search
---

# Reading Confluence

## Searching

CQL is not a search box. `text ~ vpn does not work` searches for four separate words and
returns everything mentioning any of them. Quote the phrase:

    text ~ "vpn does not work"

Narrow by space once you know which one holds the documentation:

    text ~ "onboarding" AND space = 002IT AND type = page

Search titles before full text when you are looking for a known document. `title ~ "..."`
finds the page somebody named; `text ~ "..."` finds the forty pages that mention it.

If nothing matches, broaden one term at a time. Concluding that a page does not exist after
one narrow CQL is how a wiki gets a second copy of a document that was already there.

## Reading

`confluence_page` returns the body in storage format: XHTML with Confluence macros in it.
Expect `<ac:structured-macro>` elements, `<ri:page>` links and tables as real markup. Read
through it — the text is in there — and quote the prose rather than the markup when you
answer.

A long page is a long answer. Summarise it and name the page and its space, so the person
can open the source if your summary is not enough. Never present a summary as if it were
the page.

## Writing

There is no write tool here, and that is a decision rather than an omission. A page is a
shared document that colleagues rely on and that carries an author's name; one written by
an agent that nobody reviewed is worse than no page at all. Draft the text in the
conversation and let the person paste it themselves.
