using Armux.Core;

namespace Armux.App;

/// <summary>
/// Session restore (deviation D-33): the main window saves its workspaces, splits and tabs to session.json beside
/// settings.json every 30 seconds when something changed, and when it closes; the next start reopens them
/// (<see cref="LaunchOptions.BuildState"/>). Setting: General > Reopen workspaces.
/// </summary>
public sealed partial class MainWindow
{
    public static string SessionPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!, "session.json");

    Microsoft.UI.Dispatching.DispatcherQueueTimer? sessionTimer;
    string lastSession = "";

    void StartSessionSaving()
    {
        if (!opts.Persistent) return;
        // Also saved periodically: Windows can end armux at sign-out or shutdown without a close event.
        sessionTimer = DispatcherQueue.CreateTimer();
        sessionTimer.Interval = TimeSpan.FromSeconds(30);
        sessionTimer.Tick += (_, _) => SaveSession();
        sessionTimer.Start();
    }

    void SaveSession()
    {
        if (!opts.Persistent) return;
        try
        {
            if (!Theme.Settings.RestoreSession)
            {
                // Off means nothing is reopened later either, even after turning it back on.
                if (File.Exists(SessionPath)) File.Delete(SessionPath);
                lastSession = "";
                return;
            }
            var json = SessionStore.ToJson(SessionStore.Capture(state, s => sessions.GetValueOrDefault(s)?.Cwd));
            if (json == lastSession) return;
            SessionStore.Save(SessionPath, json);
            lastSession = json;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log(nameof(SaveSession), e); }
    }
}
