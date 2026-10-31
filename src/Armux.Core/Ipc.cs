using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Armux.Core;

/// <summary>
/// The armux control API (cmux's socket API, deviation D-06): one JSON request line and one JSON response line per
/// connection on a named pipe that only the current user can open. Each armux window process has its own pipe,
/// <c>armux-&lt;user&gt;-&lt;pid&gt;</c>, so two windows never answer for each other.
/// </summary>
public static class Ipc
{
    static string Prefix => "armux-" + new string([.. Environment.UserName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]) + "-";

    /// <summary>The pipe served by this process: <c>\\.\pipe\armux-&lt;user&gt;-&lt;pid&gt;</c>.</summary>
    public static string PipeNameFor(int processId) => Prefix + processId;

    /// <summary>
    /// The pipe a client should use: <c>ARMUX_PIPE</c> (set in every armux terminal, naming the window that owns
    /// it), else the first armux pipe of this user that exists, else null.
    /// </summary>
    public static string? FindPipe()
    {
        if (Environment.GetEnvironmentVariable("ARMUX_PIPE") is { Length: > 0 } fromEnv) return fromEnv;
        try
        {
            return Directory.GetFiles(@"\\.\pipe\", Prefix + "*").Select(Path.GetFileName).Order(StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// Sends one request to the app and waits up to <paramref name="timeout"/> for the answer. Throws
    /// <see cref="IOException"/> with a message for the user when armux is not running or does not answer.
    /// </summary>
    public static Response Send(Request request, TimeSpan timeout)
    {
        if (FindPipe() is not { } pipeName) throw new IOException("the app is not running");
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try { pipe.Connect(2000); }
        catch (TimeoutException) { throw new IOException("the armux window does not answer (closed?)"); }
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        writer.WriteLine(Encode(request));
        var read = reader.ReadLineAsync();
        if (!read.Wait(timeout)) throw new IOException("no answer from the app in time");
        return DecodeResponse(read.Result ?? "") ?? throw new IOException("no answer from the app");
    }

    public static string Encode(Request r) => JsonSerializer.Serialize(r, IpcJson.Default.Request);
    public static string Encode(Response r) => JsonSerializer.Serialize(r, IpcJson.Default.Response);

    public static Request? DecodeRequest(string line)
    {
        try { return JsonSerializer.Deserialize(line, IpcJson.Default.Request); }
        catch (JsonException) { return null; }
    }

    public static Response? DecodeResponse(string line)
    {
        try { return JsonSerializer.Deserialize(line, IpcJson.Default.Response); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// <c>Command</c> is "ping", "notify", "send" (types <c>Body</c> into a terminal), "open" (<c>Body</c> in a browser split) or "list". <c>Surface</c> targets a terminal (the CLI fills it from
/// ARMUX_SURFACE_ID); 0 means the focused surface.
/// <para>
/// Browser commands (used by <c>armux mcp</c>, allowed only with <see cref="Settings.BrowserControl"/>) target a browser tab by
/// <c>Surface</c>, or pick one when it is 0: "browser-tabs", "browser-navigate" (<c>Body</c> address), "browser-snapshot",
/// "browser-text", "browser-eval" (<c>Body</c> JavaScript), "browser-click" (<c>Selector</c>), "browser-fill" (<c>Selector</c>,
/// <c>Body</c> text), "browser-type" (<c>Body</c> text into the focused element), "browser-key" (<c>Body</c> key name),
/// "browser-scroll" (<c>Body</c> pixels), "browser-screenshot" (base64 PNG in <c>Output</c>), "browser-console", "browser-back",
/// "browser-forward", "browser-reload".
/// </para>
/// </summary>
public sealed record Request
{
    public string Command { get; set; } = "";
    public int Surface { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Browser commands: an element ref from a snapshot ("e12") or a CSS selector.</summary>
    public string Selector { get; set; } = "";
}

public sealed record Response
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    /// <summary>Text for the CLI to print (version for ping, the workspace list for list).</summary>
    public string Output { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
internal sealed partial class IpcJson : JsonSerializerContext;
