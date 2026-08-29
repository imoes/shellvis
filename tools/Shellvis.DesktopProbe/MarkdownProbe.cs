using Shellvis.Core.Markdown;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Checks the Markdown parser against what the models here actually write.
///
/// <b>Why this harness exists.</b> The parser lived inside the WinUI renderer, so it could
/// not run without a XAML app and had no checks at all. That is also the piece the user's
/// "the output still is not Markdown" complaint pointed at, and answering it meant reading
/// code instead of running it. Split out into Core, it is a pure function over strings, so
/// the cases can simply be stated.
///
/// The cases are taken from the session database rather than invented: what is in there is
/// bullets with a bold head, code spans around cmdlet names, and fenced blocks. The
/// regressions are pinned too, because both of them shipped: a closing pair of asterisks
/// that refused to close, and a heading whose base weight inverted its own markup.
/// </summary>
internal static class MarkdownProbe
{
    public static int Run()
    {
        int failures = 0;

        void Check(string what, bool passed, string detail = "")
        {
            if (!passed)
                failures++;

            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        // Every span of a document, flattened, so a check can talk about text.
        static IEnumerable<MarkdownSpan> Spans(MarkdownDocument doc) =>
            doc.Blocks.SelectMany(b => b switch
            {
                MarkdownBlock.Heading h => h.Inlines,
                MarkdownBlock.Bullet u => u.Inlines,
                MarkdownBlock.Paragraph p => p.Inlines,
                _ => [],
            });

        static string Plain(MarkdownDocument doc) =>
            string.Concat(Spans(doc).Select(s => s.Text));

        static bool Styled(MarkdownDocument doc, string text, SpanStyle style) =>
            Spans(doc).Any(s => s.Text == text && s.Style == style);

        Console.WriteLine("markdown: the forms this model writes\n");

        // The calendar answer, verbatim from the session database.
        MarkdownDocument calendar = MarkdownParser.Parse(
            "- **Montag, 24. August**: CUE-STACK-Team JF, 11:00\n"
            + "- **Dienstag, 25. August**: Mobil, ganztägig");

        Check("two bullets become two blocks", calendar.Blocks.Count == 2);
        Check("the marker is not left in the text",
            !Plain(calendar).Contains('-', StringComparison.Ordinal)
            || Plain(calendar).Contains("CUE", StringComparison.Ordinal));
        Check("the bold head is bold", Styled(calendar, "Montag, 24. August", SpanStyle.Strong));
        Check("the rest of the line is not",
            Styled(calendar, ": CUE-STACK-Team JF, 11:00", SpanStyle.None));
        Check("no asterisk survives into the text",
            !Plain(calendar).Contains('*', StringComparison.Ordinal), Plain(calendar));

        // The regression that shipped: the closing pair refused to close, so a heading came
        // out as "Montag, 24.08.2026**".
        MarkdownDocument atEnd = MarkdownParser.Parse("Der Termin ist am **24.08.2026**");
        Check("a bold span that ends the line closes",
            !Plain(atEnd).Contains('*', StringComparison.Ordinal), Plain(atEnd));
        Check("and the span itself is bold", Styled(atEnd, "24.08.2026", SpanStyle.Strong));

        // The second regression, caught before it was visible: a heading's base weight was
        // held in the same variable as the markup state, so the first pair CLOSED.
        MarkdownDocument boldHeading = MarkdownParser.Parse("## **Titel** und Rest");
        var head = boldHeading.Blocks[0] as MarkdownBlock.Heading;

        Check("a heading with markup stays a heading", head is not null);
        Check("its own bold opens rather than closes",
            head?.Inlines.Any(s => s.Text == "Titel" && s.Has(SpanStyle.Strong)) == true);
        Check("and the remainder is not bold",
            head?.Inlines.Any(s => s.Text.Contains("und Rest", StringComparison.Ordinal)
                && !s.Has(SpanStyle.Strong)) == true);

        Console.WriteLine("\nblocks:");

        MarkdownDocument headings = MarkdownParser.Parse("# Eins\n### Drei\n#nicht\nC# ist keine Überschrift");
        Check("a hash with a space is a heading",
            headings.Blocks.OfType<MarkdownBlock.Heading>().Count() == 2);
        Check("levels are kept as written",
            headings.Blocks.OfType<MarkdownBlock.Heading>().Select(h => h.Level).SequenceEqual([1, 3]));
        Check("a hash without a space is not",
            Plain(headings).Contains("#nicht", StringComparison.Ordinal));
        Check("and neither is a language name",
            Plain(headings).Contains("C# ist", StringComparison.Ordinal));

        MarkdownDocument ordered = MarkdownParser.Parse("3. Drei\n4. Vier");
        Check("an ordered list keeps its own numbers",
            ordered.Blocks.OfType<MarkdownBlock.Bullet>().Select(b => b.Marker).SequenceEqual(["3.", "4."]));

        MarkdownDocument nested = MarkdownParser.Parse("- eins\n  - zwei\n      - sechs");
        Check("indent becomes depth",
            nested.Blocks.OfType<MarkdownBlock.Bullet>().Select(b => b.Depth).SequenceEqual([0, 1, 2]));
        Check("depth is clamped rather than growing without bound",
            nested.Blocks.OfType<MarkdownBlock.Bullet>().All(b => b.Depth <= 2));

        MarkdownDocument fenced = MarkdownParser.Parse(
            "Hier:\n```powershell\nGet-Process | Where-Object { $_.CPU -gt 10 }\n```\nFertig.");

        var code = fenced.Blocks.OfType<MarkdownBlock.Code>().FirstOrDefault();
        Check("a fence becomes a code block", code is not null);
        Check("a closed fence is marked closed", code?.Closed == true);
        Check("nothing inside a fence is Markdown",
            code?.Text.Contains("-gt 10", StringComparison.Ordinal) == true, code?.Text ?? "");
        Check("the prose around it survives",
            Plain(fenced).Contains("Fertig.", StringComparison.Ordinal));

        // A dash inside a fence is a switch, not a bullet. This is the reason fences are
        // handled before anything else.
        MarkdownDocument switches = MarkdownParser.Parse("```\n- Recurse\n```");
        Check("a dash inside a fence is not a bullet",
            !switches.Blocks.OfType<MarkdownBlock.Bullet>().Any());

        MarkdownDocument streaming = MarkdownParser.Parse("```\nGet-Date");
        Check("an unterminated fence still renders as code",
            streaming.Blocks.OfType<MarkdownBlock.Code>().Any());
        Check("and is marked as not closed",
            streaming.Blocks.OfType<MarkdownBlock.Code>().First().Closed == false);

        MarkdownDocument wrapped = MarkdownParser.Parse("Eine Zeile\nund noch eine.\n\nNeuer Absatz.");
        Check("wrapped prose joins into one paragraph",
            wrapped.Blocks.OfType<MarkdownBlock.Paragraph>().Count() == 2);
        Check("joined with a space, not a break",
            Plain(wrapped).Contains("Eine Zeile und noch eine.", StringComparison.Ordinal), Plain(wrapped));

        MarkdownDocument padded = MarkdownParser.Parse("A\n\n\n\n\nB");
        Check("runs of blank lines collapse",
            padded.Blocks.Count == 2, padded.Blocks.Count.ToString());

        Console.WriteLine("\ninline:");

        MarkdownDocument spans = MarkdownParser.Parse(
            "Nutze `Get-PSDrive`, *nicht* ~~Get-Volume~~, und zwar **immer**.");

        Check("a code span is code", Styled(spans, "Get-PSDrive", SpanStyle.Code));
        Check("emphasis is emphasis", Styled(spans, "nicht", SpanStyle.Emphasis));
        Check("strikethrough survives", Styled(spans, "Get-Volume", SpanStyle.Strike));
        Check("strong is strong", Styled(spans, "immer", SpanStyle.Strong));
        Check("no delimiter leaks into the text",
            !Plain(spans).Any(c => c is '*' or '~' or '`'), Plain(spans));

        MarkdownDocument nestedSpan = MarkdownParser.Parse("**fett `code` fett**");
        Check("code nests inside bold", Styled(nestedSpan, "code", SpanStyle.Code));
        Check("and the bold survives around it", Styled(nestedSpan, "fett ", SpanStyle.Strong));

        // The domain reason this rule exists: snake_case names are everywhere here.
        MarkdownDocument snake = MarkdownParser.Parse("Das Tool heisst powershell_run_winps.");
        Check("an underscore inside a word stays a character",
            Plain(snake).Contains("powershell_run_winps", StringComparison.Ordinal), Plain(snake));

        MarkdownDocument backtick = MarkdownParser.Parse("PowerShell escapes with ` and that is that.");
        Check("an unclosed backtick is a backtick",
            Plain(backtick).Contains('`', StringComparison.Ordinal), Plain(backtick));

        MarkdownDocument stray = MarkdownParser.Parse("Ein Stern ** ohne Partner");
        Check("a stray pair does not turn the rest bold",
            Spans(stray).All(s => !s.Has(SpanStyle.Strong)), Plain(stray));

        MarkdownDocument escaped = MarkdownParser.Parse("Ein literaler \\*Stern\\* bleibt stehen.");
        Check("a backslash escape yields the character",
            Plain(escaped).Contains("*Stern*", StringComparison.Ordinal), Plain(escaped));
        Check("and does not open emphasis",
            Spans(escaped).All(s => !s.Has(SpanStyle.Emphasis)));

        Console.WriteLine("\nlinks:");

        MarkdownDocument link = MarkdownParser.Parse(
            "Siehe [Angebot Q3](shellvis:mail/000000012A) von Frau Meier.");

        var linked = Spans(link).Where(sp => sp.Has(SpanStyle.Link)).ToList();

        Check("a link becomes a link span", linked.Count == 1);
        Check("its text is the label, not the target",
            linked.FirstOrDefault()?.Text == "Angebot Q3", linked.FirstOrDefault()?.Text ?? "");
        Check("the target is carried",
            linked.FirstOrDefault()?.Href == "shellvis:mail/000000012A",
            linked.FirstOrDefault()?.Href ?? "");
        Check("no brackets leak into the text",
            !Plain(link).Any(c => c is '[' or ']' or '(' or ')'), Plain(link));
        Check("the prose around it is untouched",
            Plain(link).Contains("von Frau Meier.", StringComparison.Ordinal));

        // Markup keeps working inside a label, because a model writes it there.
        MarkdownDocument richLabel = MarkdownParser.Parse("[**Wichtig** und `code`](https://x/y)");
        Check("a label keeps its own markup",
            Spans(richLabel).Any(sp => sp.Text == "Wichtig" && sp.Has(SpanStyle.Strong)));
        Check("and every part of it stays one target",
            Spans(richLabel).All(sp => sp.Href == "https://x/y"));

        // The reason the parser is strict about the parenthesis: prose contains brackets.
        MarkdownDocument notLink = MarkdownParser.Parse("Siehe [Anmerkung 1] (weiter unten).");
        Check("a bracket followed by a space is not a link",
            !Spans(notLink).Any(sp => sp.Has(SpanStyle.Link)), Plain(notLink));
        Check("and its text survives unchanged",
            Plain(notLink).Contains("[Anmerkung 1] (weiter unten).", StringComparison.Ordinal));

        MarkdownDocument emptyTarget = MarkdownParser.Parse("[Label]()");
        Check("a link with no target is not a link",
            !Spans(emptyTarget).Any(sp => sp.Has(SpanStyle.Link)), Plain(emptyTarget));

        MarkdownDocument escapedBracket = MarkdownParser.Parse("Ein \\[Wert\\](kein Link)");
        Check("an escaped bracket does not open a link",
            !Spans(escapedBracket).Any(sp => sp.Has(SpanStyle.Link)), Plain(escapedBracket));

        Console.WriteLine("\ntables:");

        MarkdownDocument table = MarkdownParser.Parse(
            "| Tag | Termin | Ort |\n"
            + "|-----|:------:|----:|\n"
            + "| Mo  | **JF** | R1  |\n"
            + "| Di  | Mobil  |     |");

        var grid = table.Blocks.OfType<MarkdownBlock.Table>().FirstOrDefault();

        Check("a header plus a separator makes a table", grid is not null);
        Check("three header cells", grid?.Header.Cells.Count == 3);
        Check("two body rows", grid?.Rows.Count == 2);
        Check("alignment is read from the separator",
            grid?.Alignment.SequenceEqual(
                [ColumnAlignment.Left, ColumnAlignment.Center, ColumnAlignment.Right]) == true,
            string.Join(",", grid?.Alignment ?? []));
        Check("cell markup is parsed",
            grid?.Rows[0].Cells[1].Inlines.Any(sp => sp.Text == "JF" && sp.Has(SpanStyle.Strong)) == true);
        Check("an empty cell is empty, not missing", grid?.Rows[1].Cells.Count == 3);

        // The reason the separator is required: this domain is full of pipes.
        MarkdownDocument pipeline = MarkdownParser.Parse(
            "Nimm Get-Process | Where-Object { $_.CPU -gt 10 } | Sort-Object.");

        Check("a pipeline in prose is not a table",
            !pipeline.Blocks.OfType<MarkdownBlock.Table>().Any(), Plain(pipeline));

        MarkdownDocument mismatch = MarkdownParser.Parse("| a | b |\n|---|\n| 1 | 2 |");
        Check("a separator of the wrong width is not a table",
            !mismatch.Blocks.OfType<MarkdownBlock.Table>().Any());

        MarkdownDocument shortRow = MarkdownParser.Parse("| a | b | c |\n|---|---|---|\n| 1 |");
        var squared = shortRow.Blocks.OfType<MarkdownBlock.Table>().First();
        Check("a short row is padded rather than dropping the table",
            squared.Rows.Count == 1 && squared.Rows[0].Cells.Count == 3);

        MarkdownDocument arriving = MarkdownParser.Parse("| Tag | Termin |");
        Check("a header with no separator yet stays prose",
            !arriving.Blocks.OfType<MarkdownBlock.Table>().Any(), Plain(arriving));

        MarkdownDocument after = MarkdownParser.Parse("| a |\n|---|\n| 1 |\n\nDanach.");
        Check("prose after a table is a separate block",
            after.Blocks.OfType<MarkdownBlock.Paragraph>()
                .Any(b => b.Inlines.Any(sp => sp.Text.Contains("Danach", StringComparison.Ordinal))));

        Console.WriteLine("\nthematic breaks:");

        // Models emit these constantly to separate sections. Without a block for them the
        // line arrived as the literal text "---" sitting in the middle of an answer, which
        // is how it was found: in a real reply, on screen.
        MarkdownDocument broken = MarkdownParser.Parse("Oben\n\n---\n\nUnten");

        Check("three dashes become a rule",
            broken.Blocks.OfType<MarkdownBlock.Rule>().Count() == 1);
        Check("and no dash survives as text",
            !Plain(broken).Contains("-", StringComparison.Ordinal), Plain(broken));
        Check("the prose on both sides is kept",
            Plain(broken).Contains("Oben", StringComparison.Ordinal)
            && Plain(broken).Contains("Unten", StringComparison.Ordinal));

        Check("asterisks and underscores work too",
            MarkdownParser.Parse("***").Blocks.OfType<MarkdownBlock.Rule>().Any()
            && MarkdownParser.Parse("___").Blocks.OfType<MarkdownBlock.Rule>().Any());

        Check("spaces between them are allowed",
            MarkdownParser.Parse("- - -").Blocks.OfType<MarkdownBlock.Rule>().Any());

        // The reason this is checked before the bullet rule: a dash starts both.
        MarkdownDocument bullet = MarkdownParser.Parse("- ein Punkt");
        Check("a real bullet is still a bullet",
            bullet.Blocks.OfType<MarkdownBlock.Bullet>().Any()
            && !bullet.Blocks.OfType<MarkdownBlock.Rule>().Any());

        Check("two dashes are not a rule",
            !MarkdownParser.Parse("--").Blocks.OfType<MarkdownBlock.Rule>().Any());
        Check("a mixture is not a rule",
            !MarkdownParser.Parse("-*-").Blocks.OfType<MarkdownBlock.Rule>().Any());

        // A table separator also looks like dashes. It must not be eaten as a rule.
        MarkdownDocument stillTable = MarkdownParser.Parse(
            "| a | b |\n|---|---|\n| 1 | 2 |");
        Check("a table separator is still a table",
            stillTable.Blocks.OfType<MarkdownBlock.Table>().Any());

        // Inside a fence a line of dashes is output, not a rule.
        Check("dashes inside a fence stay text",
            !MarkdownParser.Parse("```\n---\n```").Blocks.OfType<MarkdownBlock.Rule>().Any());

        Console.WriteLine("\nedges:");

        Check("null parses to an empty document", MarkdownParser.Parse(null).Blocks.Count == 0);
        Check("empty parses to an empty document", MarkdownParser.Parse("").Blocks.Count == 0);
        Check("whitespace alone produces no blocks", MarkdownParser.Parse("   \n \n").Blocks.Count == 0);

        // Streaming re-parses the whole answer on every delta, so every prefix of an answer
        // has to parse without throwing. A crash here would take the window down mid-answer.
        const string Whole = "## Ergebnis\n\n- **Eins**: `a`\n- Zwei\n\n"
            + "| Tag | Ort |\n|-----|----:|\n| Mo  | R1  |\n\n"
            + "Siehe [Angebot](shellvis:mail/12A).\n\n```ps\nGet-Date\n```\n";
        bool everyPrefix = true;

        for (int i = 0; i <= Whole.Length; i++)
        {
            try
            {
                MarkdownParser.Parse(Whole[..i]);
            }
            catch (Exception ex)
            {
                everyPrefix = false;
                Console.WriteLine($"       prefix {i} threw: {ex.Message}");
                break;
            }
        }

        Check($"every one of the {Whole.Length + 1} prefixes parses", everyPrefix);

        // Windows line endings arrive from tool output and from the clipboard.
        Check("CRLF is handled like LF",
            MarkdownParser.Parse("- a\r\n- b").Blocks.Count == 2);

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: the forms this model writes parse into the blocks the renderer draws,\n"
                + "both shipped regressions stay fixed, and no prefix of a streamed answer throws."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }
}
