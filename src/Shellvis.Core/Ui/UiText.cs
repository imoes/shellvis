using System.Reflection;

namespace Shellvis.Core.Ui;

/// <summary>
/// Every string a person reads, in one language.
///
/// <b>Why a class of required properties rather than a resource file.</b> The property this
/// design is bought for is that A MISSING TRANSLATION DOES NOT COMPILE. Every member is
/// <c>required</c>, so adding one here and forgetting it in the other language is CS9035 at
/// build time rather than an empty label somebody notices in a screenshot months later. A
/// .resx pair gives a key lookup that returns null, which is the same class of silent
/// failure this project has spent the week removing from everything else.
///
/// It also keeps both languages side by side in one file. Two .resx files drift: a wording
/// is improved in the one the author speaks and the other keeps the old sentence, and
/// nothing anywhere says so.
///
/// <b>What belongs here.</b> Labels, headings, tooltips, notifications, the reference page.
/// What does not: the console log, which stays English because it is a record of tool names
/// and results; and anything the model reads, which is not presentation at all.
/// </summary>
public sealed class UiText
{
    /// <summary>
    /// The two-letter code of this language, for the document's <c>lang</c> attribute and
    /// for choosing the culture a date is written in.
    ///
    /// Here rather than looked up from the strings, because the page is handed a
    /// <see cref="UiText"/> and nothing else, and a German page stamped <c>lang="en"</c> is
    /// read aloud in the wrong accent by a screen reader and hyphenated by the wrong rules.
    /// </summary>
    public required string LanguageTag { get; init; }

    // ----------------------------------------------------------------- the page: masthead
    public required string DeskEyebrow { get; init; }
    public required string DeskTitle { get; init; }

    // ------------------------------------------------------------------- the page: status
    public required string NotCountedYet { get; init; }
    public required string CountNow { get; init; }
    public required string Counting { get; init; }
    public required string CountedAt { get; init; }

    /// <summary>What the button says for a moment after a count somebody asked for.</summary>
    public required string Counted { get; init; }

    /// <summary>What the button says after a count that failed.</summary>
    public required string TryAgain { get; init; }

    public required string NoMailboxNoNumbers { get; init; }
    public required string NoMailboxShort { get; init; }

    // ---------------------------------------------------------------- the page: the day
    /// <summary>The heading over the day's appointments.</summary>
    public required string TrayToday { get; init; }

    /// <summary>Under the heading when the calendar has nothing at all today.</summary>
    public required string NoAppointmentsToday { get; init; }

    /// <summary>Beside an appointment whose end has passed.</summary>
    public required string Past { get; init; }

    /// <summary>Beside an appointment that has started and not ended.</summary>
    public required string Running { get; init; }

    /// <summary>"in " -- opens the time-until phrase beside the next appointment.</summary>
    public required string InPrefix { get; init; }

    /// <summary>" Min." / " min" -- closes a minute count.</summary>
    public required string MinutesShort { get; init; }

    /// <summary>" Std. " / " h " -- between an hour count and the minutes that follow it.</summary>
    public required string HoursShort { get; init; }

    /// <summary>Beside an all-day entry, which has no time to show.</summary>
    public required string AllDay { get; init; }

    /// <summary>Beside the meeting-request count in the day tray.</summary>
    public required string MeetingRequestsNote { get; init; }

    /// <summary>Under an appointment that has exactly one mail about it.</summary>
    public required string OneMailAbout { get; init; }

    /// <summary>"Mails dazu" / "mails about it" -- after a count of more than one.</summary>
    public required string MailsAbout { get; init; }

    /// <summary>What the day tray says while the mail about each appointment is being looked up.</summary>
    public required string LookingAhead { get; init; }

    // ---------------------------------------------------------- the page: the long form
    //
    // A row opens. Under the sentence the sorting wrote appears the whole conversation as
    // an overview, written once by the model and kept.
    public required string UnfoldTip { get; init; }
    public required string FoldTip { get; init; }
    public required string ReadingThread { get; init; }
    public required string ThreadFailed { get; init; }
    public required string ReadAgain { get; init; }

    /// <summary>" Nachrichten" / " messages" -- after the count the digest covered.</summary>
    public required string MessagesWord { get; init; }

