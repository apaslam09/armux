using System.Text;
using System.Text.Json;
using Armux.Core;

namespace Armux.App;

/// <summary>
/// The browser half of the control API, behind <c>armux mcp</c>: coding agents read pages and click, type and run
/// JavaScript through the Chrome DevTools Protocol of the tab's own WebView2 (D-37). Nothing reaches Windows input,
/// and only <see cref="Settings.BrowserControl"/> turns it on.
/// </summary>
public sealed partial class MainWindow
{
    const int MaxOutput = 100_000;

    async Task<Response> HandleBrowserAsync(Request r)
    {
        if (!Theme.Settings.BrowserControl)
            return new Response { Error = "browser control is off: turn on Settings > Browser > Let agents control the browser" };
        if (r.Command == "browser-tabs") return new Response { Ok = true, Output = BrowserTabs() };
        if (BrowserFor(r.Surface) is not { } panel)
            return new Response { Error = r.Surface > 0 ? $"no browser tab {r.Surface} (or it has never been shown)" : "no browser tab open: use browser_open" };
        try
        {
            string output = await RunBrowserCommand(panel, r);
            // A screenshot is base64 and must arrive whole; cutting it made "Invalid Base64 string".
            bool cut = output.Length > MaxOutput && r.Command != "browser-screenshot";
            return new Response { Ok = true, Output = cut ? output[..MaxOutput] + "\n… (cut)" : output };
        }
        catch (InvalidOperationException e) { return new Response { Error = e.Message }; }
        catch (TimeoutException) { return new Response { Error = "the page did not answer in time" }; }
        finally { panel.Settle(); }
    }

    async Task<string> RunBrowserCommand(BrowserPanel p, Request r)
    {
        switch (r.Command)
        {
            case "browser-navigate": return await p.NavigateAsync(r.Body);
            case "browser-back": return await p.HistoryAsync("back");
            case "browser-forward": return await p.HistoryAsync("forward");
            case "browser-reload": return await p.HistoryAsync("reload");
            case "browser-snapshot": return Str(await p.EvalAsync(SnapshotJs));
            case "browser-text":
                int max = int.TryParse(r.Body, out int m) && m > 0 ? m : 20_000;
                var text = Str(await p.EvalAsync("document.body ? document.body.innerText : ''"));
                return text.Length > max ? text[..max] + $"\n… ({text.Length - max} more characters)" : text;
            case "browser-eval":
                var value = await p.EvalAsync(r.Body);
                return value is null ? "undefined" : value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? "" : value.Value.GetRawText();
            case "browser-click":
            {
                var at = await p.EvalAsync($"({LocateJs})({BrowserPanel.Json(r.Selector)})");
                if (at is not { ValueKind: JsonValueKind.Object } pos) throw new InvalidOperationException($"no element {r.Selector}: take a new snapshot");
                string xy = $"\"x\":{pos.GetProperty("x").GetDouble():0.#},\"y\":{pos.GetProperty("y").GetDouble():0.#}";
                await Cdp(p, "Input.dispatchMouseEvent", $"{{\"type\":\"mouseMoved\",{xy}}}");
                await Cdp(p, "Input.dispatchMouseEvent", $"{{\"type\":\"mousePressed\",{xy},\"button\":\"left\",\"clickCount\":1}}");
                await Cdp(p, "Input.dispatchMouseEvent", $"{{\"type\":\"mouseReleased\",{xy},\"button\":\"left\",\"clickCount\":1}}");
                return $"clicked {pos.GetProperty("tag").GetString()?.ToLowerInvariant()} {r.Selector}";
            }
            case "browser-fill":
            {
                var how = Str(await p.EvalAsync($"({FocusJs})({BrowserPanel.Json(r.Selector)}, {BrowserPanel.Json(r.Body)})"));
                if (how == "missing") throw new InvalidOperationException($"no element {r.Selector}: take a new snapshot");
                if (how != "ok") return how;
                // The field's text is selected, so typing replaces it; an empty value deletes it.
                if (r.Body.Length == 0) await Key(p, "Backspace");
                else await Cdp(p, "Input.insertText", $"{{\"text\":{BrowserPanel.Json(r.Body)}}}");
                return $"filled {r.Selector}";
            }
            case "browser-type":
                if (r.Selector.Length > 0 && Str(await p.EvalAsync($"({FocusJs})({BrowserPanel.Json(r.Selector)}, null, true)")) == "missing")
                    throw new InvalidOperationException($"no element {r.Selector}: take a new snapshot");
                await Cdp(p, "Input.insertText", $"{{\"text\":{BrowserPanel.Json(r.Body)}}}");
                return $"typed {r.Body.Length} characters";
            case "browser-key":
                await Key(p, r.Body);
                return "pressed " + r.Body;
            case "browser-scroll":
                int dy = int.TryParse(r.Body, out int d) ? d : 600;
                return Str(await p.EvalAsync($"(() => {{ scrollBy(0, {dy}); return 'scroll ' + Math.round(scrollY) + ' of ' + Math.max(0, document.documentElement.scrollHeight - innerHeight); }})()"));
            case "browser-screenshot":
            {
                using var doc = await p.CdpAsync("Page.captureScreenshot", "{\"format\":\"png\"}").WaitAsync(TimeSpan.FromSeconds(10));
                return doc.RootElement.GetProperty("data").GetString() ?? "";
            }
            case "browser-console":
                var (started, lines) = await p.ConsoleAsync();
                if (started) return "console capture started: messages from now on are kept (call again to read them)";
                return lines.Count == 0 ? "no console messages" : string.Join('\n', lines);
            default:
                throw new InvalidOperationException($"unknown command \"{r.Command}\"");
        }
    }

