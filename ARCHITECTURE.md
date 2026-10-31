# armux architecture

Status: **Phase 3 complete (2026-10-26), awaiting maintainer review.** armux runs real PowerShell and cmd shells through ConPTY with its own terminal emulator, turns OSC 9/99/777 into notification rings and Windows toasts, browses real pages, and has an `armux` CLI. Built with WinUI 3 and published as NativeAOT. Measurements are in section 7. Nothing in this file claims a measured number unless it gives the method and date.

## 1. Scope

armux is a terminal workspace app for **Windows 11 on ARM64**. It follows the UI and feature set of [cmux](https://github.com/manaflow-ai/cmux), the macOS app, as closely as Windows allows.

- Target: `win-arm64`. The shipped app `armux.exe` (and the CLI `armux.com`) is NativeAOT ARM64 code with PE machine type `0xAA64`, checked by `scripts/check-pe-arm64.ps1` in CI.
- Not in scope: macOS, Linux, x64, ARM64EC. x64 users already have other options (section 2).
- Resource efficiency is a product requirement. Memory is measured, budgeted and reported per scenario (section 7).

### Roadmap

| Phase | Deliverable | Status |
|---|---|---|
| 1 | Repository, docs, CI on a native ARM64 runner | Done 2026-10-25 |
| 2 | Full cmux-style UI on mock data, stack comparison, memory measurements | Done 2026-10-26 |
| 3 | ConPTY shells (pwsh, cmd), terminal emulation and rendering, OSC 9/99/777 notifications, real browser navigation, `armux` CLI. WSL dropped by the maintainer (D-21). | Done 2026-10-26 |
| 0.3.0 | First release: Settings page (themes including high contrast, fonts, zoom, every shortcut rebindable, About), app icon, taskbar status, prompt editor, mouse support, warning before closing while programs run; the app is renamed `armux.exe` (CLI `armux.com`, D-11). Release builds come from `.github/workflows/release.yml` on a `v*` tag. | Released 2026-10-26 |
| 0.4.0 | Settings page in Zed's style with search and an editable, live-reloading settings.json (D-31); explorer browses any folder or drive (D-32); workspaces reopen on start (D-33); orange light while a program waits for an answer (D-34); Ctrl+, for Settings and Windows Terminal's common shortcuts (D-35); prompt editor keys changed (D-23); theme-change crash fixed. | Drafted 2026-10-29 |

### Repository layout

| Path | What |
|---|---|
| `src/Armux.Core` | UI-free model: workspaces, split tree, notifications, palette search, settings, file tree, the terminal emulator (`Vt/`), ConPTY sessions (`Pty/`) and the pipe protocol (`Ipc.cs`). Unit-tested. |
| `src/Armux.App` | WinUI 3 app (`armux.exe`). Views are built in C#; only `App.xaml` is XAML. |
| `src/Armux.Cli` | The `armux` CLI (`armux.com`), NativeAOT, no dependencies beyond Core. |
| `tests/Armux.Core.Tests` | MSTest tests for the core, including a live ConPTY round trip. |
| `scripts/` | `measure-memory.ps1`, `bench.ps1`, `screenshot.ps1`, `check-pe-arm64.ps1` |
| `docs/screenshots/` | Screenshots: 01–12 on mock data, 13–14 from live shells |

## 2. Prior art and positioning

Checked 2026-10-25:

| Project | What it is | Windows ARM64 build |
|---|---|---|
| [manaflow-ai/cmux](https://github.com/manaflow-ai/cmux) | Native macOS terminal (Swift/AppKit, libghostty). GPL-3.0-or-later. | n/a (macOS only) |
| [kevmtt/wmux](https://github.com/kevmtt/wmux) | cmux port for Windows. Electron + xterm.js + node-pty. MIT. | No. README lists only `wmux-*-win-x64.zip`. |
| [openwong2kim/wmux](https://github.com/openwong2kim/wmux) | Cross-platform terminal multiplexer. Squirrel.Windows packaging. | None visible on the v4.10.0 release page (6 of 18 assets were not shown). |
| psmux | tmux-style CLI multiplexer | Not a GUI |

armux differs on three points: native ARM64 builds, cmux-level UI fidelity, and a measured memory budget.

## 3. cmux license decision

- cmux is **GPL-3.0-or-later**. Its server components (`web/`, Cloudflare workers, relay services) are under **BSL 1.1**. Source: cmux README, read 2026-10-25.
- armux is **MIT**. MIT code cannot contain GPL-derived code without becoming GPL.
- **Decision: armux copies no cmux source code, assets, icons or screenshots.** It is reimplemented from cmux's public README, docs (cmux.com/docs) and screenshots. Feature names, the concept hierarchy and the shortcut layout are interface facts, not copied code. The 17 workspace color values are armux's own (cmux documents the count, not the values).
- Contributors must not paste or translate cmux source (see CONTRIBUTING.md). Reviewers reject PRs that do.
- cmux screenshots are compared against armux screenshots locally and are never committed.

## 4. Concept model (from cmux)

armux uses cmux's hierarchy and names unchanged (cmux.com/docs/concepts):

```
Window                    one top-level window, own sidebar
└─ Workspace              a sidebar entry ("tab" in UI copy, "workspace" in CLI/API)
   └─ Pane                a split region (split right / split down)
      └─ Surface          a tab inside a pane, own tab bar, own ARMUX_SURFACE_ID
         └─ Panel         content: terminal or browser (internal, CLI addresses surfaces)
```

In code: `Workspace`, `Pane`, `SplitNode`, `Surface` in `src/Armux.Core/Model.cs`; the panel is `TerminalView` or `BrowserPanel`.

CLI handles follow cmux's short-ref style: `workspace:2`, `pane:1`, `surface:7`. A bare number is an index, not an ID. Environment variables use the prefix `ARMUX_` (D-05).

## 5. Feature traceability

Every UI element traces to a cmux feature. Windows deviations link to section 9. Screenshot numbers refer to `docs/screenshots/`.

| cmux feature (source) | armux element | Phase 2 status | Screenshot | Deviation |
|---|---|---|---|---|
| Vertical sidebar of workspaces: title, latest notification text, branch, directory, ports, PR (README) | `SidebarRow` | Done (mock data) | 01 | — |
| Unread badge on the workspace row, row lights up when an agent needs attention (README) | Badge on the row | Done | 01, 09 | D-12 |
| Tab colors: right-click a workspace, 17 presets or custom; color on the tab and the indicator rail (changelog 0.61.0) | Context menu, swatch flyout, `ColorPicker` for custom | Done | 04, 05 | D-10 |
| Workspace indicator style Accent / Underline / None (cmux settings) | Left rail (default) or underline; palette commands switch it | Done | 01 | — |
| Pin a workspace to the top (changelog 0.61.0) | Pin from the context menu, pin glyph on the row | Done | 01 | — |
| Per-pane tab bar of surfaces with new-terminal, new-browser, split-right and split-down buttons (screenshots) | `PaneView` tab bar | Done | 01 | — |
| Split right / down, directional focus, resize, zoom, equalize (README, keyboard shortcuts) | Split tree, draggable 1 px dividers, zoom | Done | 06, 08, 10 | D-01 |
| Built-in browser beside the terminal: address bar, back, forward, reload (README) | `BrowserPanel` on WebView2: real navigation, search from the address bar, page title on the tab; mock page only in the screenshot scenarios | Done | 01, 14 | D-02, D-03 |
| Terminal: Ghostty-based emulator with GPU rendering (README) | `Armux.Core.Vt` emulator (parser, screen, scrollback, reflow) and `TerminalView` (XAML rows) on ConPTY | Done | 13, 14 | D-18 |
| Shell integration: working directory and git branch in the sidebar (README) | OSC 7 from a wrapped PowerShell prompt or cmd `PROMPT`; branch read from `.git/HEAD` | Done | 13 | D-19 |
| Command palette `⌘⇧P`, shortcuts shown inline, workspace switcher results, `⌃N`/`⌃P` (changelog 0.61.0, keyboard shortcuts) | `CommandPalette` | Done | 02, 10 | D-01 |
| Blue notification ring on panes that need attention (README) | 2 px accent ring, plus blue dot on the tab | Done | 01, 09 | — |
| Notification panel `⌘I`, jump to latest unread `⌘⇧U`, toggle unread, mark all read, clear (README) | Bell flyout with count, palette commands | Done | 03 | D-04 |
| Flash focused panel `⌘⇧H` (README) | Ring pulse animation | Done | — | — |
| Theme follows system / light / dark (cmux settings) | Dark and light token sets, "Match Windows" command | Done | 07 | D-07 |
| Clear scrollback, font size | Clear scrollback (mock); font size commands | Done | — | — |
| Settings window `⌘,` (cmux settings) | Settings dialog: terminal font and size, interface font | Done (fonts only) | 12 | D-15, D-16 |
| _Not in cmux:_ Warp's file tree (docs.warp.dev, "File Tree") | `ExplorerPanel` beside the sidebar, read-only `FileView` tabs | Done | 11 | D-13, D-17 |
| Desktop notification when a process needs attention (README) | Windows toast while armux is in the background; clicking it jumps to the tab | Done (toast display not verified on screen) | — | D-04 |
| OSC 9, OSC 99, OSC 777 detection (README) | `OscNotifications` (kitty chunks and base64 included; ConEmu `9;n` codes ignored) | Done | 13 | — |
| `cmux notify` CLI, socket API (README) | `armux notify`, `send`, `open`, `list`, `ping` over a per-user named pipe | Done | 14 | D-06, D-20 |
| Copy and paste (⌘C / ⌘V) | Mouse selection, Ctrl+Shift+C / Ctrl+V, Ctrl+Shift+V, Shift+Insert, right-click menu, bracketed paste | Done | — | — |
| Find, find in directory, session restore, mouse reporting | — | Later | — | — |
| SSH workspaces, Cloud workspaces, Claude Code Teams, browser import | — | Not planned | — | — |

### Comparison with cmux screenshots

Compared by eye against cmux's README images `main-first-image.png`, `vertical-horizontal-tabs-and-splits.png`, `notification-rings.png`, `sidebar-notification-badge.png` and `built-in-browser.png`.

- **Matches:** title row with sidebar toggle, bell with count and `+`, then folder icon and workspace title. Selected workspace as a filled rounded block (gray in armux, D-12). Badge, title, notification text and `branch · directory` lines. Per-pane tab bars with the four action buttons. Blue ring on panes needing attention. Browser panel with back, forward, reload and an address bar. Thin dividers between panes.
- **Differs, by request:** Windows caption buttons on the right instead of macOS traffic lights (D-08). Segoe UI Variable instead of SF Pro, Lilex for terminals (D-09, D-15). Gray selected workspace (D-12). File explorer button and column (D-13).
- **Differs, not yet built:** cmux's terminal content is a real Ghostty terminal; armux shows static mock text until Phase 3. cmux's address bar row has extra browser tool icons, which armux omits.

## 6. Stack

### Decision: WinUI 3 (Windows App SDK) with C#/.NET 10, published as NativeAOT

Decided by the maintainer on 2026-10-25 after the spikes below:

- **Rust native (B)** was rejected for its tradeoff: the whole UI (layout, text input, IME, accessibility) would have to be built by hand.
- **Tauri 2 (A)** was given a chance to beat a real reference: the maintainer's running Warp terminal. Even fully tuned, it used more working set and more Task Manager memory than Warp (table below), so it was rejected.
- **WinUI 3 (C)** uses less memory than Warp on all three metrics and gives native controls, accessibility and IME.

### Spike results (2026-10-25)

Setup: Surface Laptop 7, Snapdragon X Plus, Adreno X1-85 GPU (driver 31.0.152.1), Windows 10.0.26200, WebView2 Runtime 155.0.4283.62. Each spike draws the same window (sidebar with 5 rows, a tab label, 40 lines of static monospace text), optionally with a browser panel showing the same local HTML page. Method: `scripts/bench.ps1`, app-attributable memory (section 7), MB.

| Scenario (working set / private) | B native, D2D software | B native, D2D hardware | A Tauri 2 | C WinUI 3 (JIT, .NET 8) |
|---|---|---|---|---|
| Idle | 29.7 / 14.6 | 52.3 / 91.4 | 344.9 / 130.8 | 112.5 / 48.4 |
| Browser panel open | 358.1 / 138.4 | not run | 416.8 / 158.3 | 441.5 / 174.3 |
| Browser closed, 65 s later | 36.7–40.0 / 15.6–15.9 | not run | 370.7 / 132.4 | 121.5 / 50.0 |
| Cold start, median of 10 | 82 ms | 116 ms | 407 ms | 325 ms |

### Tauri tuning versus the running Warp (2026-10-25)

Warp was measured as `warp.exe` plus its ConPTY host, excluding shells and what runs inside them. Tauri variants change WebView2 browser arguments plus `MemoryUsageTargetLevel=Low`.

| | Working set | Private | Task Manager "Memory" | Processes |
|---|---|---|---|---|
| Warp (running, long-lived) | 203.2 | 477.6 | 43.7 | 2 |
| Tauri default | 347.0 | 130.4 | 76.0 | 7 |
| Tauri, GPU off, low-end mode, 1 renderer, MemoryUsageTargetLevel Low | 325.1 | 107.1 | 59.0 | 7 |
| Tauri, plus GPU and network service in the browser process | 266.6 | 86.8 | 51.3 | 5 |
| WinUI spike (JIT) | 111.7 | 48.2 | 26.2 | 1 |

The best Tauri variant still loses to Warp on working set (+31%) and Task Manager memory (+17%). It also depends on undocumented WebView2 flags.

### Findings that shaped the design

1. **NativeAOT matters for WinUI.** The same app went from 134.9 to 95.1 MB working set at idle, and from 320 to 156 ms start-up. With the JIT, closing the browser also left about 68 MB of runtime memory committed; under AOT that retention disappears (section 7).
2. **WebView2 is the largest cost.** The first browser panel adds about 316 MB working set and 122 MB private in 6 processes. All of them exit when the last browser panel closes.
3. **The GPU driver is loaded in every WinUI process** (`qcdx11arm64xum.dll`), because WinUI composites on the GPU. It is part of the idle baseline above.
4. **The WebView2 user data folder needs a short path.** A ~200-character path failed with `0x80080005`. armux uses `%LOCALAPPDATA%\armux\WebView2`.
5. **Dead end: NativeAOT on a code-only WinUI app.** The spike with no XAML at all crashed at start (`0xC000027B`, stowed exception). The real app keeps a minimal `App.xaml`, and AOT works.

### Toolchain

- .NET SDK 10.0.402 (pinned by `global.json`, roll forward within 10.0), Windows App SDK 1.8.260921001, Visual Studio 2022/2026 with the MSVC ARM64 tools and Windows 11 SDK (for the AOT linker).
- `dotnet build` produces a framework-dependent JIT build for development. `dotnet publish` produces the self-contained NativeAOT release build. The AOT linker finds MSVC through `vswhere.exe`, so its folder must be on `PATH`.
- `Armux.Core.dll` in a `dotnet build` output is IL-only (AnyCPU) and reports PE machine `0x014C`. It runs as ARM64 code. The NativeAOT `armux.exe` contains everything as ARM64 code.

## 7. Memory budget

### Method

`scripts/measure-memory.ps1 -Name armux` walks the process tree from the app by `ParentProcessId` (with a creation-time check against PID reuse). For each process it reads three numbers and sums them:

- **Working set** (`WorkingSet64`): resident pages, including shared DLL pages.
- **Private bytes** (`PrivateMemorySize64`): committed private memory, resident or not.
- **Task Manager "Memory"**: the private working set (`Win32_PerfRawData_PerfProc_Process.WorkingSetPrivate`), the number users see.

It takes 5 samples 1 s apart and reports the median. Processes are grouped:

| Category | Processes | Counts against budget |
|---|---|---|
| app | `armux.exe` and helpers | yes |
| webview | `msedgewebview2.exe` | yes |
| conpty | `OpenConsole.exe`, `conhost.exe` | yes |
| shell | `pwsh`, `powershell`, `cmd`, `wsl`, `wslhost`, `bash` | reported, not budgeted |
| workload | anything a shell started | reported, not budgeted |

**app-attributable** = app + webview + conpty. `scripts/bench.ps1` launches a scenario, measures cold start (process creation to first rendered frame, median of 10 after one warm-up), then memory after a settle period. Scenarios are reproducible with `armux.exe --scenario idle|panes|browser|demo` (see `LaunchOptions.cs`).

### Targets (revised 2026-10-25 for WinUI, app-attributable, summed)

| Scenario | Working set | Private bytes | Task Manager | Reason |
|---|---|---|---|---|
| Idle: 1 workspace, 1 terminal pane, browser closed | ≤ 130 MB | ≤ 60 MB | ≤ 40 MB | Set from the WinUI spike; the Task Manager target sits below the maintainer's Warp (43.7 MB). |
| Each additional terminal pane | ≤ 20 MB | ≤ 8 MB | reported | One ConPTY host measured 13.8 / 3.7 MB; the rest covers buffer and view state. |
| Browser panel open | reported | reported | reported | Page-dependent. |
| All browser panels closed, 65 s later | reported | ≤ idle + 10% | reported | Judged on private bytes; working set is trimmed and not a fair signal. |

### Phase 2 results (2026-10-26, NativeAOT build, mock data, no ConPTY yet)

Same device as section 6. Window 1440×900 physical pixels at 150% scale. Settle 15 s (70 s for "closed").

| Scenario | Working set | Private | Task Manager | Processes | Target |
|---|---|---|---|---|---|
| Idle (3 runs) | 94.0–96.1 | 46.0–48.7 | 24.6–27.0 | 1 | **Met** |
| 2 panes | 105.8 | 53.0 | 30.9 | 1 | — |
| 4 panes | 104.9 | 56.3 | 33.9 | 1 | — |
| 8 panes | 118.7 | 65.6 | 43.2 | 1 | — |
| Per additional pane (1→8, average) | +3.4 | +2.8 | +2.7 | — | **Met** (≤ 20 / ≤ 8) |
| Browser panel open | 410.9 | 167.9 | 93.5 | 7 | reported |
| Browser closed, 65 s later (3 runs) | 11.8–12.0 | 48.2–51.7 | 4.6–4.8 | 1 | **Met** on median: 48.9 vs idle median 46.5 (+5%). One run was +12%. |
| Demo (6 workspaces, 3 panes, browser) | 422.5 | 180.2 | 104.6 | 7 | reported |
| Cold start, idle, median of 10 | 156 ms | | | | — |

The same scenarios with the JIT build (`dotnet build`) measured idle 134.9 / 56.6 / 34.5 and start 320 ms. Browser-closed retained 122.7 MB private there. The managed heap was 0.8 MB, so that memory was .NET runtime and native commit. Shipping AOT avoids it.

### Review round 1 results (2026-10-26, same build settings)

Measured after the round 1 changes (gray selection, Lilex, settings, file explorer). App-attributable, MB. Explorer runs show this repository.

| Scenario | Working set | Private | Task Manager | Note |
|---|---|---|---|---|
| Idle (3 runs) | 94.2–95.7 | 46.1–46.4 | 24.7–25.6 | Same as Phase 2. Lilex instead of Cascadia Mono: +1.3 private. |
| 8 panes | 110.5 | 61.4 | 40.6 | — |
| Explorer open (3 runs) | 100.8–105.5 | 52.3–57.2 | 30.4–35.5 | About +6 private over idle |
| Explorer closed after one open (3 runs) | 100.6–104.2 | 51.6–56.2 | 30.4–33.5 | One-time cost stays (see below) |
| Explorer opened and closed 1, 5, 20, 40, 80 times | 108.1–108.5 | 54.9–56.3 | 33.6–34.3 | Flat: no growth per cycle |
| Browser open / closed | 411.3 / 11.7 | 168.8 / 49.4 | 93.0 / 4.6 | Same as Phase 2 |
| Demo with explorer open | 428.9 | 183.3 | 109.0 | — |
| Cold start, idle, median of 10 | 232–248 ms | | | The Phase 2 commit measured 231 ms in the same session, so the change from 156 ms is machine state, not code. |

Explorer findings:

- The first version created the panel on open and dropped it on close. It grew **1.9 MB private per open/close cycle** (127 MB after 40 cycles). The released rows' native XAML objects are freed only when their managed wrappers are collected, and an idle app rarely collects. Collecting on close cut the growth to 0.3 MB per cycle.
- The shipped version keeps one panel, drops its rows, listings and file watchers when hidden, and collects once on close. 80 cycles stay flat at 55–56 MB private.
- About 7 MB private stays after the first open (XAML templates, the icon font, text caches). The idle target (≤ 60 MB private) still holds.
- Trimming the working set on close was tried and rejected. It lowered the working-set and Task Manager numbers but not private bytes, so it only made the numbers look better.

The open/close cycles were driven through UI Automation (`InvokePattern` on the explorer button), which sends no input to other windows.

### Phase 3 results (2026-10-26, NativeAOT, real shells)

`--scenario live` (the default) starts the default shell from Settings in every pane; `ARMUX_SETTINGS_FILE` pointed at defaults (PowerShell 7.6, Lilex). App-attributable = armux + one `conhost.exe` (ConPTY host) per pane. Shells are reported separately, as the method says.

| Scenario | Working set | Private | Task Manager | Target |
|---|---|---|---|---|
| Idle: 1 live pwsh pane (4 runs) | 106.0–107.5 | 47.7–50.1 | 26.1–28.1 | **Met** (≤ 130 / ≤ 60 / ≤ 40) |
| 2 / 4 / 8 live pwsh panes | 120.2 / 141.7 / 194.9 | 53.6 / 57.8 / 76.1 | 31.3 / 34.4 / 50.8 | — |
| Per additional pane (1→8, average) | +12.7 | +4.0 | +3.5 | **Met** (≤ 20 / ≤ 8) |
| One ConPTY host (`conhost.exe`) | 10.2 | 1.8 | 1.2 | part of the above |
| cmd instead of pwsh: 1 / 4 panes | 106.6 / 139.9 | 48.5 / 58.2 | 26.4 / 34.5 | same as pwsh: the shell is not in this number |
| After 80,000 lines of output (full 5,000-line scrollback) | 131.2 | 68.5 | 46.7 | reported |
| Browser open / closed (65 s), mock page | 417.1 / 11.2 | 168.0 / 50.2 | 94.0 / 4.6 | closed **Met** (+8% over the mock idle 46.3) |
| Demo (6 workspaces, mock) | 419.0 | 173.4 | 98.3 | reported |
| Cold start to first frame, live, median of 10 | 272–284 ms | | | reported |

Shells, not budgeted: one `pwsh.exe` 91.5 / 25.6 / 20.3, one `cmd.exe` 9.3 / 5.8 / 2.2. A whole one-pane tree is 199 / 76 / 48 with pwsh and 117 / 55 / 30 with cmd. For comparison, the maintainer's running Warp measured 203 / 478 / 44 (section 6).

Notes:

- The 80,000-line run was 20,000 PowerShell error messages. armux used 6.8 s of CPU in total for it. Scrollback lines are stored packed (text plus attribute runs), not as cell arrays.
- Rendering is idle when output is idle: a render is queued only when bytes arrive, and only changed rows are rebuilt.
- Executables: `armux.exe` 9.5 MB, `armux.com` (CLI) 2.7 MB, both NativeAOT ARM64.

### Browser panel policy (implemented in `BrowserHost`)

1. **Lazy.** No WebView2 exists until a browser panel is first shown.
2. **One environment.** All panels share the default WebView2 environment (one browser and GPU process; each panel adds a renderer).
3. **Suspend when hidden.** A panel that stays off-screen for 1 s gets `MemoryUsageTargetLevel = Low` and `TrySuspendAsync`. It resumes when shown.
4. **Cap.** At most 2 live browser panels. Showing a third unloads the least recently shown one, which reloads when shown again.
5. **Tear down.** Closing the last panel closes its WebView2, so every WebView2 process exits (verified). After 5 s armux runs a compacting GC and trims its working set.
6. **Short data path.** `%LOCALAPPDATA%\armux\WebView2`.

## 8. Keyboard shortcuts

cmux binds ⌘, which Windows doesn't have. Plain Ctrl+letter can't be used, because terminals need it (Ctrl+C, Ctrl+D, Ctrl+W, Ctrl+K, Ctrl+L, ...). armux maps:

- ⌘ to **Ctrl+Shift**
- ⌘⇧ to **Ctrl+Alt+Shift**
- ⌥⌘ to **Ctrl+Alt**
- ⌃⌘ to **Alt+Shift**

Exceptions are marked with *. Verified by injected key presses (`scripts/screenshot.ps1 -Keys`, screenshot 10, and review round 1): Ctrl+Shift+D, Ctrl+Alt+Shift+D, Ctrl+Shift+T, Ctrl+Shift+N, Ctrl+Shift+1, Ctrl+Shift+P, Ctrl+2, Ctrl+PageDown, Ctrl+Shift+E, Ctrl+Shift+= (one press). The other bindings are registered but not yet tested by injection.

| Action | cmux | armux |
|---|---|---|
| New workspace | ⌘N | Ctrl+Shift+N |
| Jump to workspace 1 to 8 / last | ⌘1–8 / ⌘9 | Ctrl+1–8 / Ctrl+9 * (D-14), also Ctrl+Shift+1–8 / Ctrl+Shift+9 |
| Next / previous workspace | ⌃⌘] / ⌃⌘[ | Ctrl+Shift+Tab * (D-38) / Ctrl+PageUp * (D-14), also Ctrl+PageDown, Alt+Shift+] / Alt+Shift+[ |
| Close workspace | ⌘⇧W | Ctrl+Alt+Shift+W |
| Rename workspace | ⌘⇧R | Ctrl+Alt+Shift+R |
| Toggle sidebar | ⌘B | Ctrl+Shift+B |
| New surface | ⌘T | Ctrl+Shift+T |
| Close surface | ⌘W | Ctrl+Shift+W |
| Next / previous surface | ⌃Tab / ⌃⇧Tab | Ctrl+Tab across every pane of the workspace / no default * (D-38) |
| Jump to surface 1 to 8 / last | ⌃1–8 / ⌃9 | Alt+1–8 / Alt+9 * (Ctrl+digit sends legacy control codes) |
| Split right / down | ⌘D / ⌘⇧D | Ctrl+Shift+D / Ctrl+Alt+Shift+D |
| Focus pane | ⌥⌘ arrows | Ctrl+arrows * (D-38; the key reaches the program when no pane is that way), also Ctrl+Alt+arrows |
| Focus next / previous pane | — | F6 / Shift+F6 * (D-14); programs in the terminal therefore never receive F6 |
| File explorer | — (Warp: left panel ⌘\) | Ctrl+Shift+E * (D-13) |
| Resize pane | ⌃⇧ H J K L | Alt+Shift+arrows * (Ctrl+Shift+K/L are taken by ⌘K/⌘L) |
| Toggle pane zoom | ⌘⇧↩ | Ctrl+Alt+Shift+Enter |
| Equalize splits | ⌃⌘⇧= | Ctrl+Alt+= * |
| Flash focused panel | ⌘⇧H | Ctrl+Alt+Shift+H |
| Command palette | ⌘⇧P | Ctrl+Shift+P |
| Palette next / previous | ⌃N / ⌃P | Ctrl+N / Ctrl+P, or arrows |
| Open browser in split | ⌘⇧L | Ctrl+Alt+Shift+L |
| Browser: address bar, back, forward, reload, dev tools | ⌘L, ⌘[, ⌘], ⌘R, ⌥⌘I | Ctrl+L (armux), Alt+Left, Alt+Right, F5, F12 (WebView2 built in) * (D-03) |
| Notifications panel | ⌘I | Ctrl+Shift+I |
| Jump to latest unread | ⌘⇧U | Ctrl+Alt+Shift+U |
| Toggle unread | ⌥⌘U | Ctrl+Alt+U |
| Clear scrollback | ⌘K | Ctrl+Shift+K |
| New window | ⌘⇧N | Ctrl+Alt+Shift+N |
| Zoom the window in / out / reset | — | Ctrl+= / Ctrl+- / Ctrl+0 and Ctrl+wheel * (D-27) |
| Terminal font size bigger / smaller / reset | ⌘+ / ⌘- / ⌘0 | Ctrl+Shift+= / Ctrl+Shift+- / Ctrl+Shift+0 |
| Prompt editor | — (Warp: Ctrl+G) | Ctrl+G * (D-23); Claude Code's own Ctrl+G (external editor) no longer reaches it |
| Settings | ⌘, | Ctrl+, * or the gear in the title bar; Ctrl+Shift+, * opens settings.json (Windows Terminal) |
| Copy / paste | ⌘C / ⌘V | Ctrl+Shift+C / Ctrl+V * (D-38), also Ctrl+Shift+V, Ctrl+Insert / Shift+Insert * (Windows Terminal) and the right-click menu |
| Scroll the terminal | — | Ctrl+Shift+Up / Down (line), Ctrl+Shift+PageUp / PageDown (page), Ctrl+Shift+Home / End (top, bottom) * (Windows Terminal, D-35) |
| Split (Windows Terminal keys) | — | Alt+Shift+= right, Alt+Shift+- down, Alt+Shift+D along the longer side * (D-35) |
| Full screen | ⌃⌘F | F11 * (Windows Terminal, browsers, VS Code) |
| Find | ⌘F | Later (Windows Terminal's Ctrl+Shift+F is kept free for it) |
| Quit | ⌘Q | Alt+F4 |

## 9. Windows deviations

| ID | cmux behavior | armux behavior | Reason |
|---|---|---|---|
| D-01 | ⌘-based shortcuts | Ctrl+Shift-based mapping (section 8) | No ⌘ key. Ctrl+letter belongs to the terminal. AltGr handling (Ctrl+Alt combos that type characters on some layouts) is a Phase 3 task with real terminal input. |
| D-02 | WebKit web view | WebView2 (Chromium) | The system web engine on Windows. |
| D-03 | Browser shortcuts follow Safari defaults | Will follow Edge defaults | Same principle: follow the platform browser. |
| D-04 | macOS notification center | Windows toast notifications (App SDK `AppNotificationManager`), shown only while armux is in the background and registered on first use | Platform API. Registering lazily keeps the notification stack out of sessions that never notify. |
| D-05 | `CMUX_*` environment variables | `ARMUX_*` | Avoid confusing tools that detect cmux. |
| D-06 | Unix socket API | Named pipe `\\.\pipe\armux-<user>-<pid>` (D-26), opened with `PipeOptions.CurrentUserOnly`; one JSON request and response line per connection | Windows-native IPC. Other users on the machine cannot connect. |
| D-07 | Reads Ghostty config for themes | Own dark and light tokens (`Theme.cs`) | Ghostty has no Windows build. |
| D-08 | macOS traffic-light window buttons on the left | Windows caption buttons on the right | Maintainer request: macOS-only chrome is replaced by Windows chrome. |
| D-09 | SF Pro | IBM Plex Sans (Zed's interface font, ".ZedSans"), bundled, for the interface and built-in controls; Segoe UI Variable as fallback | Maintainer request (2026-10-25: Windows font; 2026-10-26: Zed's font, to match Lilex). SF Pro is not licensed for Windows apps. Terminal font: D-15. |
| D-10 | Color presets in the right-click menu | Right-click menu opens a 17-swatch flyout | A swatch grid is quicker to scan than 17 text items in a Windows menu. |
| D-11 | App and CLI both named `cmux` | App `armux.exe`; the CLI is `armux.com` beside it. `armux.exe` started with a CLI command (`notify`, `send`, `list`, ...) runs `armux.com` with the same arguments and exits, so shells that ignore PATHEXT (Git Bash) still work. | Windows file names are case-insensitive, so one folder cannot hold two `armux.exe`. PowerShell and cmd try `.com` before `.exe`, so `armux` reaches the CLI there, while Explorer, the Start menu and the taskbar open the app (Visual Studio's devenv.com and devenv.exe work the same way). Until 0.3.0 the app was `ArmuxApp.exe`. |
| D-12 | Selected workspace is a blue block with white text | Gray block (lighter than hover) with normal text colors; the unread badge stays blue | Maintainer request (2026-10-26): on blue, the workspace color rail was hard to see. |
| D-13 | No file explorer | Warp-style file tree, hidden by default. A title-bar button left of the bell, or Ctrl+Shift+E, opens it as a column beside the sidebar, never in place of it. It shows the current workspace's directory; a missing directory falls back to its nearest existing parent. Click opens a file. Context menu: open in new tab, open in new pane, copy path, copy relative path, new file (folders), reveal in File Explorer, open with default app. Icons for common file types; the folders on screen are watched for changes. | Maintainer request (2026-10-26). Warp's "Attach as context" becomes "Insert path in terminal". Browsing outside the workspace: D-32. |
| D-14 | ⌘1–9 and ⌃⌘] / ⌃⌘[ switch workspaces | Ctrl+1–9 and Ctrl+PageDown / Ctrl+PageUp added; F6 / Shift+F6 cycle panes | Maintainer request (2026-10-26). Ctrl+Shift+Tab stayed "previous tab" until D-38 made it the next workspace. Ctrl+digit no longer reaches the shell (Ctrl+2 to Ctrl+7 send rarely used legacy control codes). |
| D-15 | Terminal font from the Ghostty config | Lilex, Zed's default font since 2025, is bundled and the default; any installed font can be chosen | Maintainer request (2026-10-26). |
| D-16 | Native macOS settings window | A full Settings page over the workspace (D-29), saved to `%LOCALAPPDATA%\armux\settings.json` (`ARMUX_SETTINGS_FILE` overrides the path). Numbers use − / + steppers. | WinUI's `NumberBox` crashes NativeAOT builds (missing `TabViewScrollButtonBackground` resource). |
| D-17 | Warp opens files in its code editor | Read-only `FileView` tab in the terminal font: first 256 KB; binary files get an "Open with default app" button. Only settings.json opens editable (D-31). | armux has no editor; building one is out of scope. |
| D-18 | Ghostty terminal core, GPU rendered | Own emulator in `Armux.Core.Vt` (VT500 parser, xterm subset that ConPTY emits and full-screen programs use, packed scrollback, reflow on resize), drawn as one XAML TextBlock per row with background rectangles | Ghostty has no Windows library. No dependency was needed, and XAML text keeps memory low (see section 7). Mouse reporting: D-25. Known gaps: no OSC 8 links, no combining marks, and wide characters rely on the font's glyph widths. |
| D-19 | Shell integration scripts | PowerShell: a prompt wrapper passed with `-EncodedCommand` after the profile loads, emitting OSC 7, plus `LocationChangedAction` (PowerShell 7.1+) so a `cd` inside a longer command line, such as `cd x; claude`, reports at once. cmd: `PROMPT` prefixed with an OSC 7 code. | Lets the sidebar, git branch and explorer follow `cd`. The user's own prompt is kept. |
| D-20 | `cmux` CLI with many commands | `armux notify`, `send`, `open`, `list`, `ping`; the target tab comes from `ARMUX_SURFACE_ID`, which every armux terminal sets, and `armux.com` is on each shell's PATH | The commands an agent needs first. More can follow on the same pipe. |
| D-21 | (macOS) | No WSL profile | Maintainer decision (2026-10-26): a WSL VM costs far more memory than the app. PowerShell and cmd only. |
| D-22 | Ghostty font features | Lilex ligatures are on, so `->` and `----` render as ligatures in the terminal | Zed shows the same. A setting to turn them off can come later. |
| D-23 | (none) | Prompt editor (Ctrl+G, Warp's input editor): a multi-line text box under the terminal with mouse selection, undo and spell check. Enter pastes the text and presses Enter (Send); Shift+Enter starts a new line; Ctrl+Enter or Paste pastes without Enter. Each terminal keeps its draft. | Maintainer requests (2026-10-26; keys changed 2026-10-29): easier prompting. The placeholder keeps its gray when the box has focus. Text goes in as one bracketed paste, so newlines do not submit early. |
| D-24 | (macOS dock badge) | Taskbar status for the current workspace only (ITaskbarList3): a full bar in the accent color while a program works, yellow when a tab needs input (unread notification or bell), red on OSC 9;4 error | Maintainer request (2026-10-26). "Working" comes from OSC 9;4 or Claude Code's title spinner (◐◓◑◒): Claude Code sends OSC 9;4 only to ConEmu, Ghostty and iTerm. The indeterminate marquee was tried and rejected: Windows 11 draws it as a faint dot. A notification while armux is in the background stays unread until you return. |
| D-25 | Mouse handled by Ghostty | Programs that turn on mouse tracking (1000/1002/1003, SGR 1006) get clicks, drags and the wheel; Shift+drag still selects. Selections copy on release (setting). Files dragged from the explorer or Windows Explorer type their path relative to the terminal's directory (up to three `..\` levels; further away or on another drive the path is absolute), quoted for the shell. | Claude Code turns tracking on; without it, the wheel scrolled prompt history. Copy on select and drag-to-type are maintainer requests (Warp behavior). |
| D-26 | (cmux has one window per socket) | Each armux window serves its own pipe, `armux-<user>-<pid>`; terminals get `ARMUX_PIPE`, and the CLI outside armux uses the first window it finds | Two windows used to answer on one pipe name. |
| D-27 | ⌘+ / ⌘- change the font size | Ctrl+= / Ctrl+- / Ctrl+0 (and Ctrl+wheel) zoom the whole window in 10% steps from 70% to 200%, saved in Settings; the terminal font size alone moved to Ctrl+Shift | Maintainer request (2026-10-26). WinUI has no layout scale, so the content is laid out at window size ÷ zoom inside a `Viewbox`. Menus, flyouts and dialogs are popups outside it and stay at 100%. |
| D-28 | Status line from the agent's notifications | For workspaces with live terminals, the status line and a dot follow the terminals: an unread notification (amber), `Working · <program>` (green), an OSC 9;4 error (red), else the latest notification or `Idle · <program>` (gray). Rows are taller (64 px minimum). | Maintainer request (2026-10-26): cmux shows each workspace's state. |
| D-29 | Settings window | Settings page with sections: General (shell, start directory, new-tab directory, last-shell exit, copy on select, settings file, reset), Appearance (Match Windows / Dark / Light / High contrast theme, zoom, interface font, workspace color style), Terminal (font, size, line height, ligatures, cursor shape and blink, scrollback), Keyboard (every palette command rebindable by pressing the keys, with conflict warnings and reset; fixed shortcuts listed), Notifications (toasts, taskbar status, bell, test), Browser (search engine, new tab page), About (version and commit, build, .NET, Windows App SDK, Windows, live memory, links, logs). Changes apply and save at once. | Maintainer request (2026-10-26). High contrast is armux's own token set (white on black, yellow links, cyan accent); "Match Windows" also follows Windows' contrast themes. Shortcut overrides are stored by command title in `keybindings`. Version 0.3.0 is set in `Directory.Build.props`; the SDK appends the commit. |
| D-30 | Closing a window ends its shells | Closing the window (close button, Alt+F4, taskbar) asks first when a shell has started a program that is still running, and names it ("claude is still running"). The rule is a setting: when a program is running (default), always or never, plus "Don't ask again" in the dialog. A terminal drops Enter, Esc and other control characters whose key went down outside it, so the Enter that closes a dialog or the palette never reaches the shell. | Maintainer request (2026-10-26). Running programs are the direct children of each shell, found with a Toolhelp snapshot only when the window is closed; nothing is polled. |
| D-31 | Settings window | The Settings page follows Zed's settings editor: a search box over the section list (it matches setting names and descriptions across sections, plus shortcuts), flat rows with a line under each, dropdowns as menus, and armux's accent on switches. It covers the whole window, and the title bar shows only "Settings" until it closes, so no workspace can be opened from behind it. "Edit in settings.json" opens the file as an editable tab: Ctrl+S checks the JSON, writes it and applies it. The file is watched, so a save from any editor applies at once; JSON that does not parse is ignored until it does. Comments and trailing commas are allowed. | Maintainer request (2026-10-29): the page looked like Windows 10 Settings and its title-bar buttons still opened workspaces. A settings tab inside a workspace (Warp) was the other option offered; a full-window page was chosen because settings belong to the app, not a workspace. Saving from the page rewrites the file, which drops comments. |
| D-32 | (Warp's tree shows the project only) | The explorer can browse anywhere: an up button (Backspace), a path box that takes a typed or pasted folder or file (`~` and `%VARS%` work), a places menu (terminal's folder, home, Desktop, Documents, Downloads, each OneDrive, every ready drive), and double-click to browse into a folder. A browsed folder is kept per workspace until "back to the terminal's folder"; until then the explorer does not follow `cd`. "Insert path in terminal" types the path as a drop would. | Maintainer request (2026-10-29): drag in paths and files from anywhere. |
| D-33 | (cmux restores windows through macOS state restoration) | The main window saves its workspaces (title, color, pin, folder), split tree with ratios, and tabs (terminal shell and folder, browser address, file path) to `session.json` beside settings.json: when it closes, and every 30 s if something changed. The next start reopens them; each terminal starts a new shell in its last folder. Setting: General > Reopen workspaces (on by default; off deletes the saved session). `--panes`, `--no-restore`, mock scenarios and New Window never restore or save. | Maintainer request (2026-10-29). Running programs and scrollback can't come back: their processes end with the window (`claude --continue` resumes a conversation). The 30 s save covers sign-out and shutdown, which can end armux without a close event. Terminals in a workspace start when it is first shown, as before. |
| D-34 | Blue ring for every notification | A program waiting for an answer turns orange: pane ring, tab dot and the workspace's status dot and line ("Waiting for you · Claude Code"). It starts with the program's bell (not a bell at a bare shell prompt) or an OSC 9 / 99 / 777 notification, and lasts until a key or paste reaches that terminal, even after the notification was read. Unread notifications stay blue. The taskbar shows yellow: Windows has no orange progress state. | Maintainer request (2026-10-29): see at a glance which agent is stuck on a question or permission prompt. Mouse reports and focus events don't count as an answer. |
| D-35 | macOS shortcuts | Settings moves to Ctrl+, (Windows Terminal, VS Code), and Ctrl+Shift+, opens settings.json, as in Windows Terminal. Also from Windows Terminal: Ctrl+Insert / Shift+Insert copy and paste, Ctrl+Shift+arrows / PageUp / PageDown / Home / End scroll, Alt+Shift+= / - / D split, and F11 full screen. All except the copy, paste and split aliases are rebindable palette commands. | Maintainer request (2026-10-29): conventional defaults. Not added: Ctrl+Shift+F (no find yet), Ctrl+W / Ctrl+T / Ctrl+B (shell keys), Alt+Enter (Claude Code uses it for a new line). |
| D-36 | macOS sheets and alerts | armux draws its own dialogs (`ArmuxDialog`): a rounded card on a dimmed window, the Settings page's flat Cancel button and a blue primary button; Enter is the primary button, Esc cancels. Used for close confirmation, rename, workspace color, reset and new file. | Maintainer request (2026-10-29): ContentDialog made in code drew WinUI's old square template, and its rounded style (DefaultContentDialogStyle) can't be reached from code in this app, not even through a style in App.xaml. The buttons keep ContentDialog's automation ids for UI tests. |
| D-37 | cmux's browser automation API | `armux mcp` (in armux.com) is an MCP server on stdio that Claude Code starts. Its 14 `browser_*` tools (tabs, open, navigate, history, snapshot with element refs, text, click, fill, type, press key, scroll, evaluate, screenshot, console) become pipe requests; the app runs them through the tab's WebView2 DevTools Protocol (`Runtime.evaluate`, `Input.dispatchMouseEvent` / `dispatchKeyEvent` / `insertText`, `Page.captureScreenshot`). Off by default: Settings > Browser > Let agents control the browser (`BrowserControl`). Tools use only tabs that already have a page and never create a WebView, so the 2-live cap holds; a hidden tab is resumed for the call and suspended again. Console capture starts on the first `browser_console` call. `armux open` now prints the new tab's id and leaves the keyboard where it was. The pipe serves several clients at once, so a slow page can't hold up `armux notify`. | Maintainer request (2026-10-31): the same browser access Claude has in wmux. The MCP is written by hand (about 200 lines, System.Text.Json nodes): the official C# SDK adds Microsoft.Extensions dependencies and reflection that NativeAOT trimming warns about. Input goes into the page only (DevTools), never through SendInput. Password values are hidden in snapshots. |
| D-38 | ⌃Tab / ⌃⇧Tab move between surfaces | Ctrl+Tab cycles every tab in the workspace, pane by pane (a pane with one tab takes its turn); Ctrl+Shift+Tab is the next workspace (Ctrl+PageDown stays as a fixed alias; Previous Tab has no default). Ctrl+arrows focus the pane in that direction; with no pane there, or in a text box, the key goes on to the program (word jumps in the shell). Ctrl+Alt+arrows stay as fixed aliases. Ctrl+V pastes when a terminal has the keyboard and is a text box's own paste elsewhere; Ctrl+Shift+V is a fixed alias. A command can now decline its key (`CmdIf`). | Maintainer request (2026-10-31). Ctrl+V took ^V (vim block select, quoted insert) from programs, as in Windows Terminal; rebind Paste to give it back. |
| D-39 | (macOS windows have a fixed title bar layout) | The window has a minimum size (`OverlappedPresenter.PreferredMinimumWidth` / `Height`, physical pixels): the title bar's button column (sidebar width, 180 with the sidebar hidden), 140 for the title, and the caption buttons' width (`TitleBar.RightInset`), all times zoom and DPI; height 220 × zoom. It is recomputed on sidebar toggle, zoom and DPI changes, and grows the window if it is below it. The title keeps clear of the caption buttons and trims a long name with "…". | Maintainer request (2026-10-31): a narrow window drew the title-bar buttons and title under minimize, maximize and close. |
| D-40 | (cmux's agent integration needs no setup) | While `BrowserControl` is on, armux writes an `"armux"` entry (`command` = its own `armux.com`, `args` = `["mcp"]`) into `mcpServers` in Claude Code's user config (`~/.claude.json`, or `$CLAUDE_CONFIG_DIR/.claude.json`; `ARMUX_CLAUDE_CONFIG` for tests). It checks at start and when the setting turns on, writes only if the entry is missing or points elsewhere, and removes it (only if its command is an `armux.com`) when the setting turns off. It never creates the file. Every other key is kept; the edit is redone if Claude Code wrote the file meanwhile, and the result is swapped in from a temp file (`Armux.Core/ClaudeMcp.cs`). | Maintainer request (2026-10-31): wmux needs no `claude mcp add`; it writes the same user-level entry. Manual registration was rejected as an extra step that breaks when armux moves. |
| D-41 | (macOS menu shortcuts work wherever focus is) | Every shortcut works while the window is active, wherever the keyboard is. The root's PreviewKeyDown runs them over the Settings page too: Settings, full screen, window zoom, theme and the palette leave the page open, any other command closes it first. Caret, selection, clipboard, undo and Enter keys (`Chord.IsTextEditing`) stay with a focused text box. If focus is lost (a control removed, a click on something that takes no focus, the window re-activated), it goes back to the terminal or, over Settings, the key sink. WebView2 keeps keys from the window, so every page gets a script (`BrowserHost.KeyScript`, added before the page's own scripts) whose first capture listener stops armux's chords and posts them to armux. Only real key presses (`isTrusted`) are sent, with a per-process token the page can't read, so a page can't run commands. Text-editing chords and keys armux doesn't use stay with the page. Top-level documents only: keys typed inside an iframe stay with it. | Maintainer request (2026-10-31): shortcuts only worked with a terminal focused, not in Settings or the browser. |

## 10. Dependency register

Allowed licenses, direct and transitive: MIT, Apache-2.0, BSD (any clause count), Unicode-3.0 and Zlib. The Windows App SDK is approved as a platform SDK (2026-10-25, when the maintainer chose WinUI). SIL OFL-1.1 is approved **for bundled fonts only** (2026-10-26, Lilex). Anything else stops for maintainer review.

| Dependency | Where | License | Why |
|---|---|---|---|
| Microsoft.WindowsAppSDK 1.8.260921001 | App | Microsoft Software License Terms (approved platform SDK) | WinUI 3, windowing, title bar |
| Microsoft.Web.WebView2 (transitive) | App | BSD-3-Clause | Browser panel |
| Microsoft.Windows.SDK.BuildTools (transitive) | Build only | Microsoft Software License Terms | WinRT projections at build time |
| Lilex 2.630 (`Lilex[wght].ttf`, shipped as `Assets/Fonts/Lilex-VF.ttf`) | App | SIL OFL-1.1 (approved for fonts); `OFL-Lilex.txt` ships beside it | Default terminal font |
| IBM Plex Sans 1.1.0 (Regular and SemiBold `.ttf`) | App | SIL OFL-1.1 (approved for fonts), reserved font name "Plex", shipped unmodified; `OFL-IBMPlexSans.txt` ships beside it | Default interface font (Zed's) |
| MSTest.Sdk 4.0.2 | Tests only | MIT | Unit tests |
| `actions/checkout@v7`, `actions/cache@v6`, `actions/upload-artifact@v7`, `actions/setup-dotnet@v6` | CI | MIT | Check out, cache NuGet, install .NET, publish the build |

Phase 3 added no dependencies: ConPTY, the named pipe, JSON (source generated) and toasts come from Windows, .NET and the approved Windows App SDK. `Armux.Core` and the `armux` CLI have no package dependencies. Third-party notices for WebView2 (BSD-3-Clause attribution) ship with the first release.