    /// <summary>"gelesen " / "read " -- before the time the digest was written.</summary>
    public required string ReadAt { get; init; }

    // ------------------------------------------------------------- the page: the search
    //
    // The trays show a handful and put the rest behind a count. The search is how the rest
    // is reached: what the desk remembers about a subject, with the model's sentence where
    // there is one, and what Outlook's own search finds beyond that.
    public required string SearchPlaceholder { get; init; }
    public required string SearchButton { get; init; }
    public required string SearchHeading { get; init; }
    public required string Searching { get; init; }

    /// <summary>"nichts gefunden für " -- takes the query.</summary>
    public required string NothingFoundFor { get; init; }

    /// <summary>" Treffer" / " hit(s)" -- after the count.</summary>
    public required string HitsWord { get; init; }

    /// <summary>"über den Suchindex in " -- before the folder count.</summary>
    public required string ViaIndex { get; init; }

    /// <summary>" Ordnern" / " folders" -- after it.</summary>
    public required string FoldersWord { get; init; }

    /// <summary>"die neuesten " / "the newest " -- before the scanned count, when the index had nothing.</summary>
    public required string ViaWalkStart { get; init; }

    /// <summary>" Nachrichten gelesen" / " messages read" -- after it.</summary>
    public required string ViaWalkEnd { get; init; }

    /// <summary>" aus dem Gedächtnis" -- the part of the result the desk already knew.</summary>
    public required string FromMemory { get; init; }

    /// <summary>What the panel says when Outlook could not be asked.</summary>
    public required string SearchFailed { get; init; }

    /// <summary>
    /// "auch gesucht nach: " -- before the words the model imagined the answer would
    /// contain, so the reader can see why a hit without their word in it is a hit.
    /// </summary>
    public required string AlsoSearched { get; init; }

    /// <summary>
    /// "dazu: " / "see: " -- opens the line under a summary that points at the earlier
    /// thing the sorting tied this mail to.
    /// </summary>
    public required string SeeAlso { get; init; }

    /// <summary>What stands where the sender would, on a hit that has no sender.</summary>
    public required string KindTask { get; init; }
    public required string KindAppointment { get; init; }
    public required string KindTicket { get; init; }

    // -------------------------------------------------------------- the page: the ledger
    //
    // The two figures that describe the COUNT rather than the desk: how much is unread in
    // the folder, and how much of it the model has not judged yet. They used to be cells in
    // a band of eight; they are a line of small print now, because neither one is something
    // to act on.
    public required string UnreadNote { get; init; }
    public required string NotYetSortedNote { get; init; }

    // --------------------------------------------------------------------- the page: trays
    public required string TrayAnswer { get; init; }
    public required string TrayInformation { get; init; }
    public required string TrayIgnore { get; init; }
    public required string TrayIgnoreNote { get; init; }

    /// <summary>The heading over the overdue tasks.</summary>
    public required string TrayOverdue { get; init; }

    /// <summary>"fällig " / "due " -- before a task's date.</summary>
    public required string DuePrefix { get; init; }

    /// <summary>
    /// How that date is written: "dd.MM." / "d MMM". Short on purpose -- the tray is
    /// narrow, the year is almost always this one, and "fällig 09.09.2026" pushed the
    /// task's own name into an ellipsis.
    /// </summary>
    public required string DueFormat { get; init; }

    public required string NotSortedYet { get; init; }
    public required string NothingOfThat { get; init; }
    public required string NoSubject { get; init; }
    public required string UnknownSender { get; init; }
    public required string OpenInOutlook { get; init; }

    /// <summary>The heading of the little "it changed" strip above the trays.</summary>
    public required string Refreshed { get; init; }

    /// <summary>Its dismiss button. Not <c>Close</c>, which is the window's tooltip.</summary>
    public required string CloseWord { get; init; }

    // ------------------------------------------------- the page: sentences built in script
    /// <summary>"und " / "and " -- the lead-in to "N more".</summary>
    public required string AndPrefix { get; init; }

    /// <summary>" weitere" / " more" -- what follows the count.</summary>
    public required string MoreSuffix { get; init; }

    /// <summary>The line before older entries, when some fresh ones lead.</summary>
    public required string OlderLabel { get; init; }

