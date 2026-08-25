using System.Globalization;
using System.Text.Json.Nodes;

namespace Shellvis.Core.Browser;

/// <summary>
/// Reads and acts on the page currently being driven.
///
/// The whole design rests on one decision, carried over from the desktop tools and from
/// Hermes' browser tool before them: the model addresses elements by REFERENCE from a
/// snapshot, never by coordinate or selector. A coordinate is wrong the moment the page
/// scrolls or a banner appears; a hand-written selector is a second guess layered on top
/// of the first. A reference either still resolves or fails loudly, and failing loudly
/// is what lets the model recover in one round instead of acting on the wrong element.
/// </summary>
public sealed class PageDriver(BrowserHost host)
{
    private readonly BrowserHost _host = host;

    /// <summary>
    /// Builds the reference tree, in the page.
    ///
    /// Done in one injected script rather than by walking the DOM over the protocol:
    /// a DOM.getDocument walk is a round trip per node and a page of a few thousand
    /// nodes then takes seconds. It also keeps reference numbering and rendering in the
    /// same place, so a reference cannot mean one thing to the writer and another to
    /// the reader.
    ///
    /// The references live in a page-side array. That array dies with the document,
    /// which is the intended behaviour: after a navigation every reference fails, rather
    /// than silently pointing at whatever now occupies that index.
    /// </summary>
    private const string SnapshotScript = """
    (() => {
      const refs = [];
      window.__shellvisRefs = refs;
      const out = [];
      const MAX = __MAX__;
      let truncated = false;

      const visible = (el) => {
        const r = el.getBoundingClientRect();
        if (r.width <= 0 || r.height <= 0) return false;
        const s = getComputedStyle(el);
        return s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
      };

      const clean = (t) => {
        t = (t || '').replace(/\s+/g, ' ').trim();
        return t.length > 120 ? t.slice(0, 120) + '...' : t;
      };

      const label = (el) => {
        let t = el.getAttribute('aria-label') || el.getAttribute('placeholder')
             || el.getAttribute('title') || el.getAttribute('alt') || '';
        if (!t) {
          const own = Array.from(el.childNodes)
            .filter(n => n.nodeType === 3).map(n => n.textContent).join(' ');
          t = own.trim() ? own : (el.innerText || '');
        }
        return clean(t);
      };

      const INTERACTIVE_TAGS =
        ['a','button','input','select','textarea','summary','option','label'];
      const INTERACTIVE_ROLES =
        ['button','link','checkbox','radio','tab','menuitem','menuitemcheckbox',
         'textbox','combobox','switch','option','searchbox','slider'];
      const STRUCTURAL_TAGS = ['h1','h2','h3','h4','h5','h6','legend','th','caption'];

      const interactive = (el) => {
        const tag = el.tagName.toLowerCase();
        if (tag === 'label') return false;
        if (INTERACTIVE_TAGS.includes(tag)) return true;
        if (el.hasAttribute('onclick')) return true;
        const role = el.getAttribute('role');
        if (role && INTERACTIVE_ROLES.includes(role)) return true;
        if (el.isContentEditable) return true;
        const ti = el.getAttribute('tabindex');
        return ti !== null && ti !== '-1';
      };

      const describe = (el) => {
        const tag = el.tagName.toLowerCase();
        let kind = tag;
        if (tag === 'input') kind = 'input[' + (el.type || 'text') + ']';
        const role = el.getAttribute('role');
        if (role && role !== tag) kind += '/' + role;

        let line = kind;
        const text = label(el);
        if (text) line += ' "' + text + '"';

        if (tag === 'a' && el.href) {
          let href = el.href;
          if (href.length > 80) href = href.slice(0, 80) + '...';
          line += ' -> ' + href;
        }
        // A checkbox's value is the constant "on" and says nothing; [checked] below
        // carries the state that matters. Printing both is noise on every line.
        const valueless = el.type === 'password' || el.type === 'checkbox' || el.type === 'radio';
        if (('value' in el) && el.value && !valueless)
          line += ' = "' + clean(el.value) + '"';
        if (el.type === 'password' && el.value) line += ' = (set)';
        if (el.checked === true) line += ' [checked]';
        if (el.disabled === true) line += ' [disabled]';
        if (el.getAttribute('aria-expanded'))
          line += ' [expanded=' + el.getAttribute('aria-expanded') + ']';
        return line;
      };

      const walk = (el, depth) => {
        if (out.length >= MAX) { truncated = true; return; }
        const tag = el.tagName.toLowerCase();
        if (tag === 'script' || tag === 'style' || tag === 'noscript' || tag === 'svg') return;

        let emitted = false;
        if (visible(el)) {
          if (interactive(el)) {
            refs.push(el);
            out.push('  '.repeat(Math.min(depth, 8)) + '@e' + refs.length + ' ' + describe(el));
            emitted = true;
          } else if (STRUCTURAL_TAGS.includes(tag)) {
            const t = label(el);
            if (t) {
              out.push('  '.repeat(Math.min(depth, 8)) + tag + ' "' + t + '"');
              emitted = true;
            }
          }
        }

        for (const child of el.children) walk(child, emitted ? depth + 1 : depth);
      };

      if (document.body) walk(document.body, 0);

      let header = document.title + '\n' + location.href;
      if (truncated) header += '\n(truncated at ' + MAX + ' nodes; narrow the page or scroll)';
      if (out.length === 0)
        header += '\nNo visible interactive elements. The page may still be loading.';
      return header + '\n' + out.join('\n');
    })()
    """;

