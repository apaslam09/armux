using Armux.Core;
using Armux.Cli;

// armux: the command-line companion of the armux app, like cmux's `cmux` CLI. It ships as armux.com beside the
// app's armux.exe and talks to the running app over the named pipe described in Armux.Core.Ipc. Inside an armux
// terminal, ARMUX_PIPE selects the window and ARMUX_SURFACE_ID the calling terminal; outside, the first running
// armux window answers.

const string Usage = """
    usage: armux <command> [options]

      notify [--title T] [--body B] [message...]   Notify from this terminal: ring, badge, and a toast if armux is in the background
      send [text...]                               Type text into a terminal (\n becomes Enter)
      open <url>                                   Open a page in a browser split beside the terminal
      list                                         List workspaces and their tabs
      ping                                         Check that armux is running
      mcp                                          MCP server on stdio: lets Claude Code read and drive browser tabs
      --version                                    Print the version

    options:
      --surface ID   Target another tab (default: $ARMUX_SURFACE_ID, else the focused tab)
    """;

if (args.Length > 0 && args[0] is "--version" or "-v" or "version")
{
    var v = typeof(Request).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
    Console.WriteLine("armux " + (v.Contains('+') ? v[..Math.Min(v.Length, v.IndexOf('+') + 8)] : v));
    return 0;
}

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}

if (args[0] == "mcp") return Mcp.Run();

var request = new Request { Command = args[0] };
if (int.TryParse(Environment.GetEnvironmentVariable("ARMUX_SURFACE_ID"), out int envSurface)) request.Surface = envSurface;
var words = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    string? Next() => i + 1 < args.Length ? args[++i] : null;
    switch (args[i])
    {
        case "--title": request.Title = Next() ?? ""; break;
        case "--body": request.Body = Next() ?? ""; break;
        case "--surface":
            if (!int.TryParse(Next(), out int s)) { Console.Error.WriteLine("armux: --surface needs a number"); return 2; }
            request.Surface = s;
            break;
        default: words.Add(args[i]); break;
    }
}

switch (request.Command)
{
    case "notify":
        if (words.Count > 0) request.Body = string.Join(' ', words);
        if (request.Title.Length == 0 && request.Body.Length == 0) { Console.Error.WriteLine("armux notify: give a message, --title or --body"); return 2; }
        break;
    case "send":
        request.Body = string.Join(' ', words).Replace("\\n", "\r").Replace("\\t", "\t");
        break;
    case "open":
        request.Body = string.Join(' ', words);
        if (request.Body.Length == 0) { Console.Error.WriteLine("armux open: give a URL"); return 2; }
        break;
    case "list": case "ping": break;
    default:
        Console.Error.WriteLine($"armux: unknown command \"{request.Command}\"\n");
        Console.Error.WriteLine(Usage);
        return 2;
}

try
{
    var response = Ipc.Send(request, TimeSpan.FromSeconds(30));
    if (!response.Ok) { Console.Error.WriteLine("armux: " + response.Error); return 1; }
    if (response.Output.Length > 0) Console.Write(response.Output.EndsWith('\n') ? response.Output : response.Output + "\n");
    return 0;
}
catch (IOException e)
{
    Console.Error.WriteLine("armux: " + e.Message);
    return 1;
}