    /// <summary>"nichts aus den letzten " / "nothing from the last " -- takes a day count.</summary>
    public required string NothingFromTheLast { get; init; }

    /// <summary>" Tagen" / " days" -- closes the phrase above.</summary>
    public required string DaysSuffix { get; init; }

    /// <summary>Used instead of a day count when the period is unknown.</summary>
    public required string FromThePeriod { get; init; }

    /// <summary>The period line. Takes the period phrase in the middle.</summary>
    public required string PeriodSentenceStart { get; init; }
    public required string PeriodSentenceEnd { get; init; }

    // ------------------------------------------------------ the remembering period, named
    //
    // Every phrase is a plural noun phrase that can stand as the subject of "liegen" /
    // "are on the desk" -- see DeskWindow.Describe for why that is a constraint and not a
    // preference. PeriodDays takes a number between its two halves.
    public required string PeriodTwoDays { get; init; }
    public required string PeriodDaysStart { get; init; }
    public required string PeriodDaysEnd { get; init; }
    public required string PeriodTwoWeeks { get; init; }
    public required string PeriodFourWeeks { get; init; }
    public required string PeriodTwoMonths { get; init; }
    public required string PeriodThreeMonths { get; init; }

    /// <summary>Shown in the unsorted cell while a pass runs. Takes a count.</summary>
    public required string JudgingOne { get; init; }
    public required string JudgingManyStart { get; init; }
    public required string JudgingManyEnd { get; init; }

    /// <summary>The badge on a cell that has grown. Takes a count.</summary>
    public required string BadgeNewSuffix { get; init; }

    /// <summary>Appended to the unread note when the scan was capped. Takes a count.</summary>
    public required string ScanCappedNote { get; init; }

    // ------------------------------------- what each figure is CALLED inside a sentence
    //
    // Separate from the cell labels above, and not derived from them. A label is styled
    // text set in small capitals; these go into a sentence the change notice builds
    // ("3 more need a reply"), and a label reads wrong there in either language.
    public required string NameAnswer { get; init; }
    public required string NameInformation { get; init; }
    public required string NameIgnore { get; init; }
    public required string NamePending { get; init; }
    public required string NameUnread { get; init; }
    public required string NameRequests { get; init; }
    public required string NameToday { get; init; }
    public required string NameOverdue { get; init; }

    // ----------------------------------------------------------------------- window chrome
    public required string VorzimmerWindowTitle { get; init; }
    public required string VorzimmerButtonTip { get; init; }
    public required string AnswerButtonTip { get; init; }
    public required string ConsoleShowTip { get; init; }
    public required string ConsoleHideTip { get; init; }
    public required string MinimiseTip { get; init; }
    public required string CloseTip { get; init; }
    public required string DictateTip { get; init; }
    public required string HistoryTip { get; init; }
    public required string SettingsTip { get; init; }
    public required string ExpandTip { get; init; }
    public required string DockTip { get; init; }
    public required string AttachTip { get; init; }
    public required string ModeTip { get; init; }
    public required string AskPlaceholder { get; init; }
    public required string ConversationTitle { get; init; }
    public required string ConversationWriting { get; init; }

    // -------------------------------------------------------------------------- approvals
    public required string ApprovalTitle { get; init; }
    public required string ApprovalOnce { get; init; }
    public required string ApprovalSession { get; init; }
    public required string ApprovalDeny { get; init; }
    public required string ApprovalArguments { get; init; }
    public required string AwaitingApproval { get; init; }

    // ------------------------------------------------- the change notice, and what stopped
    /// <summary>Its title. The body is the list of what grew.</summary>
    public required string DeskChangedTitle { get; init; }

    public required string NoticeNeedsAnswer { get; init; }
    public required string NoticeNewUnread { get; init; }
    public required string NoticeMeetingRequests { get; init; }
    public required string NoticeOverdue { get; init; }

    public required string OutlookUnreachable { get; init; }
    public required string CouldNotCount { get; init; }

    // ----------------------------------------------------------------------- the settings
    public required string LanguageLabel { get; init; }
    public required string LanguageAuto { get; init; }

