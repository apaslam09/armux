using Armux.Core;

namespace Armux.Core.Tests;

[TestClass]
public sealed class SessionTests
{
    [TestMethod]
    public void SessionRoundTripKeepsLayoutTabsAndFolders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "armux-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var notes = Path.Combine(dir, "notes.md");
        File.WriteAllText(notes, "");

        var state = new AppState();
        var first = new Surface { Kind = PanelKind.Terminal, Title = "pwsh", Shell = "pwsh" };
        var ws = MockData.NewWorkspace("api", "", "", "~\\dev", first);
        ws.Color = "#EF4444";
        ws.Pinned = true;
        var right = Layout.Split(ws, ws.Focused!, SplitDirection.Right, new Surface { Kind = PanelKind.Terminal, Title = "cmd", Shell = "cmd" });
        ((SplitNode)ws.Root).Ratio = 0.3;
        var file = new Surface { Kind = PanelKind.File, Title = "notes.md", FilePath = notes };
        right.Surfaces.Add(file);
        right.Surfaces.Add(new Surface { Kind = PanelKind.File, Title = "gone.txt", FilePath = Path.Combine(dir, "gone.txt") });
        right.Surfaces.Add(new Surface { Kind = PanelKind.Browser, Title = "Docs", Url = "https://example.com/" });
        right.Selected = file;
        ws.Focused = right;
        state.Workspaces.Add(ws);
        state.Workspaces.Add(MockData.NewWorkspace("second", "", "", "~", new Surface { Kind = PanelKind.Terminal, Title = "pwsh", Shell = "pwsh" }));
        state.Current = state.Workspaces[1];

        var json = SessionStore.ToJson(SessionStore.Capture(state, s => ReferenceEquals(s, first) ? dir : null));
        var path = Path.Combine(dir, "session.json");
        SessionStore.Save(path, json);
        var back = SessionStore.Restore(SessionStore.Load(path)!, "pwsh")!;

        Assert.HasCount(2, back.Workspaces);
        Assert.AreEqual("second", back.Current!.Title);
        var api = back.Workspaces[0];
        Assert.AreEqual(("api", "#EF4444", true, "~\\dev"), (api.Title, api.Color, api.Pinned, api.Cwd));
        var split = (SplitNode)api.Root;
        Assert.AreEqual(SplitDirection.Right, split.Direction);
        Assert.AreEqual(0.3, split.Ratio);
        var panes = api.Panes.ToList();
        Assert.AreEqual(dir, panes[0].Surfaces[0].StartDirectory);       // the terminal's last folder
        Assert.AreEqual("cmd", panes[1].Surfaces[0].Shell);
        // The missing file is dropped; the selected tab and focused pane survive.
        CollectionAssert.AreEqual(new[] { PanelKind.Terminal, PanelKind.File, PanelKind.Browser }, panes[1].Surfaces.Select(s => s.Kind).ToArray());
        Assert.AreEqual(notes, panes[1].Selected!.FilePath);
        Assert.AreSame(panes[1], api.Focused);
        Assert.AreEqual("https://example.com/", panes[1].Surfaces[2].Url);
        Directory.Delete(dir, true);
    }

    [TestMethod]
    public void BrokenOrEmptySessionsRestoreNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), "armux-tests", Guid.NewGuid().ToString("N") + ".json");
        Assert.IsNull(SessionStore.Load(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        Assert.IsNull(SessionStore.Load(path));
        Assert.IsNull(SessionStore.Restore(new SessionFile(), "pwsh"));
        // A pane with no usable tabs still gets a terminal.
        var state = SessionStore.Restore(new SessionFile { Workspaces = [new SessionWorkspace { Root = new SessionNode { Tabs = [] } }] }, "cmd")!;
        Assert.AreEqual("cmd", state.Workspaces[0].Panes.Single().Selected!.Shell);
        File.Delete(path);
    }
}
