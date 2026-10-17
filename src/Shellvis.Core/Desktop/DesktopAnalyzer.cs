using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Shellvis.Core.Desktop;

/// <summary>
/// Captures the UI Automation tree of a window and resolves element references back
/// to live elements so they can be acted on.
///
/// One instance owns one UIA connection and the reference maps for the snapshots it
/// produced. It is deliberately stateful: the whole reference mechanism depends on
/// remembering which live element "@e12" meant, and that memory has to outlive the
/// tool call that produced the snapshot.
///
/// Not thread-safe. UIA is COM and wants a consistent apartment; drive one analyzer
/// from one thread.
/// </summary>
public sealed class DesktopAnalyzer : IDisposable
{
    /// <summary>
    /// Cap on nodes per snapshot. A real application easily exposes several thousand
    /// automation elements, the vast majority structural containers with no name and
    /// no behaviour. Past a few hundred the snapshot stops helping a model and starts
    /// crowding out its actual task.
    /// </summary>
    public const int DefaultMaxElements = 400;

    /// <summary>Depth cap. Deeply nested chrome is almost never what the agent wants.</summary>
    public const int DefaultMaxDepth = 12;

    private readonly UIA3Automation _automation = new();

    // Snapshot id -> (reference -> live element). Keeping only the most recent
    // snapshot per window would be tidier, but an agent legitimately interleaves
    // two windows, so the map is keyed by snapshot.
    private readonly Dictionary<string, Dictionary<string, AutomationElement>> _references = new();

    private int _snapshotCounter;

    /// <summary>
    /// Capture the tree of a specific window.
    /// </summary>
    /// <param name="windowHandle">Native handle, from <see cref="WindowInspector"/>.</param>
    /// <param name="maxElements">Node budget. Defaults to <see cref="DefaultMaxElements"/>.</param>
    /// <param name="maxDepth">Depth budget. Defaults to <see cref="DefaultMaxDepth"/>.</param>
    /// <param name="interactiveOnly">
    /// Keep only elements that can be acted on, plus the ancestors needed to reach
    /// them. Much smaller, and usually what an agent that intends to click wants;
    /// turn it off when the task is to READ the window rather than drive it.
    /// </param>
    public DesktopSnapshot Capture(
        nint windowHandle,
        int maxElements = DefaultMaxElements,
        int maxDepth = DefaultMaxDepth,
        bool interactiveOnly = false)
    {
        WindowInfo window = WindowInspector.Describe(windowHandle)
            ?? throw new InvalidOperationException(
                $"Window {windowHandle} is not visible or no longer exists.");

        AutomationElement root = _automation.FromHandle(windowHandle);

        string snapshotId = $"s{++_snapshotCounter}";
        var map = new Dictionary<string, AutomationElement>();
        var budget = new Budget(maxElements);

        UiElement tree = Build(root, map, budget, depth: 0, maxDepth, interactiveOnly);
        _references[snapshotId] = map;

        return new DesktopSnapshot(
            SnapshotId: snapshotId,
            Window: window,
            Root: tree,
            ElementCount: map.Count,
            WasTruncated: budget.Exhausted);
    }

    /// <summary>Capture the window the user is currently working in.</summary>
    public DesktopSnapshot CaptureForeground(
        int maxElements = DefaultMaxElements,
        int maxDepth = DefaultMaxDepth,
        bool interactiveOnly = false)
    {
        WindowInfo window = WindowInspector.Foreground()
            ?? throw new InvalidOperationException("No window currently has focus.");

        return Capture(window.Handle, maxElements, maxDepth, interactiveOnly);
    }

    /// <summary>
    /// Resolve a reference such as "e12" (with or without the leading @) back to a
    /// live element.
    ///
    /// Throws rather than returning null: acting on the wrong element is far worse
    /// than failing, and a stale reference is a real condition the agent must be told
    /// about so it re-captures instead of guessing.
    /// </summary>
    public AutomationElement Resolve(string snapshotId, string elementRef)
    {
        string key = elementRef.TrimStart('@');

        if (!_references.TryGetValue(snapshotId, out Dictionary<string, AutomationElement>? map))
        {
            throw new KeyNotFoundException(
                $"Snapshot '{snapshotId}' is unknown. Capture the window again.");
        }

        if (!map.TryGetValue(key, out AutomationElement? element))
        {
            throw new KeyNotFoundException(
                $"Reference '@{key}' does not exist in snapshot '{snapshotId}'.");
        }

        // The element object survives even after the control is destroyed, so
        // liveness has to be probed rather than assumed.
        try
        {
            _ = element.Properties.ProcessId.Value;
        }
        catch (Exception ex) when (ex is COMException or TimeoutException)
        {
            throw new InvalidOperationException(
                $"Reference '@{key}' is stale: the element no longer exists. "
                + "Capture the window again.", ex);
        }

        return element;
    }