    /// <summary>
    /// The page's tokens, keyed by property name.
    /// </summary>
    /// <remarks>
    /// Built by reflection so a string added above reaches the page without a second list to
    /// keep in step. The page uses <c>{{PropertyName}}</c>, and whatever is left unsubstituted
    /// is caught by the harness rather than shown to somebody.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Tokens =>
        _tokens ??= typeof(UiText)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .ToDictionary(p => p.Name, p => (string)(p.GetValue(this) ?? string.Empty), StringComparer.Ordinal);

    private IReadOnlyDictionary<string, string>? _tokens;

    /// <summary>English.</summary>
    public static UiText En { get; } = new()
    {
        LanguageTag = "en",

        DeskEyebrow = "Shellvis &middot; Front office",
        DeskTitle = "The Front Office",

        NotCountedYet = "Not counted yet",
        CountNow = "Count now",
        Counting = "counting ...",
        CountedAt = "counted at ",
        Counted = "counted",
        TryAgain = "Try again",
        NoMailboxNoNumbers = "No mailbox, no numbers.",
        NoMailboxShort = "no mailbox, no numbers",

        TrayToday = "Today",
        NoAppointmentsToday = "no appointments today",
        Past = "over",
        Running = "running",
        InPrefix = "in ",
        MinutesShort = " min",
        HoursShort = " h ",
        AllDay = "all day",
        MeetingRequestsNote = "check for a clash",
        OneMailAbout = "1 mail about it",
        MailsAbout = " mails about it",
        LookingAhead = "looking for mail about today's meetings ...",

        UnfoldTip = "Show the whole conversation as an overview",
        FoldTip = "Fold it away",
        ReadingThread = "reading the conversation ...",
        ThreadFailed = "The conversation could not be read",
        ReadAgain = "Read again",
        MessagesWord = " messages",
        ReadAt = "read ",

        SearchPlaceholder = "Search the mailbox and the desk",
        SearchButton = "Search",
        SearchHeading = "Search",
        Searching = "searching ...",
        NothingFoundFor = "nothing found for ",
        HitsWord = " hit(s)",
        ViaIndex = "via the search index in ",
        FoldersWord = " folders",
        ViaWalkStart = "the newest ",
        ViaWalkEnd = " messages read, the index had none",
        FromMemory = " from the desk's memory",
        SearchFailed = "Outlook could not be searched",
        AlsoSearched = "also searched for: ",
        SeeAlso = "see: ",
        KindTask = "Task",
        KindAppointment = "Appointment",
        KindTicket = "Ticket",

        UnreadNote = "in the inbox",
        NotYetSortedNote = "judged one after another",

        TrayAnswer = "Needs a reply",
        TrayInformation = "Worth knowing",
        TrayIgnore = "Not worth reading",
        TrayIgnoreNote = "not listed",
        TrayOverdue = "Overdue",
        DuePrefix = "due ",
        DueFormat = "d MMM",
        NotSortedYet = "not sorted yet",
        NothingOfThat = "none of that",
        NoSubject = "(no subject)",
        UnknownSender = "(unknown)",
        OpenInOutlook = "Open in Outlook",
        Refreshed = "Refreshed",
        CloseWord = "Close",

        AndPrefix = "and ",
        MoreSuffix = " more",
        OlderLabel = "older:",
        NothingFromTheLast = "nothing from the last ",
        DaysSuffix = " days",
        FromThePeriod = "the period",
        PeriodSentenceStart = "Everything unread is sorted. On the desk is ",
        PeriodSentenceEnd = " — older is counted only. Changeable in the settings.",
        PeriodTwoDays = "the last two days",
        PeriodDaysStart = "the last ",
        PeriodDaysEnd = " days",
        PeriodTwoWeeks = "the last two weeks",
        PeriodFourWeeks = "the last four weeks",
        PeriodTwoMonths = "the last two months",
        PeriodThreeMonths = "the last three months",
        JudgingOne = "judging one message ...",
        JudgingManyStart = "judging ",
        JudgingManyEnd = " messages ...",
        BadgeNewSuffix = " new",
        ScanCappedNote = "in the inbox · the breakdown counts the newest ",

        NameAnswer = "need a reply",
        NameInformation = "for information",
        NameIgnore = "not worth reading",
        NamePending = "still unsorted",
        NameUnread = "unread",
        NameRequests = "meeting requests",
        NameToday = "appointments today",
        NameOverdue = "overdue",

        VorzimmerWindowTitle = "The Front Office",
        VorzimmerButtonTip = "The front office: how this assistant keeps a desk",
        AnswerButtonTip = "Bring the answer back",
        ConsoleShowTip = "Show console",
        ConsoleHideTip = "Hide console",
        MinimiseTip = "Minimise to the taskbar",
        CloseTip = "Close (the answer button on the pill brings it back)",
        DictateTip = "Dictate (hold the space bar in the box, or Ctrl+Alt+D)",
        HistoryTip = "Past conversations",
        SettingsTip = "Settings",
        ExpandTip = "Back to the full bar",
        DockTip = "Dock on the taskbar",
        AttachTip = "Attach a file",
        ModeTip = "How much may run without asking",
        AskPlaceholder = "Ask Shellvis",
        ConversationTitle = "Conversation",
        ConversationWriting = "Conversation (writing...)",

        ApprovalTitle = "Shellvis needs your say-so",
        ApprovalOnce = "Once",
        ApprovalSession = "Session",
        ApprovalDeny = "Deny",
        ApprovalArguments = "Arguments",
        AwaitingApproval = "Shellvis needs your say-so.",

        DeskChangedTitle = "The desk has changed",
        NoticeNeedsAnswer = " need a reply",
        NoticeNewUnread = " newly unread",
        NoticeMeetingRequests = " meeting request(s)",
        NoticeOverdue = " overdue",
        OutlookUnreachable = "Outlook cannot be reached",
        CouldNotCount = "could not be counted",

        LanguageLabel = "Language",
        LanguageAuto = "follow the system",
    };

