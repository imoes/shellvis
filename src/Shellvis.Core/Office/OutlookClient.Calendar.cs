namespace Shellvis.Core.Office;

public sealed partial class OutlookClient
{
    /// <summary>olAppointmentItem, and olMeeting once somebody else is invited.</summary>
    private const int ItemAppointment = 1;
    private const int MeetingStatusAppointment = 0;
    private const int MeetingStatusMeeting = 1;

    /// <summary>
    /// olTeamsForBusiness. The value Outlook uses to mark an appointment as a Teams meeting.
    /// </summary>
    /// <remarks>
    /// Setting it is what makes the Teams add-in put the join link in the body when the item
    /// is saved. It exists only on a build with that add-in, so it is set inside a try and
    /// its absence is reported rather than thrown: an appointment without a Teams link is
    /// still an appointment, and being told why is better than losing the whole thing.
    /// </remarks>
    private const int OnlineTeams = 3;

    /// <summary>
    /// Create an appointment, and open it rather than sending it.
    ///
    /// <b>Saved and then displayed, never sent.</b> An appointment with attendees is an
    /// invitation, which is sent mail, and nothing in this application sends mail: the same
    /// rule that makes a reply a draft and a Teams message a filled-in compose box. So the
    /// item lands in the calendar as an unsent meeting and opens in Outlook, where the person
    /// who is inviting nine colleagues can look at it first and press Send themselves.
    ///
    /// <b>MeetingStatus before the recipients.</b> An appointment only accepts attendees once
    /// it knows it is a meeting; added first, they are silently dropped, and the invitation
    /// nobody received looks exactly like one that was sent.
    /// </summary>
    /// <param name="teams">
    /// Ask for a Teams meeting. Reported honestly when the running Outlook cannot provide
    /// one, because a meeting somebody expects to be online and is not wastes a room booking
    /// and everybody's first five minutes.
    /// </param>
    public Task<string> CreateAppointmentAsync(
        string subject,
        DateTime start,
        DateTime end,
        string? body = null,
        string? attendees = null,
        bool teams = false,
        string? location = null,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? appointment = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                appointment = outlook.CreateItem(ItemAppointment);

                appointment.Subject = subject;
                appointment.Start = start;
                appointment.End = end;

                if (location is { Length: > 0 })
                    appointment.Location = location;

                if (body is { Length: > 0 })
                    appointment.Body = body;

                bool hasAttendees = attendees is { Length: > 0 };

                appointment.MeetingStatus = hasAttendees
                    ? MeetingStatusMeeting
                    : MeetingStatusAppointment;

                string? teamsNote = null;

                if (teams)
                {
                    try
                    {
                        appointment.OnlineMeetingProvider = OnlineTeams;
                    }
                    catch (Exception ex)
                    {
                        teamsNote = "the Teams link could not be added ("
                            + ex.Message.Split('\n')[0].Trim()
                            + "); the appointment itself was created.";
                    }
                }

                DraftAddressing? addressing = hasAttendees
                    ? Address(appointment, attendees!, cc: null)
                    : null;

                if (addressing is { AnyResolved: false })
                {
                    // Nothing saved. A meeting in the calendar that invites nobody is worse
                    // than an error, because it looks like the invitation went out.
                    return $"error: none of '{attendees}' could be resolved to an attendee, "
                        + "so no appointment was created. Give a full name as it appears in "
                        + "the address book, or an email address.";
                }

                appointment.Save();

                // Opened so the person can read it and send it. Same reasoning as mail_open:
                // an answer that says "I have arranged it" is a claim they cannot check, and
                // the window is what makes it checkable before anybody else is told.
                appointment.Display(false);

                var summary = new AppointmentSummary(
                    Subject: subject,
                    Start: start,
                    End: end,
                    Location: location ?? string.Empty,
                    IsAllDay: false);

                string what = hasAttendees
                    ? $"created an unsent MEETING and opened it: {summary}"
                    : $"created an appointment and opened it: {summary}";

                if (addressing is not null)
                    what += $"  {addressing.Describe()}";

                if (teams && teamsNote is null)
                    what += "  Teams meeting requested; Outlook adds the join link on save.";

                if (teamsNote is not null)
                    what += "  " + teamsNote;

                return what + (hasAttendees
                    ? "  It has NOT been sent -- the invitation goes out when the user presses Send."
                    : "  It is in the calendar; nobody else was told.")
                    + StartNoticeText()
                    + Environment.NewLine + "      id: " + Str(() => appointment.EntryID);
            }
            finally
            {
                Com.ReleaseAll(outlook, appointment);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// The one-line notice that Outlook had to be started, when it did.
    ///
    /// Duplicated from the tool layer on purpose: this method opens a window, so the fact
    /// that a profile was loaded to do it belongs in its own answer rather than only in a
    /// listing somewhere else.
    /// </summary>
    private string StartNoticeText() =>
        WasStarted ? "  (Outlook was not running and was started.)" : string.Empty;
}