    private UiElement Build(
        AutomationElement element,
        Dictionary<string, AutomationElement> map,
        Budget budget,
        int depth,
        int maxDepth,
        bool interactiveOnly)
    {
        string reference = $"e{map.Count + 1}";
        map[reference] = element;

        Snapshotted info = Read(element);
        var children = new List<UiElement>();

        if (depth < maxDepth && !budget.Exhausted)
        {
            foreach (AutomationElement child in SafeChildren(element))
            {
                if (budget.Take())
                {
                    // Budget spent. Stop rather than silently returning a partial
                    // subtree that looks complete.
                    break;
                }

                UiElement built = Build(child, map, budget, depth + 1, maxDepth, interactiveOnly);

                // An uninteresting container is still worth keeping if something
                // actionable lives beneath it, otherwise the reference chain breaks.
                if (!interactiveOnly || built.IsActionable || built.Children.Count > 0)
                    children.Add(built);
            }
        }

        return new UiElement(
            Ref: reference,
            ControlType: info.ControlType,
            Name: info.Name,
            Value: info.Value,
            IsEnabled: info.IsEnabled,
            IsOffscreen: info.IsOffscreen,
            Left: info.Left,
            Top: info.Top,
            Width: info.Width,
            Height: info.Height,
            Actions: info.Actions,
            Children: children);
    }

    /// <summary>
    /// Read the properties this snapshot cares about.
    ///
    /// Every single UIA property read is a cross-process call that can throw or time
    /// out if the target app is busy or shutting down. A snapshot that aborts because
    /// one control was mid-repaint is useless, so each read degrades to a default
    /// instead of propagating.
    /// </summary>
    private static Snapshotted Read(AutomationElement element)
    {
        string controlType = Try(() => element.Properties.ControlType.Value.ToString(), "Unknown");
        string name = Try(() => element.Properties.Name.ValueOrDefault ?? string.Empty, string.Empty);
        bool enabled = Try(() => element.Properties.IsEnabled.ValueOrDefault, true);
        bool offscreen = Try(() => element.Properties.IsOffscreen.ValueOrDefault, false);

        int left = 0, top = 0, width = 0, height = 0;
        try
        {
            var rect = element.BoundingRectangle;
            left = (int)rect.Left;
            top = (int)rect.Top;
            width = (int)rect.Width;
            height = (int)rect.Height;
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or NotSupportedException)
        {
            // Some elements genuinely have no bounds (virtualized list items).
        }

        return new Snapshotted(
            controlType, name, ReadValue(element), enabled, offscreen,
            left, top, width, height, ReadActions(element));
    }

    private static string? ReadValue(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
                return element.Patterns.Value.Pattern.Value.ValueOrDefault;

            if (element.Patterns.RangeValue.IsSupported)
                return element.Patterns.RangeValue.Pattern.Value.ValueOrDefault
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or NotSupportedException)
        {
        }

        return null;
    }

    /// <summary>
    /// Which interactions this element supports.
    ///
    /// Reported as capabilities rather than as raw pattern names so the model can map
    /// them straight onto the action tools it has available.
    /// </summary>
    private static IReadOnlyList<string> ReadActions(AutomationElement element)
    {
        var actions = new List<string>(4);

        Add(() => element.Patterns.Invoke.IsSupported, "Invoke");
        Add(() => element.Patterns.Toggle.IsSupported, "Toggle");
        Add(() => element.Patterns.Value.IsSupported && !element.Patterns.Value.Pattern.IsReadOnly, "SetValue");
        Add(() => element.Patterns.SelectionItem.IsSupported, "Select");
        Add(() => element.Patterns.ExpandCollapse.IsSupported, "Expand");
        Add(() => element.Patterns.Scroll.IsSupported, "Scroll");

        return actions;

        void Add(Func<bool> probe, string name)
        {
            try
            {
                if (probe())
                    actions.Add(name);
            }
            catch (Exception ex) when (ex is COMException or TimeoutException or NotSupportedException)
            {
            }
        }
    }

    private static AutomationElement[] SafeChildren(AutomationElement element)
    {
        try
        {
            return element.FindAllChildren();
        }
        catch (Exception ex) when (ex is COMException or TimeoutException)
        {
            return [];
        }
    }

    private static T Try<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or NotSupportedException)
        {
            return fallback;
        }
    }

    public void Dispose()
    {
        _references.Clear();
        _automation.Dispose();
    }

    private sealed record Snapshotted(
        string ControlType,
        string Name,
        string? Value,
        bool IsEnabled,
        bool IsOffscreen,
        int Left,
        int Top,
        int Width,
        int Height,
        IReadOnlyList<string> Actions);

    /// <summary>Mutable node budget shared across one recursive capture.</summary>
    private sealed class Budget(int max)
    {
        private int _remaining = max;

        public bool Exhausted { get; private set; }

        /// <summary>Consume one node. Returns true when the budget just ran out.</summary>
        public bool Take()
        {
            if (_remaining-- > 0)
                return false;

            Exhausted = true;
            return true;
        }
    }
}
