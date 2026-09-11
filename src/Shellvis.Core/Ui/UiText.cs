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
    // ----------------------------------------------------------------- the page: masthead
    public required string DeskEyebrow { get; init; }
    public required string DeskTitle { get; init; }

    // ------------------------------------------------------------------- the page: status
    public required string StandEyebrow { get; init; }
    public required string StandHeading { get; init; }
    public required string NotCountedYet { get; init; }
    public required string CountNow { get; init; }
    public required string Counting { get; init; }
    public required string CountedAt { get; init; }
    public required string NoMailboxNoNumbers { get; init; }
    public required string NoMailboxShort { get; init; }

    // --------------------------------------------------------------------- the page: cells
    public required string NeedsAnswer { get; init; }
    public required string NeedsAnswerNote { get; init; }
    public required string JustInformation { get; init; }
    public required string JustInformationNote { get; init; }
    public required string NotWorthReading { get; init; }
    public required string NotWorthReadingNote { get; init; }
    public required string NotYetSorted { get; init; }
    public required string NotYetSortedNote { get; init; }
    public required string Unread { get; init; }
    public required string UnreadNote { get; init; }
    public required string MeetingRequests { get; init; }
    public required string MeetingRequestsNote { get; init; }
    public required string LeftToday { get; init; }
    public required string LeftTodayNote { get; init; }
    public required string Overdue { get; init; }
    public required string OverdueNote { get; init; }

    // --------------------------------------------------------------------- the page: trays
    public required string SortedEyebrow { get; init; }
    public required string SortedHeading { get; init; }
    public required string TrayAnswer { get; init; }
    public required string TrayInformation { get; init; }
    public required string TrayIgnore { get; init; }
    public required string TrayIgnoreNote { get; init; }
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
        DeskEyebrow = "Shellvis &middot; Front office",
        DeskTitle = "The Front Office",

        StandEyebrow = "State",
        StandHeading = "What is on the desk right now",
        NotCountedYet = "Not counted yet",
        CountNow = "Count now",
        Counting = "counting ...",
        CountedAt = "counted at ",
        NoMailboxNoNumbers = "No mailbox, no numbers.",
        NoMailboxShort = "no mailbox, no numbers",

        NeedsAnswer = "Needs a reply",
        NeedsAnswerNote = "somebody is waiting",
        JustInformation = "Information only",
        JustInformationNote = "worth knowing, nothing to send",
        NotWorthReading = "Not worth reading",
        NotWorthReadingNote = "circulars, duplicates",
        NotYetSorted = "Not yet sorted",
        NotYetSortedNote = "judged one after another",
        Unread = "Unread",
        UnreadNote = "in the inbox",
        MeetingRequests = "Meeting requests",
        MeetingRequestsNote = "check for a clash",
        LeftToday = "Left today",
        LeftTodayNote = "appointments",
        Overdue = "Overdue",
        OverdueNote = "the date has passed",

        SortedEyebrow = "Sorted",
        SortedHeading = "The sorted post",
        TrayAnswer = "Needs a reply",
        TrayInformation = "Worth knowing",
        TrayIgnore = "Not worth reading",
        TrayIgnoreNote = "not listed",
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
        DeskEyebrow = "Shellvis &middot; Sekretariat",
        DeskTitle = "Das Vorzimmer",

        StandEyebrow = "Stand",
        StandHeading = "Was gerade auf dem Schreibtisch liegt",
        NotCountedYet = "Noch nicht gezählt",
        CountNow = "Jetzt zählen",
        Counting = "zählt ...",
        CountedAt = "gezählt um ",
        NoMailboxNoNumbers = "Ohne Postfach keine Zahlen.",
        NoMailboxShort = "ohne Postfach keine Zahlen",

        NeedsAnswer = "Braucht Antwort",
        NeedsAnswerNote = "jemand wartet",
        JustInformation = "Nur Information",
        JustInformationNote = "wissen, nicht antworten",
        NotWorthReading = "Nicht lesenswert",
        NotWorthReadingNote = "Rundschreiben, Dubletten",
        NotYetSorted = "Noch unsortiert",
        NotYetSortedNote = "wird der Reihe nach beurteilt",
        Unread = "Ungelesen",
        UnreadNote = "im Posteingang",
        MeetingRequests = "Terminanfragen",
        MeetingRequestsNote = "auf Kollision prüfen",
        LeftToday = "Heute noch",
        LeftTodayNote = "Termine",
        Overdue = "Überfällig",
        OverdueNote = "Datum liegt in der Vergangenheit",

        SortedEyebrow = "Sortiert",
        SortedHeading = "Die sortierte Post",
        TrayAnswer = "Braucht eine Antwort",
        TrayInformation = "Muss man wissen",
        TrayIgnore = "Nicht lesenswert",
        TrayIgnoreNote = "nicht aufgezählt",
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
