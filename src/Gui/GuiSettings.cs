using System.Collections.Generic;
using System.Linq;

namespace GameSwitcher.Gui
{
    // Everything the window edits; saved to settings.json, which the service reads (see Config)
    class GuiSettings
    {
        public Dictionary<string, GameRef> Selected = new Dictionary<string, GameRef>();
        public Dictionary<string, ActionSet> Profiles = new Dictionary<string, ActionSet>();
        public ActionSet Actions = new ActionSet(null);
        public List<string> CustomFolders = new List<string>(), CustomExes = new List<string>();
        public string Language = "", Hotkey = "Ctrl+Alt+G", Theme = "system";
        public bool AutoDetect, Notifications = true, CheckUpdates = true, SetupDone, TrayHdrToggle;
        public bool Demo;

        public static GuiSettings Load()
        {
            var s = new GuiSettings();
            Config c = Config.Load();
            var raw = c.Raw;
            foreach (var g in c.Selected) s.Selected[g.Key] = g;
            if (raw.Dict("Profiles") is Dictionary<string, object> profiles)
                foreach (var p in profiles) s.Profiles[p.Key] = new ActionSet(p.Value as Dictionary<string, object>);
            s.Actions = c.ActionsFor(null);
            s.CustomFolders = raw.Strings("CustomFolders").ToList();
            s.CustomExes = raw.Strings("CustomExes").ToList();
            s.Language = raw.Str("Language");
            if (raw.ContainsKey("Hotkey")) s.Hotkey = raw.Str("Hotkey");
            if (raw.ContainsKey("Theme")) s.Theme = raw.Str("Theme");
            s.AutoDetect = raw.Bool("AutoDetect");
            s.Notifications = raw.Bool("Notifications", true);
            s.CheckUpdates = raw.Bool("CheckUpdates", true);
            s.SetupDone = raw.Bool("SetupDone");
            s.TrayHdrToggle = raw.Bool("TrayHdrToggle");
            return s;
        }

        public void Save(IEnumerable<Game> library)
        {
            if (Demo) return;
            object Ref(GameRef g) => new Dictionary<string, object> { ["Key"] = g.Key, ["Name"] = g.Name, ["Exes"] = g.Exes };
            var data = new Dictionary<string, object>
            {
                ["Selected"] = Selected.Values.Select(Ref).ToArray(),
                ["Profiles"] = Profiles.ToDictionary(p => p.Key, p => (object)p.Value.Values),
                ["Actions"] = Actions.Values,
                ["CustomFolders"] = CustomFolders.ToArray(),
                ["CustomExes"] = CustomExes.ToArray(),
                ["Language"] = Language,
                ["AutoDetect"] = AutoDetect,
                ["Hotkey"] = Hotkey,
                ["Notifications"] = Notifications,
                ["Theme"] = Theme,
                ["CheckUpdates"] = CheckUpdates,
                ["SetupDone"] = SetupDone,
                ["TrayHdrToggle"] = TrayHdrToggle,
                // all found games, so the service's automatic detection uses the same keys as the list
                ["Library"] = library.Where(g => g.Source != "Missing").Select(g => Ref(new GameRef { Key = g.Key, Name = g.Name, Exes = g.Exes })).ToArray(),
            };
            Json.WriteFile(Paths.Data("settings.json"), data);
        }
    }
}