    static async Task Cdp(BrowserPanel p, string method, string parameters) => (await p.CdpAsync(method, parameters)).Dispose();

    static string Str(JsonElement? v) => v is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : v?.GetRawText() ?? "";

    /// <summary>Presses a key in the page, with modifiers: "Enter", "Tab", "ArrowDown", "Ctrl+A", "Shift+Tab".</summary>
    static async Task Key(BrowserPanel p, string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new InvalidOperationException("give a key, like Enter or Ctrl+A");
        int mods = 0;
        foreach (var m in parts[..^1])
            mods |= m.ToLowerInvariant() switch
            {
                "alt" => 1, "ctrl" or "control" => 2, "meta" or "win" or "cmd" => 4, "shift" => 8,
                _ => throw new InvalidOperationException($"unknown modifier {m}"),
            };
        string k = parts[^1];
        (string key, string code, int vk, string text) = k.ToLowerInvariant() switch
        {
            "enter" or "return" => ("Enter", "Enter", 13, "\r"),
            "tab" => ("Tab", "Tab", 9, ""),
            "escape" or "esc" => ("Escape", "Escape", 27, ""),
            "backspace" => ("Backspace", "Backspace", 8, ""),
            "delete" or "del" => ("Delete", "Delete", 46, ""),
            "space" => (" ", "Space", 32, " "),
            "arrowup" or "up" => ("ArrowUp", "ArrowUp", 38, ""),
            "arrowdown" or "down" => ("ArrowDown", "ArrowDown", 40, ""),
            "arrowleft" or "left" => ("ArrowLeft", "ArrowLeft", 37, ""),
            "arrowright" or "right" => ("ArrowRight", "ArrowRight", 39, ""),
            "home" => ("Home", "Home", 36, ""),
            "end" => ("End", "End", 35, ""),
            "pageup" => ("PageUp", "PageUp", 33, ""),
            "pagedown" => ("PageDown", "PageDown", 34, ""),
            _ when k.Length == 1 && char.IsAsciiLetterOrDigit(k[0]) =>
                (k, char.IsAsciiLetter(k[0]) ? "Key" + char.ToUpperInvariant(k[0]) : "Digit" + k, char.ToUpperInvariant(k[0]), k),
            _ when k.Length == 1 => (k, "", 0, k),
            _ => throw new InvalidOperationException($"unknown key {k}"),
        };
        // Ctrl or Alt chords are commands, not text.
        if ((mods & 3) != 0) text = "";
        string common = $"\"modifiers\":{mods},\"key\":{BrowserPanel.Json(key)},\"code\":\"{code}\",\"windowsVirtualKeyCode\":{vk}";
        await Cdp(p, "Input.dispatchKeyEvent", text.Length > 0
            ? $"{{\"type\":\"keyDown\",{common},\"text\":{BrowserPanel.Json(text)}}}"
            : $"{{\"type\":\"rawKeyDown\",{common}}}");
        await Cdp(p, "Input.dispatchKeyEvent", $"{{\"type\":\"keyUp\",{common}}}");
    }

    /// <summary>The browser tab a request names; with none, the focused one, then one shown in this workspace, then the last shown.</summary>
    BrowserPanel? BrowserFor(int id)
    {
        BrowserPanel? Panel(Surface? s) => s is { Kind: PanelKind.Browser } && panels.TryGetValue(s, out var p) ? p as BrowserPanel : null;
        if (id > 0) return Panel(state.Workspaces.SelectMany(w => w.Panes).SelectMany(p => p.Surfaces).FirstOrDefault(s => s.Id == id));
        if (Panel(Focused?.Selected) is { } focused) return focused;
        if (Ws?.Panes.Select(p => Panel(p.Selected)).FirstOrDefault(b => b is not null) is { } shown) return shown;
        return browsers.MostRecent;
    }

