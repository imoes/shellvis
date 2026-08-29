---
name: daily-briefing
description: >-
  Produce the morning or evening briefing, and the short reminders in between.
  Loaded by the scheduled jobs; also useful when someone asks what is on today.
requires_tools:
  - agenda_today
  - agenda_due
---

# The briefing

Two different jobs, and confusing them is the usual way this goes wrong.

## A reminder is one line, or nothing

Call `agenda_due`. It returns only what has **not** already been said, so it is safe on a
timer, and most of the time it returns nothing.

When it returns nothing, **say nothing**. Not "nothing to report", not "all clear" — say
nothing at all. A scheduled job that speaks every five minutes to confirm it is still
running is a job the user turns off within a day, and then the reminders that mattered are
gone too.

When it returns something, say it in one or two lines. What, when, and the one thing they
need to have ready. Not the whole calendar.

> In 15 Minuten: Linux Team Weekly, 10:00. Seit der Einladung kam eine Mail von
> [Schwarz zum Kernel-Update](shellvis:mail/000000012A).

## A briefing is short, and it is not a list

Call `agenda_today`. It repeats things, on purpose: a summary is meant to be complete.

Then do the work that makes it a briefing rather than a dump:

1. **Say what is fixed.** The appointments, in order, with times. This is the shape of the
   day and it comes first.
2. **Name the three things that matter most**, and say why each one matters. Not five, not
   ten. Choosing is the work; a list of thirty items has done none of it.
3. **Say what is late.** Overdue tasks and notes past their date are already marked in the
   result — do not work it out yourself.
4. **Put the rest behind a count.** "Dazu 11 weitere ungelesene Mails" is useful. Listing
   them is not.

In the evening the same call answers a different question: what did not get done, and what
lands tomorrow before the first meeting.

## What not to do

- **Do not invent.** An empty calendar is a fine answer. This application has produced six
  fabricated appointments once, in a year that had not happened yet, from a query that
  legitimately found nothing. Everything factual comes from a tool result in this
  conversation.
- **Do not write anything.** A scheduled run refuses every approval, because nobody is there
  to give one. Reading is all that will work, and it is all that should: propose the task,
  and let the person create it when they are back.
- **Do not repeat yesterday's briefing.** If the calendar is unchanged and nothing is due,
  the briefing is two lines. That is correct.

## Setting the jobs up

Both live in `%USERPROFILE%\.shellvis\jobs.json`. A file with these two, disabled, is
written on first run so they can be turned on rather than typed out:

```json
[
  {
    "name": "reminders",
    "prompt": "Load the assistant/daily-briefing skill and follow the reminder half.",
    "schedule": "5m",
    "enabled": false,
    "skills": ["daily-briefing"]
  },
  {
    "name": "morning-briefing",
    "prompt": "Load the assistant/daily-briefing skill and give the morning briefing.",
    "schedule": "0 7 * * 1-5",
    "enabled": false,
    "skills": ["daily-briefing"]
  }
]
```

`5m` counts from the last attempt, not the last success, so a failing job does not retry in
a tight loop. `0 7 * * 1-5` is absolute and does not drift; a machine that was asleep at
seven still delivers up to two hours late and then lets the moment pass, because a report
about this morning at midnight is noise.