    /// <summary>Read the page as a reference tree.</summary>
    public async Task<string> SnapshotAsync(
        int maxNodes = 300, CancellationToken cancellationToken = default)
    {
        string script = SnapshotScript.Replace(
            "__MAX__", maxNodes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        return await EvaluateStringAsync(script, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Go to a url and wait for the document to settle.</summary>
    public async Task<string> NavigateAsync(
        string url, int timeoutSeconds = 30, CancellationToken cancellationToken = default)
    {
        JsonNode? result = await _host.SendAsync(
            "Page.navigate",
            new JsonObject { ["url"] = url },
            cancellationToken).ConfigureAwait(false);

        // Chrome reports a refusal here rather than by failing the command, so this is
        // the only place a blocked navigation becomes visible.
        if (result?["errorText"]?.GetValue<string>() is { Length: > 0 } error)
            return $"Navigation to {url} failed: {error}";

        bool loaded = await WaitForLoadAsync(timeoutSeconds, cancellationToken).ConfigureAwait(false);

        string where = await _host.GetUrlAsync(cancellationToken).ConfigureAwait(false);

        return loaded
            ? $"Loaded {where}"
            : $"Navigated to {where} but the document was still loading after "
                + $"{timeoutSeconds}s. Snapshot it to see what is there.";
    }

    /// <summary>
    /// Wait until the document reports itself complete.
    ///
    /// Polling readyState rather than waiting for Page.loadEventFired, because a page
    /// that was already loaded when the command was issued never fires that event again
    /// and the wait would hang for the full timeout.
    /// </summary>
    private async Task<bool> WaitForLoadAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                string state = await EvaluateStringAsync(
                    "document.readyState", cancellationToken).ConfigureAwait(false);

                if (state == "complete")
                    return true;
            }
            catch (CdpException)
            {
                // Mid-navigation the execution context is torn down and recreated;
                // that is expected here rather than a failure.
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Step back in this tab's history.</summary>
    public async Task<string> BackAsync(CancellationToken cancellationToken = default)
    {
        JsonNode? history = await _host
            .SendAsync("Page.getNavigationHistory", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        int index = history?["currentIndex"]?.GetValue<int>() ?? 0;

        if (index <= 0)
            return "There is nothing to go back to in this tab.";

        int id = history?["entries"]?[index - 1]?["id"]?.GetValue<int>()
            ?? throw new CdpException("The history entry carried no id.");

        await _host.SendAsync(
            "Page.navigateToHistoryEntry",
            new JsonObject { ["entryId"] = id },
            cancellationToken).ConfigureAwait(false);

        await WaitForLoadAsync(15, cancellationToken).ConfigureAwait(false);

        return "Went back to " + await _host.GetUrlAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Click a referenced element with real input events.
    ///
    /// Note the deliberate difference from the desktop tools, which prefer UI Automation
    /// patterns over synthetic mouse input. There, a pattern invoke is better because it
    /// needs no foreground window and cannot be intercepted. Here the opposite holds:
    /// CDP's dispatched events ARE trusted by the page, while a JavaScript
    /// element.click() bypasses whatever is lying on top of the element -- so it can
    /// report success on a button hidden behind a cookie banner, which is the single
    /// most common way browser automation lies about what it did. So: hit-test first,
    /// then dispatch real events.
    /// </summary>
    public async Task<string> ClickAsync(
        string reference,
        string button = "left",
        int clickCount = 1,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseReference(reference, out int index, out string? bad))
            return bad!;

        string probe = """
        (() => {
          const refs = window.__shellvisRefs;
          if (!refs) return 'STALE';
          const el = refs[__I__ - 1];
          if (!el) return 'MISSING';
          if (!el.isConnected) return 'DETACHED';
          el.scrollIntoView({block: 'center', inline: 'center'});
          const r = el.getBoundingClientRect();
          if (r.width <= 0 || r.height <= 0) return 'INVISIBLE';
          const x = r.left + r.width / 2, y = r.top + r.height / 2;
          const hit = document.elementFromPoint(x, y);
          const covered = !hit || !(el === hit || el.contains(hit) || hit.contains(el));
          let coverDesc = '';
          if (covered && hit) {
            coverDesc = hit.tagName.toLowerCase()
              + (hit.className && typeof hit.className === 'string'
                  ? '.' + hit.className.split(/\s+/).filter(Boolean).slice(0, 2).join('.')
                  : '');
          }
          return JSON.stringify({x: x, y: y, covered: covered, cover: coverDesc,
                                 tag: el.tagName.toLowerCase(),
                                 text: (el.innerText || el.value || '').replace(/\s+/g,' ').trim().slice(0, 60)});
        })()
        """.Replace("__I__", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        string raw = await EvaluateStringAsync(probe, cancellationToken).ConfigureAwait(false);

        if (Explain(raw, reference) is { } problem)
            return problem;

        JsonNode? box = JsonNode.Parse(raw);

        double x = box?["x"]?.GetValue<double>() ?? 0;
        double y = box?["y"]?.GetValue<double>() ?? 0;

        if (box?["covered"]?.GetValue<bool>() == true)
        {
            string cover = box["cover"]?.GetValue<string>() ?? "another element";

            // Reported rather than clicked through: the thing on top is usually a
            // consent dialog or a modal, and dismissing it is the real next step.
            return $"{reference} is covered by <{cover}> at that point, so clicking there "
                + "would hit the wrong element. Snapshot the page and deal with what is "
                + "on top first.";
        }

        await DispatchMouseAsync("mouseMoved", x, y, button, 0, cancellationToken).ConfigureAwait(false);
        await DispatchMouseAsync("mousePressed", x, y, button, clickCount, cancellationToken).ConfigureAwait(false);
        await DispatchMouseAsync("mouseReleased", x, y, button, clickCount, cancellationToken).ConfigureAwait(false);

        string what = box?["text"]?.GetValue<string>() is { Length: > 0 } text
            ? $"<{box?["tag"]?.GetValue<string>()}> \"{text}\""
            : $"<{box?["tag"]?.GetValue<string>()}>";

        return $"Clicked {reference} {what}. Snapshot the page to see what changed.";
    }

    private Task DispatchMouseAsync(
        string type, double x, double y, string button, int clickCount,
        CancellationToken cancellationToken) =>
        _host.SendAsync(
            "Input.dispatchMouseEvent",
            new JsonObject
            {
                ["type"] = type,
                ["x"] = x,
                ["y"] = y,
                ["button"] = button,
                ["clickCount"] = clickCount,
                // Without the button bitmask a press is delivered with no button held,
                // and drag-aware pages ignore it.
                ["buttons"] = type == "mouseMoved" ? 0 : button == "right" ? 2 : 1,
            },
            cancellationToken);

    /// <summary>Put text into a referenced field.</summary>
    public async Task<string> TypeAsync(
        string reference,
        string text,
        bool replace = true,
        bool pressEnter = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseReference(reference, out int index, out string? bad))
            return bad!;

        string focus = """
        (() => {
          const refs = window.__shellvisRefs;
          if (!refs) return 'STALE';
          const el = refs[__I__ - 1];
          if (!el) return 'MISSING';
          if (!el.isConnected) return 'DETACHED';
          if (el.disabled) return 'DISABLED';
          el.scrollIntoView({block: 'center'});
          el.focus();
          if (document.activeElement !== el && !el.isContentEditable) return 'NOFOCUS';
          if (__REPLACE__) {
            if ('value' in el) {
              el.value = '';
              el.dispatchEvent(new Event('input', {bubbles: true}));
            } else if (el.isContentEditable) {
              el.textContent = '';
            }
          }
          return 'OK';
        })()
        """
            .Replace("__I__", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__REPLACE__", replace ? "true" : "false", StringComparison.Ordinal);

        string state = await EvaluateStringAsync(focus, cancellationToken).ConfigureAwait(false);

        if (Explain(state, reference) is { } problem)
            return problem;

        if (state == "NOFOCUS")
            return $"{reference} would not take focus, so it cannot be typed into.";

        // insertText rather than a key event per character: it is one round trip instead
        // of one per letter, and it still raises beforeinput/input, which is what
        // component frameworks listen for. Per-character key events are only needed for
        // fields that filter keystrokes, and those are rare enough to handle with
        // browser_press when they turn up.
        await _host.SendAsync(
            "Input.insertText",
            new JsonObject { ["text"] = text },
            cancellationToken).ConfigureAwait(false);

        if (pressEnter)
            await PressAsync("Enter", cancellationToken: cancellationToken).ConfigureAwait(false);

        string shown = text.Length > 60 ? text[..60] + "..." : text;

        return $"Typed \"{shown}\" into {reference}"
            + (pressEnter ? " and pressed Enter." : ".");
    }

    /// <summary>Named keys, so the model does not have to know virtual key codes.</summary>
    private static readonly Dictionary<string, (string Key, string Code, int VirtualKey)> Keys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = ("Enter", "Enter", 13),
            ["tab"] = ("Tab", "Tab", 9),
            ["escape"] = ("Escape", "Escape", 27),
            ["esc"] = ("Escape", "Escape", 27),
            ["backspace"] = ("Backspace", "Backspace", 8),
            ["delete"] = ("Delete", "Delete", 46),
            ["up"] = ("ArrowUp", "ArrowUp", 38),
            ["down"] = ("ArrowDown", "ArrowDown", 40),
            ["left"] = ("ArrowLeft", "ArrowLeft", 37),
            ["right"] = ("ArrowRight", "ArrowRight", 39),
            ["home"] = ("Home", "Home", 36),
            ["end"] = ("End", "End", 35),
            ["pageup"] = ("PageUp", "PageUp", 33),
            ["pagedown"] = ("PageDown", "PageDown", 34),
            ["space"] = (" ", "Space", 32),
        };

    /// <summary>Send a key to whatever has focus.</summary>
    public async Task<string> PressAsync(
        string key, CancellationToken cancellationToken = default)
    {
        if (!Keys.TryGetValue(key.Trim(), out (string Key, string Code, int VirtualKey) mapped))
        {
            return $"'{key}' is not a key I send. Known: "
                + string.Join(", ", Keys.Keys.Distinct(StringComparer.OrdinalIgnoreCase))
                + ". For ordinary characters use browser_type.";
        }

        foreach (string type in (string[])["keyDown", "keyUp"])
        {
            var parameters = new JsonObject
            {
                ["type"] = type,
                ["key"] = mapped.Key,
                ["code"] = mapped.Code,
                ["windowsVirtualKeyCode"] = mapped.VirtualKey,
                ["nativeVirtualKeyCode"] = mapped.VirtualKey,
            };

            // Enter and Space also produce text; without it a form listening for
            // keypress rather than keydown never fires.
            if (type == "keyDown" && mapped.VirtualKey is 13 or 32)
                parameters["text"] = mapped.VirtualKey == 13 ? "\r" : " ";

            await _host.SendAsync("Input.dispatchKeyEvent", parameters, cancellationToken)
                .ConfigureAwait(false);
        }

        return $"Pressed {mapped.Key}.";
    }

    /// <summary>Scroll the page or a referenced element into view.</summary>
    public async Task<string> ScrollAsync(
        string? reference = null,
        int pages = 1,
        CancellationToken cancellationToken = default)
    {
        if (reference is { Length: > 0 })
        {
            if (!TryParseReference(reference, out int index, out string? bad))
                return bad!;

            string script = """
            (() => {
              const refs = window.__shellvisRefs;
              if (!refs) return 'STALE';
              const el = refs[__I__ - 1];
              if (!el) return 'MISSING';
              if (!el.isConnected) return 'DETACHED';
              el.scrollIntoView({block: 'center', inline: 'center'});
              return 'OK';
            })()
            """.Replace("__I__", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            string state = await EvaluateStringAsync(script, cancellationToken).ConfigureAwait(false);

            if (Explain(state, reference) is { } problem)
                return problem;

            return $"Scrolled {reference} into view. References still hold; the snapshot "
                + "will now show what is around it.";
        }

        // Placeholder substitution rather than interpolation: JavaScript's braces and a
        // raw interpolated string's delimiters collide constantly, and the escaping
        // becomes the least readable part of the file.
        string scroll = """
        (() => {
          const before = window.scrollY;
          window.scrollBy(0, window.innerHeight * __PAGES__);
          return before + ' -> ' + window.scrollY + ' of '
               + (document.documentElement.scrollHeight - window.innerHeight);
        })()
        """.Replace("__PAGES__", pages.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        string moved = await EvaluateStringAsync(scroll, cancellationToken).ConfigureAwait(false);

        return $"Scrolled {pages} page(s): {moved}px.";
    }

    /// <summary>
    /// Capture the viewport to a file.
    ///
    /// To a file and not into the result, for the same reason the desktop capture does:
    /// a full-page PNG base64-encoded is megabytes of context spent in one call, and the
    /// model usually wants the reference tree anyway.
    /// </summary>
    public async Task<string> ScreenshotAsync(
        string? path = null,
        bool fullPage = false,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject { ["format"] = "png" };

        if (fullPage)
            parameters["captureBeyondViewport"] = true;

        JsonNode? result = await _host
            .SendAsync("Page.captureScreenshot", parameters, cancellationToken)
            .ConfigureAwait(false);

        string? base64 = result?["data"]?.GetValue<string>();

        if (string.IsNullOrEmpty(base64))
            return "The browser returned no image data.";

        string target = path is { Length: > 0 }
            ? path
            : Path.Combine(
                Path.GetTempPath(),
                $"shellvis-page-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png");

        byte[] bytes = Convert.FromBase64String(base64);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);

        return $"Saved {(fullPage ? "the full page" : "the viewport")} to {target} "
            + $"({bytes.Length / 1024} KB).";
    }

    /// <summary>Read text out of the page, either all of it or one element's.</summary>
    public async Task<string> ReadTextAsync(
        string? reference = null,
        int maxChars = 4000,
        CancellationToken cancellationToken = default)
    {
        string script;

        if (reference is { Length: > 0 })
        {
            if (!TryParseReference(reference, out int index, out string? bad))
                return bad!;

            script = """
            (() => {
              const refs = window.__shellvisRefs;
              if (!refs) return 'STALE';
              const el = refs[__I__ - 1];
              if (!el) return 'MISSING';
              if (!el.isConnected) return 'DETACHED';
              return (el.innerText || el.value || '').trim();
            })()
            """.Replace("__I__", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        else
        {
            script = "(document.body ? document.body.innerText : '').trim()";
        }

        string text = await EvaluateStringAsync(script, cancellationToken).ConfigureAwait(false);

        if (reference is { Length: > 0 } && Explain(text, reference) is { } problem)
            return problem;

        if (text.Length <= maxChars)
            return text.Length == 0 ? "The element or page has no visible text." : text;

        return text[..maxChars]
            + $"\n... truncated at {maxChars} of {text.Length} characters.";
    }

    /// <summary>Run arbitrary JavaScript. The escape hatch, and named as one.</summary>
    public Task<string> EvaluateAsync(
        string expression, CancellationToken cancellationToken = default) =>
        EvaluateStringAsync(expression, cancellationToken);

    private async Task<string> EvaluateStringAsync(
        string expression, CancellationToken cancellationToken)
    {
        JsonNode? result = await _host.SendAsync(
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                // The snapshot script is an IIFE, but a caller-supplied expression may
                // be an await, and refusing those would be a needless limitation.
                ["awaitPromise"] = true,
                // Marks the call as user-initiated, which some APIs (fullscreen,
                // clipboard, autoplay) require before they will do anything.
                ["userGesture"] = true,
            },
            cancellationToken).ConfigureAwait(false);

        if (result?["exceptionDetails"] is JsonObject exception)
        {
            string text = exception["exception"]?["description"]?.GetValue<string>()
                ?? exception["text"]?.GetValue<string>()
                ?? "the script threw";

            throw new CdpException(text);
        }

        JsonNode? value = result?["result"]?["value"];

        if (value is not null)
            return value.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? value.GetValue<string>()
                : value.ToJsonString();

        // undefined has no value member; saying so is better than an empty string that
        // reads like a blank page.
        return result?["result"]?["type"]?.GetValue<string>() == "undefined"
            ? "(undefined)"
            : string.Empty;
    }

    /// <summary>
    /// Read an @eN reference, or explain why it is not one.
    ///
    /// Returns the problem as text rather than throwing, for the same reason every other
    /// tool failure in this project is text: a model that is told what shape a reference
    /// has fixes its call in the next round, while an exception reads as a broken tool
    /// and gets retried unchanged. The convention is easy to break by accident -- this
    /// method threw until a probe caught it.
    /// </summary>
    private static bool TryParseReference(string reference, out int index, out string? problem)
    {
        problem = null;
        string trimmed = reference.Trim().TrimStart('@', 'e', 'E');

        if (int.TryParse(trimmed, CultureInfo.InvariantCulture, out index) && index > 0)
            return true;

        problem = $"'{reference}' is not an element reference. They look like @e12 and "
            + "come from browser_snapshot -- take one first and use a reference from it.";

        return false;
    }

    /// <summary>
    /// Turn a probe sentinel into an explanation, or null when the probe succeeded.
    ///
    /// Each case is a different mistake with a different remedy, and collapsing them
    /// into "element not found" is what makes a model retry the same thing.
    /// </summary>
    private static string? Explain(string state, string reference) => state switch
    {
        "STALE" =>
            "The page has no reference table -- it navigated or reloaded since the last "
            + "snapshot. Call browser_snapshot again; the old references are gone.",
        "MISSING" =>
            $"{reference} is not in the current snapshot. Call browser_snapshot and use a "
            + "reference from it.",
        "DETACHED" =>
            $"{reference} was removed from the page after the snapshot was taken. "
            + "Snapshot again to see what replaced it.",
        "INVISIBLE" =>
            $"{reference} has no size on screen, so there is nothing to click. It may be "
            + "inside a collapsed section that has to be opened first.",
        "DISABLED" => $"{reference} is disabled.",
        _ => null,
    };
}
