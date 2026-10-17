using System.Text;
using ShapeCrawler;

namespace Shellvis.Core.Office;

/// <summary>
/// Writes a .pptx without PowerPoint installed.
///
/// Input is Markdown with slides separated by a horizontal rule. Each slide takes a
/// heading for its title, bullet lines for its body, and an optional
/// <c>Notes:</c> line for the speaker notes:
///
/// <code>
/// # Quarterly review
/// - Revenue up 12%
/// - Two new markets
/// Notes: open with the revenue chart
/// ---
/// # Next quarter
/// - Hiring two engineers
/// </code>
///
/// The same reasoning as the Word writer: a model writes this fluently, whereas a tool
/// taking parallel arrays of titles and bullet lists gets filled in badly and cannot
/// express which bullet belongs to which slide.
/// </summary>
public static class SlideWriter
{
    /// <summary>Create a presentation. Overwrites the file.</summary>
    public static string Create(string path, string markdown, string? deckTitle = null)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        List<SlideSpec> slides = Parse(markdown);

        if (slides.Count == 0)
            return "error: no slides could be read from the markdown. "
                + "Separate slides with --- and give each one a # heading.";

        var presentation = new Presentation();

        // A new Presentation starts EMPTY in ShapeCrawler 0.80, so every slide has to
        // be added. An earlier revision assumed one blank starter slide and reused it
        // for the first spec, which failed with "slide number 1 exceeds the number of
        // slides 0" -- a clear enough message, but only once the assumption is named.
        //
        // Slide(int) is ONE-based here, unlike almost everything else in .NET.
        for (int i = 0; i < slides.Count; i++)
        {
            presentation.Slides.Add(LayoutNumberFor(slides[i]));
            Fill(presentation.Slide(i + 1), slides[i]);
        }

        presentation.Save(full);

        var info = new FileInfo(full);
        return $"wrote {full} ({slides.Count} slide(s), {info.Length:N0} bytes)";
    }

    /// <summary>Read a presentation back as text, for verification.</summary>
    public static string Read(string path, int maxSlides = 50)
    {
        string full = Path.GetFullPath(path);

        if (!File.Exists(full))
            return $"error: no file at {full}";

        var presentation = new Presentation(full);
        var sb = new StringBuilder();

        sb.Append(presentation.Slides.Count).Append(" slide(s) in ").AppendLine(full);

        int shown = 0;
        foreach (IUserSlide slide in presentation.Slides)
        {
            if (shown++ >= maxSlides)
            {
                sb.Append("... ").Append(presentation.Slides.Count - maxSlides)
                  .AppendLine(" more slide(s) not shown");
                break;
            }

            sb.Append("\n--- slide ").Append(shown).AppendLine(" ---");

            foreach (IShape shape in slide.Shapes)
            {
                // Not every shape holds text: pictures, lines and tables do not, and
                // asking them for it throws rather than returning empty.
                string? text = TryReadText(shape);
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text.Trim());
            }
        }

        return sb.ToString();
    }

    private static string? TryReadText(IShape shape)
    {
        try
        {
            return shape.TextBox?.Text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pick a layout, by one-based number in the default template: 1 is the title
    /// slide, 2 is Title and Content. A slide with no bullets reads far better on the
    /// title layout than as a content slide with an empty body placeholder.
    /// </summary>
    private static int LayoutNumberFor(SlideSpec spec) => spec.Bullets.Count == 0 ? 1 : 2;

    private static void Fill(IUserSlide slide, SlideSpec spec)
    {
        // Placeholders are addressed by position rather than by name: the names differ
        // between templates and locales, while the first text placeholder being the
        // title holds across all of them.
        List<IShape> textShapes = slide.Shapes
            .Where(s => TryReadText(s) is not null)
            .ToList();

        if (textShapes.Count > 0)
            SetText(textShapes[0], spec.Title);

        if (spec.Bullets.Count > 0 && textShapes.Count > 1)
            SetText(textShapes[1], string.Join('\n', spec.Bullets));

        if (spec.Notes is { Length: > 0 })
            TrySetNotes(slide, spec.Notes);
    }

    private static void SetText(IShape shape, string text)
    {
        try
        {
            if (shape.TextBox is { } box)
                box.SetText(text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A placeholder that refuses text is not worth failing the whole deck for.
        }
    }

    private static void TrySetNotes(IUserSlide slide, string notes)
    {
        try
        {
            // 0.80 takes the notes as lines rather than one string; splitting keeps
            // multi-line notes as separate paragraphs instead of one run with
            // embedded newlines.
            slide.AddNotes(notes.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Speaker notes are a nice-to-have; the slides are the deliverable.
        }
    }

    private static List<SlideSpec> Parse(string markdown)
    {
        var slides = new List<SlideSpec>();

        string[] chunks = markdown
            .ReplaceLineEndings("\n")
            .Split("\n---", StringSplitOptions.None);

        foreach (string chunk in chunks)
        {
            string title = string.Empty;
            var bullets = new List<string>();
            string? notes = null;

            foreach (string raw in chunk.Split('\n'))
            {
                string line = raw.Trim();

                if (line.Length == 0 || line == "---")
                    continue;

                if (line.StartsWith('#'))
                {
                    title = line.TrimStart('#').Trim();
                    continue;
                }

                if (line.StartsWith("Notes:", StringComparison.OrdinalIgnoreCase))
                {
                    notes = line["Notes:".Length..].Trim();
                    continue;
                }

                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    bullets.Add(line[2..].Trim());
                    continue;
                }

                // Prose without a bullet marker is still content the user wrote, so it
                // becomes a bullet rather than being discarded.
                bullets.Add(line);
            }

            if (title.Length > 0 || bullets.Count > 0)
                slides.Add(new SlideSpec(title.Length > 0 ? title : "Slide", bullets, notes));
        }

        return slides;
    }

    private sealed record SlideSpec(string Title, List<string> Bullets, string? Notes);
}
