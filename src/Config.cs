using System.Collections.Generic;
using System.Linq;

namespace GameSwitcher
{
    class GameRef
    {
        public string Key, Name;
        public string[] Exes;
    }

    // settings.json as written by the GUI
    class Config
    {
        public Dictionary<string, object> Raw = new Dictionary<string, object>();
        public List<GameRef> Selected = new List<GameRef>();
        public List<GameRef> Library = new List<GameRef>();
        public bool AutoDetect, Notifications = true;
        public string Hotkey = "";

        public static Config Load()
        {
            var c = new Config();
            var raw = Json.ReadFile(Paths.Data("settings.json"));
            if (raw == null) return c;
            c.Raw = raw;
            c.Selected = Games(raw.List("Selected"));
            c.Library = Games(raw.List("Library"));
            c.AutoDetect = raw.Bool("AutoDetect");
            c.Notifications = raw.Bool("Notifications", true);
            c.Hotkey = raw.Str("Hotkey");
            return c;
        }

        static List<GameRef> Games(List<object> items) =>
            items.OfType<Dictionary<string, object>>()
                 .Select(g => new GameRef { Key = g.Str("Key"), Name = g.Str("Name"), Exes = g.Strings("Exes") })
                 .Where(g => g.Key.Length > 0).ToList();

        // Actions for a game: its own profile if it has one, otherwise the default profile
        public ActionSet ActionsFor(string gameKey)
        {
            var profiles = Raw.Dict("Profiles");
            if (!string.IsNullOrEmpty(gameKey) && profiles != null && profiles.TryGetValue(gameKey, out object own))
                return new ActionSet(own as Dictionary<string, object>);
            var a = new ActionSet(Raw.Dict("Actions"));
            if (Raw.Dict("Actions") == null && Raw.Get("TurnOffAfterGame") is bool turnOff)   // version 1.0 format
                a["Hdr"]["Revert"] = turnOff;
            return a;
        }
    }

    // A complete action set: defaults overlaid with the stored values
    class ActionSet
    {
        readonly Dictionary<string, Dictionary<string, object>> a = Defaults();

        public ActionSet(Dictionary<string, object> stored)
        {
            if (stored == null) return;
            foreach (var action in stored)
            {
                if (!a.ContainsKey(action.Key) || !(action.Value is Dictionary<string, object> fields)) continue;
                foreach (var f in fields) a[action.Key][f.Key] = f.Value;
            }
        }

        public Dictionary<string, object> this[string action] => a[action];

        public bool On(string action) => a[action].Bool("Enabled");
        public bool Revert(string action) => a[action].Bool("Revert", true);

        static Dictionary<string, Dictionary<string, object>> Defaults()
        {
            Dictionary<string, object> D(params object[] kv)
            {
                var d = new Dictionary<string, object>();
                for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
                return d;
            }
            return new Dictionary<string, Dictionary<string, object>>
            {
                ["Hdr"] = D("Enabled", true, "Revert", true),
                ["Sdr"] = D("Enabled", false, "Level", 30, "Revert", true),
                ["Display"] = D("Enabled", false, "Width", 0, "Height", 0, "Hz", 0, "Revert", true),
                ["Monitors"] = D("Enabled", false, "Revert", true),
                ["NightLight"] = D("Enabled", false, "Revert", true),
                ["Audio"] = D("Enabled", false, "DeviceId", "", "DeviceName", "", "Revert", true),
                ["Mic"] = D("Enabled", false, "DeviceId", "", "DeviceName", "", "Revert", true),
                ["Volume"] = D("Enabled", false, "Level", 50, "Revert", true),
                ["Power"] = D("Enabled", false, "PlanId", "", "PlanName", "", "Revert", true),
                ["KeepAwake"] = D("Enabled", false),
                ["CloseApps"] = D("Enabled", false, "Apps", new object[0], "Revert", true),
                ["StartApps"] = D("Enabled", false, "Apps", new object[0], "Revert", true),
                ["Script"] = D("Enabled", false, "Start", "", "End", ""),
            };
        }
    }
}
