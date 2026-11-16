using System;
using System.Diagnostics;
using System.IO;
using GameSwitcher.Native;
using static GameSwitcher.Strings;

namespace GameSwitcher
{
    // What couch mode changed; kept in couch.json so a crash or restart can still revert it
    class CouchState
    {
        public string PreviousDisplay { get; set; }   // "extend" or "internal"; null: display not changed
        public DeviceChange Audio { get; set; }
    }

    // Couch mode: when a controller connects, switch to the TV (second screen only, duplicate or extend)
    // and its audio, and run an optional command; switch back when no controller is connected anymore.
    static class Couch
    {
        static string StatePath => Paths.Data("couch.json");

        public static CouchState LoadPending() => Json.ReadFile<CouchState>(StatePath);

        static void DisplaySwitch(string mode) =>
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "DisplaySwitch.exe"), "/" + mode) { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(10000);

        public static CouchState Start(CouchSettings s)
        {
            var state = new CouchState();
            Json.WriteFile(StatePath, state);
            if (s.Display == "external" || s.Display == "clone" || s.Display == "extend")
            {
                try
                {
                    state.PreviousDisplay = DisplayConfig.CountActiveDisplays() > 1 ? "extend" : "internal";
                    if (state.PreviousDisplay != s.Display) DisplaySwitch(s.Display);
                    else state.PreviousDisplay = null;
                    Json.WriteFile(StatePath, state);
                }
                catch (Exception ex) { Log.Write(T("log.actionError", T("settings.couch"), ex.Message)); }
            }
            if (s.AudioId.Length > 0)
            {
                try
                {
                    // the TV's audio device often appears only after the display switch
                    for (int i = 0; i < 10 && !Audio.IsConnected(Audio.Output, s.AudioId); i++) System.Threading.Thread.Sleep(500);
                    string previous = Audio.GetDefaultId(Audio.Output);
                    if (previous != s.AudioId && Audio.IsConnected(Audio.Output, s.AudioId))
                    {
                        state.Audio = new DeviceChange { PreviousId = previous, PreviousName = Audio.GetDefaultName(Audio.Output), SetId = s.AudioId };
                        Audio.SetDefault(s.AudioId, false);
                        Json.WriteFile(StatePath, state);
                        Log.Write(T("log.audioSwitched", s.AudioName));
                    }
                }
                catch (Exception ex) { Log.Write(T("log.audioError", ex.Message)); }
            }
            if (s.Command.Trim().Length > 0)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c " + s.Command) { UseShellExecute = false, CreateNoWindow = true });
                    Log.Write(T("log.scriptRun", s.Command));
                }
                catch (Exception ex) { Log.Write(T("log.actionError", T("settings.couch"), ex.Message)); }
            }
            Log.Write(T("log.couchOn"));
            return state;
        }

        public static void End(CouchSettings s, CouchState state)
        {
            if (state != null && s.Revert)
            {
                if (state.Audio != null)
                {
                    try
                    {
                        if (Audio.GetDefaultId(Audio.Output) == state.Audio.SetId && Audio.IsConnected(Audio.Output, state.Audio.PreviousId))
                        {
                            Audio.SetDefault(state.Audio.PreviousId, false);
                            Log.Write(T("log.audioSwitched", state.Audio.PreviousName));
                        }
                    }
                    catch (Exception ex) { Log.Write(T("log.audioError", ex.Message)); }
                }
                if (state.PreviousDisplay != null)
                {
                    try { DisplaySwitch(state.PreviousDisplay); }
                    catch (Exception ex) { Log.Write(T("log.actionError", T("settings.couch"), ex.Message)); }
                }
            }
            Log.Write(T("log.couchOff"));
            try { File.Delete(StatePath); } catch { }
        }
    }
}