    string BrowserTabs()
    {
        var sb = new StringBuilder();
        foreach (var w in state.Workspaces)
            foreach (var p in w.Panes)
                foreach (var s in p.Surfaces.Where(s => s.Kind == PanelKind.Browser))
                {
                    var panel = panels.TryGetValue(s, out var v) ? v as BrowserPanel : null;
                    string where = panel?.Web is null ? "unloaded" : panel.IsLoaded ? "visible" : "hidden";
                    bool focused = ReferenceEquals(w, Ws) && ReferenceEquals(p, Focused) && ReferenceEquals(p.Selected, s);
                    sb.Append(focused ? "* " : "  ").Append(s.Id).Append("  ").Append(where).Append("  [").Append(w.Title).Append("]  ")
                      .Append(s.Title).Append("  ").AppendLine(s.Url);
                }
        return sb.Length == 0 ? "no browser tabs" : sb.ToString();
    }

    // Page scripts. Elements get a data-armux-ref ("e12") that click, fill and type accept in place of a CSS selector.

    const string SnapshotJs = """
        (() => {
          const clean = s => (s || '').replace(/\s+/g, ' ').trim().slice(0, 80);
          const visible = el => { const r = el.getBoundingClientRect(), s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          const secret = el => el.tagName === 'INPUT' && el.type === 'password';
          const name = el => clean(el.getAttribute('aria-label') || (el.labels && el.labels[0] && el.labels[0].innerText)
            || el.innerText || el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('alt')
            || (secret(el) ? '' : el.value) || el.name);
          const input = { checkbox: 'checkbox', radio: 'radio', submit: 'button', button: 'button', reset: 'button' };
          const tags = { A: 'link', BUTTON: 'button', SELECT: 'combobox', TEXTAREA: 'textbox', SUMMARY: 'button' };
          const role = el => el.getAttribute('role') || tags[el.tagName]
            || (el.tagName === 'INPUT' ? input[el.type] || 'textbox' : el.isContentEditable ? 'textbox' : el.tagName.toLowerCase());
          document.querySelectorAll('[data-armux-ref]').forEach(e => e.removeAttribute('data-armux-ref'));
          const out = [];
          let n = 0;
          const query = 'h1,h2,h3,a[href],button,input:not([type=hidden]),textarea,select,summary,[role=button],[role=link],[role=tab],'
            + '[role=menuitem],[role=checkbox],[role=option],[role=textbox],[contenteditable=""],[contenteditable=true]';
          for (const el of document.querySelectorAll(query)) {
            if (!visible(el)) continue;
            if (/^H[1-3]$/.test(el.tagName)) { const t = clean(el.innerText); if (t) out.push(el.tagName.toLowerCase() + ' "' + t + '"'); continue; }
            if (n >= 500) { out.push('… more elements not listed'); break; }
            const ref = 'e' + (++n);
            el.setAttribute('data-armux-ref', ref);
            let line = '[' + ref + '] ' + role(el) + ' "' + name(el) + '"';
            if (el.type === 'checkbox' || el.type === 'radio') line += el.checked ? ' checked' : ' unchecked';
            else if (/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) && el.value) line += secret(el) ? ' value=(hidden)' : ' value="' + clean(el.value) + '"';
            if (el.disabled) line += ' disabled';
            // [role=link] also matches <a> without href (Google's results page has them).
            if (el.tagName === 'A' && el.getAttribute('href')) line += ' -> ' + el.getAttribute('href').slice(0, 120);
            out.push(line);
          }
          return 'url: ' + location.href + '\ntitle: ' + document.title + '\nscroll: ' + Math.round(scrollY) + ' of '
            + Math.max(0, document.documentElement.scrollHeight - innerHeight) + '\n\n' + out.join('\n');
        })()
        """;

    /// <summary>Finds an element by ref or selector, scrolls it into view and returns its centre in page pixels.</summary>
    const string LocateJs = """
        s => {
          const el = /^e\d+$/.test(s) ? document.querySelector('[data-armux-ref="' + s + '"]') : document.querySelector(s);
          if (!el) return null;
          el.scrollIntoView({ block: 'center', inline: 'center' });
          const r = el.getBoundingClientRect();
          return { x: r.left + r.width / 2, y: r.top + r.height / 2, tag: el.tagName };
        }
        """;

    /// <summary>Focuses an element and selects its text so typing replaces it; a select element picks the option directly.</summary>
    const string FocusJs = """
        (s, value, keep) => {
          const el = /^e\d+$/.test(s) ? document.querySelector('[data-armux-ref="' + s + '"]') : document.querySelector(s);
          if (!el) return 'missing';
          el.scrollIntoView({ block: 'center' });
          el.focus();
          if (keep) return 'ok';
          if (el.tagName === 'SELECT') {
            const o = [...el.options].find(o => o.value === value || o.text.trim() === value);
            if (!o) return 'no option "' + value + '": ' + [...el.options].map(o => o.text.trim()).join(', ');
            el.value = o.value;
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            return 'selected ' + o.text.trim();
          }
          if (typeof el.select === 'function') el.select();
          else { const r = document.createRange(); r.selectNodeContents(el); const g = getSelection(); g.removeAllRanges(); g.addRange(r); }
          return 'ok';
        }
        """;
}
