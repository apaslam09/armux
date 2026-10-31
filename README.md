<img src="docs/armux-logo.png" alt="armux logo" width="88" align="right">

# armux

A terminal workspace app for **Windows 11 on ARM64**, modeled on [cmux](https://github.com/manaflow-ai/cmux) for macOS: vertical workspace sidebar, split panes, a browser beside your terminal, and notification rings when a coding agent needs you.

[![ci](https://github.com/apaslam09/armux/actions/workflows/ci.yml/badge.svg)](https://github.com/apaslam09/armux/actions/workflows/ci.yml)

> **Status: 0.4.0.** PowerShell and cmd run through ConPTY in armux's own terminal emulator; OSC 9 / 99 / 777 light up notification rings, Windows toasts and the taskbar button, and an agent waiting for an answer turns orange; workspaces reopen on start; the browser loads real pages; the `armux` CLI talks to the app; a Zed-style Settings page (and settings.json) covers themes, fonts and every shortcut. See the [roadmap](ARCHITECTURE.md#roadmap).

![armux with two PowerShell panes, the file explorer and a notification ring](docs/screenshots/13-live-shells.png)

## Why

- **Native ARM64.** Built with WinUI 3 and published as NativeAOT for `win-arm64`. CI checks both binaries have PE machine type `0xAA64`, so nothing runs under x64 emulation.
- **cmux's UI and model.** The same Window > Workspace > Pane > Surface hierarchy, workspace colors, notification rings, command palette and a shortcut layout mapped to Windows. Every UI element traces to a cmux feature ([traceability table](ARCHITECTURE.md#5-feature-traceability)).
- **Measured memory.** With one PowerShell pane on a Surface Laptop 7, armux and its ConPTY host use about 107 MB working set, 49 MB private and 27 MB in Task Manager; each extra pane adds about 13 / 4 / 3.5 MB. The shell itself is counted separately (pwsh about 26 MB private, cmd about 6). Every scenario, and the scripts to reproduce it, is in [ARCHITECTURE.md](ARCHITECTURE.md#7-memory-budget).

## Features

| Feature | Status |
|---|---|
| PowerShell 7 (or Windows PowerShell) and cmd through ConPTY, with 24-bit color, scrollback, selection, copy and paste | Done |
| OSC 9 / 99 / 777 notifications: pane ring, tab dot, workspace badge, notification panel, Windows toast when armux is in the background | Done |
| `armux` CLI: `notify`, `send`, `open`, `list`, `ping` over a per-user named pipe | Done |
| `armux mcp`: Claude Code reads and drives the built-in browser (snapshot, click, fill, type, keys, JavaScript, screenshot, console); off until turned on in Settings › Browser | Done |
| Vertical sidebar: workspace title, live status with a dot (working, waiting for you, idle, latest notification), git branch · directory (follows `cd`), ports, PR, unread badge | Done |
| Orange light when an agent waits for an answer or a permission: pane ring, tab dot and sidebar status, until you type in that terminal | Done |
| Workspaces, splits and tabs reopen on the next start, each terminal in its last folder (can be turned off) | Done |
| Workspace colors (17 presets or custom) shown as a left rail or underline; pinned workspaces | Done |
| Per-pane tab bars, split right / down, drag to resize, zoom, equalize | Done |
| Built-in browser (WebView2): address bar with search, back, forward, reload; lazy, suspended when hidden, at most 2 live, released when closed | Done |
| File explorer beside the sidebar (`Ctrl+Shift+E`), modeled on Warp's file tree; browses any folder or drive, and dragging an entry onto a terminal types its relative path | Done (hidden by default) |
| Command palette (`Ctrl+Shift+P`) with inline shortcuts and workspace switching | Done |
| Keyboard switching: `Ctrl+Tab` through the tabs of a workspace, `Ctrl+Shift+Tab`, `Ctrl+1`–`9` and `Ctrl+PageUp`/`PageDown` for workspaces, `Ctrl+arrows` and `F6` for panes, `Ctrl+V` to paste; Windows Terminal's defaults too (`Ctrl+,`, `Ctrl+Insert`/`Shift+Insert`, `Ctrl+Shift+arrows` to scroll, `Alt+Shift+=`/`-`/`D` to split, `F11`) | Done |
| Zed's fonts bundled: Lilex in terminals, IBM Plex Sans in the interface; shell, start directory, fonts and size in Settings (`Ctrl+,`); `Ctrl+=` / `Ctrl+-` / `Ctrl+0` and Ctrl+wheel zoom the whole window, `Ctrl+Shift+=` / `-` / `0` the terminal text | Done |
| Taskbar status of the current workspace while you're tabbed out: accent bar while Claude Code (or any OSC 9;4 program) works, yellow when it needs you, red on error | Done |
| Prompt editor (`Ctrl+G`): a normal text box under the terminal for writing long prompts, sent in one paste (Enter sends, Shift+Enter new line, Ctrl+Enter pastes without Enter) | Done |
| Mouse: copy on select, Shift+drag selection in mouse-aware programs, wheel and clicks passed to programs like Claude Code and vim, drag files from the explorer or Windows Explorer to type their relative path | Done |
| Settings page (`Ctrl+,` or the gear; `Ctrl+Shift+,` opens settings.json), laid out like Zed's with search and "Edit in settings.json" (live reload): theme (Match Windows, Dark, Light, High contrast), fonts, zoom, cursor, scrollback, start and new-tab directories, a warning before closing while programs run, notifications, browser, every shortcut rebindable, and About with version details | Done |
| WSL | Not planned for now: a WSL VM costs far more memory than armux |

More screenshots: [browser and CLI](docs/screenshots/14-browser-and-cli.png), [command palette](docs/screenshots/02-command-palette.png), [notifications](docs/screenshots/03-notifications.png), [workspace colors](docs/screenshots/05-workspace-colors.png), [zoomed pane](docs/screenshots/06-zoomed-pane.png), [light theme](docs/screenshots/07-light-theme.png), [four-pane grid](docs/screenshots/08-four-pane-grid.png), [file explorer](docs/screenshots/11-file-explorer.png), [settings](docs/screenshots/12-settings.png).

## The `armux` CLI

The CLI ships as `armux.com` next to the app's `armux.exe` and is on the PATH of every armux terminal. PowerShell and cmd run `.com` before `.exe`, so `armux` reaches the CLI (in Git Bash, `armux.exe notify ...` forwards to it). Every armux terminal also sets `ARMUX_SURFACE_ID`, so commands act on the calling tab:

```powershell
armux notify "Tests finished: 24 passed"          # ring + badge on this tab, a toast if armux is in the background
armux notify --title "Claude Code" --body "Waiting for your input"
armux open http://localhost:3000                  # browser split beside this terminal
armux send --surface 2 "npm test\n"               # type into another tab
armux list                                        # workspaces and tabs with their ids
```

### Browser tools for Claude Code

Turn on Settings › Browser › Let agents control the browser, then register the MCP server once:

```powershell
claude mcp add armux -s user -- "C:\path\to\armux\armux.com" mcp
```

Claude Code started in an armux terminal then has `browser_tabs`, `browser_open`, `browser_navigate`, `browser_history`, `browser_snapshot`, `browser_text`, `browser_click`, `browser_fill`, `browser_type`, `browser_press_key`, `browser_scroll`, `browser_evaluate`, `browser_screenshot` and `browser_console`. They work on armux's own browser tabs, in the window Claude Code runs in, and send input to the page only.

Programs can also notify without the CLI by printing OSC 9 (`ESC ] 9 ; text BEL`), OSC 777 (`ESC ] 777 ; notify ; title ; body BEL`) or OSC 99 (kitty).

## Download

Get `armux-<version>-win-arm64.zip` from [Releases](https://github.com/apaslam09/armux/releases), extract it anywhere (for example `%LOCALAPPDATA%\Programs\armux`) and run `armux.exe`. To use the CLI outside armux terminals too, add that folder to your PATH. There is no installer yet; settings live in `%LOCALAPPDATA%\armux\settings.json` and the saved workspaces in `session.json` beside it.

## Requirements

- Windows 11 on ARM64 (developed on a Surface Laptop 7, Snapdragon X Plus)
- Windows App Runtime 1.8 and the WebView2 Runtime (both usually preinstalled on Windows 11)

## Build from source

See [CONTRIBUTING.md](CONTRIBUTING.md#setup) for the full setup. In short:

```powershell
git clone https://github.com/apaslam09/armux
cd armux
dotnet test --project tests/Armux.Core.Tests
dotnet publish src/Armux.App -c Release -r win-arm64 -p:Platform=ARM64 -o publish
dotnet publish src/Armux.Cli -c Release -r win-arm64 -o publish
publish\armux.exe
```

## Measuring memory

```powershell
./scripts/measure-memory.ps1 -Name armux
./scripts/bench.ps1 -Exe publish/armux.exe -ArgumentList '--scenario','live','--panes','4'
```

`measure-memory.ps1` sums working set, private bytes and Task Manager memory across the app and every process it starts (WebView2, ConPTY hosts, shells), and reports app cost and shell cost separately. `bench.ps1` adds cold-start time.

## Relationship to cmux

armux is an independent project, not affiliated with Manaflow. cmux is GPL-3.0-or-later and armux is MIT, so armux contains **no cmux source code or assets**. It is reimplemented from cmux's public docs and screenshots. See [ARCHITECTURE.md](ARCHITECTURE.md#3-cmux-license-decision).

## Contributing

Contributions are welcome, especially from people on Windows on ARM hardware. Start with [CONTRIBUTING.md](CONTRIBUTING.md) and issues labeled [`good first issue`](https://github.com/apaslam09/armux/labels/good%20first%20issue). No ARM64 device? CI builds and checks every PR on a native ARM64 runner.

## License

[MIT](LICENSE). The bundled fonts are under the SIL Open Font License 1.1: [Lilex](https://github.com/mishamyrt/Lilex) ([`OFL-Lilex.txt`](src/Armux.App/Assets/Fonts/OFL-Lilex.txt)) and [IBM Plex Sans](https://github.com/IBM/plex) ([`OFL-IBMPlexSans.txt`](src/Armux.App/Assets/Fonts/OFL-IBMPlexSans.txt)).
