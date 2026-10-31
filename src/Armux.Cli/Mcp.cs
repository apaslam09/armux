using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Armux.Core;

namespace Armux.Cli;

/// <summary>
/// <c>armux mcp</c>: a Model Context Protocol server on stdio (newline-delimited JSON-RPC 2.0) that gives Claude Code
/// tools for armux's browser tabs. Each tool call is one request on the app's pipe (<see cref="Ipc"/>); the app does the
/// work through the tab's WebView2 (D-37). Written by hand instead of with an MCP SDK to keep NativeAOT free of
/// reflection and the process small.
/// </summary>
static class Mcp
{
    const string Protocol = "2025-06-18";

    sealed record Tool(string Name, string Description, string Command, (string Name, string Type, string Description, bool Required)[] Args);

    static readonly (string, string, string, bool) TabArg = ("tab", "integer", "Browser tab id from browser_tabs. Default: the focused browser tab, else one in this workspace.", false);

    static readonly Tool[] Tools =
    [
        new("browser_tabs", "List armux's browser tabs: id, state (visible, hidden, unloaded), workspace, title and URL. * marks the focused tab.", "browser-tabs", []),
        new("browser_open", "Open a page in a new armux browser tab, split beside this terminal. Returns the new tab id.", "open",
            [("url", "string", "Address, or words to search for.", true)]),
        new("browser_navigate", "Load an address (or search for words) in a browser tab and wait for the page.", "browser-navigate",
            [("url", "string", "Address, or words to search for.", true), TabArg]),
        new("browser_history", "Go back, go forward or reload.", "browser-history",
            [("action", "string", "back, forward or reload.", true), TabArg]),
        new("browser_snapshot", "The page's URL, title, headings and interactive elements, each with a ref like e12 for browser_click, browser_fill and browser_type. Take a new one after the page changes.", "browser-snapshot",
            [TabArg]),
        new("browser_text", "The visible text of the page.", "browser-text",
            [("max_chars", "integer", "Maximum characters to return (default 20000).", false), TabArg]),
        new("browser_click", "Click an element (a real mouse click inside the page only).", "browser-click",
            [("ref", "string", "Element ref from browser_snapshot (e12), or a CSS selector.", true), TabArg]),
        new("browser_fill", "Replace the text of an input or text area, or choose an option of a select element.", "browser-fill",
            [("ref", "string", "Element ref from browser_snapshot (e12), or a CSS selector.", true), ("value", "string", "Text to enter, or the option's text or value.", true), TabArg]),
        new("browser_type", "Type text into the focused element, or into the element given by ref, without clearing it.", "browser-type",
            [("text", "string", "Text to type.", true), ("ref", "string", "Element to focus first (optional).", false), TabArg]),
        new("browser_press_key", "Press a key in the page: Enter, Tab, Escape, Backspace, ArrowDown, PageDown, a letter, or a chord like Ctrl+A or Shift+Tab.", "browser-key",
            [("key", "string", "Key or chord.", true), TabArg]),
        new("browser_scroll", "Scroll the page by a number of pixels (negative scrolls up).", "browser-scroll",
            [("pixels", "integer", "Pixels to scroll (default 600).", false), TabArg]),
        new("browser_evaluate", "Run a JavaScript expression in the page and return its value as JSON. Promises are awaited.", "browser-eval",
            [("script", "string", "JavaScript expression, e.g. document.title or (async () => { ... })().", true), TabArg]),
        new("browser_screenshot", "A PNG screenshot of the visible part of the page.", "browser-screenshot", [TabArg]),
        new("browser_console", "Console messages and page errors. The first call starts capturing; later calls return what was logged since.", "browser-console", [TabArg]),
    ];

    public static int Run()
    {
        Console.InputEncoding = Encoding.UTF8;
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            JsonNode? message;
            try { message = JsonNode.Parse(line); }
            catch (JsonException) { stdout.WriteLine(Error(null, -32700, "parse error")); continue; }
            if (message is not JsonObject msg) continue;
            var id = msg["id"]?.DeepClone();
            // Notifications (no id), such as notifications/initialized, need no answer.
            if (id is null) continue;
            string method = msg["method"]?.GetValue<string>() ?? "";
            var reply = method switch
            {
                "initialize" => Result(id, new JsonObject
                {
                    ["protocolVersion"] = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? Protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "armux", ["version"] = Version() },
                    ["instructions"] = "Tools for armux's built-in browser (WebView2 tabs inside the armux window). Start with browser_tabs or browser_open, read pages with browser_snapshot or browser_text, act with the refs from the snapshot.",
                }),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray([.. Tools.Select(Describe)]) }),
                "tools/call" => Result(id, Call(msg["params"] as JsonObject)),
                _ => Error(id, -32601, $"method not found: {method}"),
            };
            stdout.WriteLine(reply);
        }
        return 0;
    }

    static JsonNode Describe(Tool t)
    {
        var props = new JsonObject();
        foreach (var (name, type, description, _) in t.Args)
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
        return new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray([.. t.Args.Where(a => a.Required).Select(a => (JsonNode)a.Name)]),
            },
        };
    }

    static JsonObject Call(JsonObject? p)
    {
        string name = p?["name"]?.GetValue<string>() ?? "";
        var args = p?["arguments"] as JsonObject ?? [];
        if (Tools.FirstOrDefault(t => t.Name == name) is not { } tool) return Text($"unknown tool {name}", error: true);

        string Arg(string key) => args[key] switch
        {
            null => "",
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            var v => v.ToJsonString(),
        };
        var request = new Request { Command = tool.Command };
        if (int.TryParse(Arg("tab"), out int tab)) request.Surface = tab;
        switch (tool.Name)
        {
            case "browser_open":
                // Split beside the terminal Claude Code runs in.
                if (int.TryParse(Environment.GetEnvironmentVariable("ARMUX_SURFACE_ID"), out int from)) request.Surface = from;
                request.Body = Arg("url");
                break;
            case "browser_navigate": request.Body = Arg("url"); break;
            case "browser_history": request.Command = "browser-" + Arg("action"); break;
            case "browser_text": request.Body = Arg("max_chars"); break;
            case "browser_click": request.Selector = Arg("ref"); break;
            case "browser_fill": request.Selector = Arg("ref"); request.Body = Arg("value"); break;
            case "browser_type": request.Selector = Arg("ref"); request.Body = Arg("text"); break;
            case "browser_press_key": request.Body = Arg("key"); break;
            case "browser_scroll": request.Body = Arg("pixels"); break;
            case "browser_evaluate": request.Body = Arg("script"); break;
        }
        if (tool.Name == "browser_history" && request.Command is not ("browser-back" or "browser-forward" or "browser-reload"))
            return Text("action must be back, forward or reload", error: true);

        Response response;
        try { response = Ipc.Send(request, TimeSpan.FromSeconds(60)); }
        catch (IOException e) { return Text("armux: " + e.Message, error: true); }
        if (!response.Ok) return Text(response.Error, error: true);
        if (tool.Name == "browser_open") return Text($"opened browser tab {response.Output.Trim()}");
        if (tool.Name == "browser_screenshot")
            return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = response.Output, ["mimeType"] = "image/png" }) };
        return Text(response.Output.Length > 0 ? response.Output : "done");
    }

    static JsonObject Text(string text, bool error = false)
    {
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (error) result["isError"] = true;
        return result;
    }

    static string Result(JsonNode id, JsonNode result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    static string Version()
    {
        var v = typeof(Request).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        return v.Split('+')[0];
    }
}
