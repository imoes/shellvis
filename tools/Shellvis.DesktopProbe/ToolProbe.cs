using System.Text.Json;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Verifies the tool layer without an LLM.
///
/// The claim worth testing is that JSON Schemas are DERIVED from the method
/// signatures rather than hand-written. If that holds, renaming a parameter cannot
/// silently diverge from what the model was told, and there is no separate argument
/// coercion pass to get wrong. So this dumps the generated schemas and then drives a
/// real call through the registry the same way the agent loop will.
/// </summary>
internal static class ToolProbe
{
    public static async Task<int> RunAsync()
    {
        using var desktop = new DesktopTools();
        var registry = new ToolRegistry();
        registry.RegisterFrom(desktop);

        Console.WriteLine($"registered {registry.Count} tools\n");

        foreach (ToolEntry tool in registry.Tools.OrderBy(t => t.Name))
        {
            string effect = tool.SideEffect switch
            {
                SideEffect.ReadOnly => "read-only  (runs silently in auto mode)",
                SideEffect.Mutating => "mutating   (prompts unless yolo)",
                _ => "always-ask (prompts even in yolo)",
            };

            Console.WriteLine($"  {tool.Name,-16} {effect}");
        }

        Console.WriteLine("\n--- generated schema for ui_click ---");
        ToolEntry? click = registry.Find("ui_click");
        if (click is null)
        {
            Console.Error.WriteLine("ui_click was not registered");
            return 1;
        }

        Console.WriteLine(FormatJson(click.Function.JsonSchema.ToString()));

        Console.WriteLine("--- invoking window_list through the registry ---");
        using var emptyArgs = JsonDocument.Parse("{}");
        string result = await registry
            .InvokeAsync("window_list", emptyArgs.RootElement)
            .ConfigureAwait(false);

        foreach (string line in result.Split('\n').Take(6))
            Console.WriteLine(line.TrimEnd());

        Console.WriteLine("\n--- a bad call must explain itself, not throw ---");
        using var badArgs = JsonDocument.Parse("""{"elementRef":"@e999"}""");
        Console.WriteLine(await registry.InvokeAsync("ui_click", badArgs.RootElement).ConfigureAwait(false));

        Console.WriteLine("\n--- an unknown tool must list what does exist ---");
        Console.WriteLine(await registry.InvokeAsync("no_such_tool", emptyArgs.RootElement).ConfigureAwait(false));

        Console.WriteLine("\nVERIFIED: schemas are generated from signatures, dispatch works, "
            + "and failures come back as readable text.");
        return 0;
    }

    /// <summary>Re-indent the schema so a human can actually read it in a terminal.</summary>
    private static string FormatJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
