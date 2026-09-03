namespace Shellvis.Core.Office;

public sealed partial class OutlookClient
{
    /// <summary>How many arrivals one look reports, however many came in.</summary>
    /// <remarks>
    /// A morning's synchronisation can bring in two hundred messages. Twenty is enough for
    /// the model to find the one that matters and short enough that the question stays a
    /// question rather than a mailbox dump; the count of what was left out goes in the state,
    /// not into the prompt, because "and 180 more" is not something an alert can act on.
    /// </remarks>
    private const int ArrivalsPerLook = 20;

    /// <summary>
    /// Look at the mailbox once: what is about to start, and what has come in.
    ///
    /// <b>Polled, not subscribed, and that is forced.</b> Outlook reports NewMailEx and
    /// Reminder through COM events, and a COM event needs a Windows message loop on the
    /// thread that subscribed. <see cref="ComApartment"/> is a work queue with no message
    /// pump -- the same reason <c>Application.AdvancedSearch</c> is unusable here -- so an
    /// event subscription would be made successfully and then never fire. Adding a pump to
    /// the one piece of carefully-reasoned threading in this project, for a feature that a
    /// three-minute look answers just as well, is the wrong trade.
    ///
    /// <b>The state is passed in and a new one comes back.</b> Deciding what is new is the
    /// whole difficulty: without it, every look announces the same morning's mail again.
    /// Keeping that decision in the caller's state rather than in a field means it survives a
    /// restart, which is what stops Shellvis greeting you with an hour of history.
    /// </summary>
    /// <param name="lead">How far ahead an appointment counts as starting soon.</param>
    public Task<WatchFindings> LookAsync(
        WatchState state,
        DateTime now,
        TimeSpan lead,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;

            var upcoming = new List<Upcoming>();
            var arrivals = new List<Arrival>();

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                session = outlook.Session;

                CollectUpcoming(session, state, now, lead, upcoming, cancellationToken);
                CollectArrivals(session, state, now, arrivals, cancellationToken);

                return new WatchFindings(upcoming, arrivals);
            }
            finally
            {
                Com.ReleaseAll(outlook, session);
            }
        }, cancellationToken);
    }

    private static void CollectUpcoming(
        dynamic session,
        WatchState state,
        DateTime now,
        TimeSpan lead,
        List<Upcoming> into,
        CancellationToken cancellationToken)
    {
        dynamic? folder = null;
        dynamic? items = null;
        dynamic? restricted = null;

        try
        {
            folder = session.GetDefaultFolder(FolderCalendar);
            items = folder.Items;

            // SORT FIRST, then expand. The order is the documented sequence and getting it
            // wrong does not fail, it answers wrongly: with IncludeRecurrences set before the
            // sort, Restrict is applied to the series MASTERS rather than to the occurrences,
            // so a weekly meeting comes back with the start date it had months ago. Measured
            // against this calendar -- a twelve-hour window returned twenty appointments,
            // several of them outside it, which is what the harness caught.
            items.Sort("[Start]", false);
            items.IncludeRecurrences = true;

            // The window is now to now+lead. The same half-open trap as the calendar
            // listing: written with the current culture, because Outlook's bracket syntax
            // reads a date in the user's short format and an invariant one is misread.
            string filter = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                "[Start] >= '{0:g}' AND [Start] <= '{1:g}'",
                now,
                now + lead);

            restricted = items.Restrict(filter);

            // ENUMERATED, not indexed, and that is not a style choice.
            //
            // A collection with IncludeRecurrences set does not support indexed access, and
            // it does not say so: restricted[i] returned twenty objects whose every property
            // read failed, so the harness saw twenty appointments with an empty subject and a
            // start date of 0001-01-01. Nothing threw. The count was even right. The working
            // calendar listing in this class has always enumerated, which is the answer;
            // writing a second loop rather than copying that one is what produced this.
            //
            // Capped anyway, because expansion of an endless series is unbounded by nature
            // and a filter is not a guarantee.
            int guard = 0;

            foreach (dynamic entry in restricted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                dynamic? item = entry;

                try
                {
                    if (guard++ > 200)
                        break;

                    string id = Str(() => item.EntryID);

                    // An occurrence Outlook expanded rather than stored has no id of its
                    // own, so one is made from the series and the start. Without it every
                    // look would announce the same daily stand-up again.
                    DateTime start = Date(() => item.Start);
                    string key = id.Length > 0
                        ? id + "@" + start.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture)
                        : Str(() => item.Subject) + "@" + start.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);

                    if (state.AnnouncedAppointments.Contains(key))
                        continue;

                    string body = Str(() => item.Body);

                    into.Add(new Upcoming(
                        EntryId: key,
                        Subject: Str(() => item.Subject),
                        Start: start,
                        Location: Str(() => item.Location),
                        HasTeamsLink: Teams.TeamsLinks.JoinUrlIn(body) is { Length: > 0 },
                        MinutesAway: (int)Math.Round((start - now).TotalMinutes)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreadable entry must not cost the whole look.
                }
                finally
                {
                    Com.Release(item);
                }
            }
        }
        finally
        {
            Com.ReleaseAll(folder, items, restricted);
        }
    }

    private static void CollectArrivals(
        dynamic session,
        WatchState state,
        DateTime now,
        List<Arrival> into,
        CancellationToken cancellationToken)
    {
        dynamic? folder = null;
        dynamic? items = null;

        try
        {
            folder = session.GetDefaultFolder(FolderInbox);
            items = folder.Items;
            items.Sort("[ReceivedTime]", true);

            // A first run reports NOTHING and only sets the mark. Announcing whatever
            // happened to be in the inbox when Shellvis started is the behaviour that makes
            // an alert worthless, and there is no honest way to tell which of it the user has
            // already read.
            DateTime since = state.SeenUpTo ?? now;

            int scan = Math.Min(100, (int)items.Count);

            for (int i = 1; i <= scan; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                dynamic? item = null;

                try
                {
                    item = items[i];

                    DateTime received = Date(() => item.ReceivedTime);

                    // Newest first, so the first one at or before the mark ends it.
                    if (received <= since)
                        break;

                    if (into.Count >= ArrivalsPerLook)
                        break;

                    // IPM.Schedule.Meeting.Request and its relatives. Checked on the class
                    // rather than on the subject, because "Besprechungsanfrage:" is a
                    // localised prefix and the class is not.
                    string messageClass = Str(() => item.MessageClass);

                    bool isRequest = messageClass.StartsWith(
                        "IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase);

                    string subject = Str(() => item.Subject);
                    string fromName = Str(() => item.SenderName);
                    string fromAddress = SmtpOf(item);

                    ArrivalKind kind = ArrivalKind.Ordinary;
                    string? ticket = null;

                    if (isRequest)
                    {
                        kind = ArrivalKind.MeetingRequest;
                    }
                    else if (TicketKeys.LooksAutomated(fromAddress, fromName)
                        && TicketKeys.Primary(subject, Str(() => item.Body)) is { } key)
                    {
                        kind = ArrivalKind.TicketNotification;
                        ticket = key;
                    }

                    into.Add(new Arrival(
                        EntryId: Str(() => item.EntryID),
                        From: fromName.Length > 0 ? fromName : fromAddress,
                        Subject: subject,
                        Received: received,
                        Kind: kind,
                        TicketKey: ticket));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A meeting response, a delivery report or a corrupt item throws on
                    // properties an ordinary mail has. Skipped, not fatal.
                }
                finally
                {
                    Com.Release(item);
                }
            }
        }
        finally
        {
            Com.ReleaseAll(folder, items);
        }
    }
}