    /// <summary>German.</summary>
    public static UiText De { get; } = new()
    {
        LanguageTag = "de",

        DeskEyebrow = "Shellvis &middot; Sekretariat",
        DeskTitle = "Das Vorzimmer",

        NotCountedYet = "Noch nicht gezählt",
        CountNow = "Jetzt zählen",
        Counting = "zählt ...",
        CountedAt = "gezählt um ",
        Counted = "gezählt",
        TryAgain = "Nochmal",
        NoMailboxNoNumbers = "Ohne Postfach keine Zahlen.",
        NoMailboxShort = "ohne Postfach keine Zahlen",

        TrayToday = "Heute",
        NoAppointmentsToday = "keine Termine heute",
        Past = "vorbei",
        Running = "läuft",
        InPrefix = "in ",
        MinutesShort = " Min.",
        HoursShort = " Std. ",
        AllDay = "ganztägig",
        MeetingRequestsNote = "auf Kollision prüfen",
        OneMailAbout = "1 Mail dazu",
        MailsAbout = " Mails dazu",
        LookingAhead = "sucht Post zu den heutigen Terminen ...",

        UnfoldTip = "Den ganzen Verlauf als Übersicht zeigen",
        FoldTip = "Wieder zuklappen",
        ReadingThread = "liest den Verlauf ...",
        ThreadFailed = "Der Verlauf konnte nicht gelesen werden",
        ReadAgain = "Neu lesen",
        MessagesWord = " Nachrichten",
        ReadAt = "gelesen ",

        SearchPlaceholder = "Postfach und Schreibtisch durchsuchen",
        SearchButton = "Suchen",
        SearchHeading = "Suche",
        Searching = "sucht ...",
        NothingFoundFor = "nichts gefunden für ",
        HitsWord = " Treffer",
        ViaIndex = "über den Suchindex in ",
        FoldersWord = " Ordnern",
        ViaWalkStart = "die neuesten ",
        ViaWalkEnd = " Nachrichten gelesen, der Index hatte nichts",
        FromMemory = " aus dem Gedächtnis des Schreibtischs",
        SearchFailed = "Outlook konnte nicht durchsucht werden",
        AlsoSearched = "auch gesucht nach: ",
        SeeAlso = "dazu: ",
        KindTask = "Aufgabe",
        KindAppointment = "Termin",
        KindTicket = "Ticket",

        UnreadNote = "im Posteingang",
        NotYetSortedNote = "wird der Reihe nach beurteilt",

        TrayAnswer = "Braucht eine Antwort",
        TrayInformation = "Muss man wissen",
        TrayIgnore = "Nicht lesenswert",
        TrayIgnoreNote = "nicht aufgezählt",
        TrayOverdue = "Überfällig",
        DuePrefix = "fällig ",
        DueFormat = "dd.MM.",
        NotSortedYet = "noch nicht sortiert",
        NothingOfThat = "nichts davon",
        NoSubject = "(kein Betreff)",
        UnknownSender = "(unbekannt)",
        OpenInOutlook = "In Outlook öffnen",
        Refreshed = "Aktualisiert",
        CloseWord = "Schließen",

        AndPrefix = "und ",
        MoreSuffix = " weitere",
        OlderLabel = "älter:",
        NothingFromTheLast = "nichts aus den letzten ",
        DaysSuffix = " Tagen",
        FromThePeriod = "dem Zeitraum",
        PeriodSentenceStart = "Sortiert wird alles Ungelesene. Auf dem Tisch liegen ",
        PeriodSentenceEnd = " — Älteres wird nur gezählt. Änderbar in den Einstellungen.",
        PeriodTwoDays = "die letzten zwei Tage",
        PeriodDaysStart = "die letzten ",
        PeriodDaysEnd = " Tage",
        PeriodTwoWeeks = "die letzten zwei Wochen",
        PeriodFourWeeks = "die letzten vier Wochen",
        PeriodTwoMonths = "die letzten zwei Monate",
        PeriodThreeMonths = "die letzten drei Monate",
        JudgingOne = "beurteilt gerade eine Nachricht ...",
        JudgingManyStart = "beurteilt gerade ",
        JudgingManyEnd = " Nachrichten ...",
        BadgeNewSuffix = " neu",
        ScanCappedNote = "im Posteingang · die Aufteilung zählt die neuesten ",

        NameAnswer = "brauchen eine Antwort",
        NameInformation = "zur Information",
        NameIgnore = "nicht lesenswert",
        NamePending = "noch unsortiert",
        NameUnread = "ungelesen",
        NameRequests = "Terminanfragen",
        NameToday = "Termine heute",
        NameOverdue = "überfällig",

        VorzimmerWindowTitle = "Das Vorzimmer",
        VorzimmerButtonTip = "Das Vorzimmer: wie dieser Assistent einen Schreibtisch führt",
        AnswerButtonTip = "Die Antwort zurückholen",
        ConsoleShowTip = "Konsole zeigen",
        ConsoleHideTip = "Konsole verbergen",
        MinimiseTip = "In die Taskleiste legen",
        CloseTip = "Schließen (der Antwort-Knopf auf der Leiste holt es zurück)",
        DictateTip = "Diktieren (Leertaste im Feld halten, oder Strg+Alt+D)",
        HistoryTip = "Frühere Unterhaltungen",
        SettingsTip = "Einstellungen",
        ExpandTip = "Zurück zur vollen Leiste",
        DockTip = "An die Taskleiste andocken",
        AttachTip = "Datei anhängen",
        ModeTip = "Was ohne Rückfrage laufen darf",
        AskPlaceholder = "Shellvis fragen",
        ConversationTitle = "Unterhaltung",
        ConversationWriting = "Unterhaltung (schreibt...)",

        ApprovalTitle = "Shellvis braucht Ihr Einverständnis",
        ApprovalOnce = "Einmal",
        ApprovalSession = "Sitzung",
        ApprovalDeny = "Ablehnen",
        ApprovalArguments = "Argumente",
        AwaitingApproval = "Shellvis braucht Ihr Einverständnis.",

        DeskChangedTitle = "Auf dem Schreibtisch hat sich etwas getan",
        NoticeNeedsAnswer = " braucht eine Antwort",
        NoticeNewUnread = " neu ungelesen",
        NoticeMeetingRequests = " Terminanfrage(n)",
        NoticeOverdue = " überfällig",
        OutlookUnreachable = "Outlook ist nicht erreichbar",
        CouldNotCount = "konnte nicht gezählt werden",

        LanguageLabel = "Sprache",
        LanguageAuto = "wie das System",
    };
}
