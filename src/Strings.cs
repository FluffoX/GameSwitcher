using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GameSwitcher
{
    // Translations from app\lang\<code>.json, shared with the GUI. Missing keys fall back to English.
    static class Strings
    {
        static Dictionary<string, string> strings = new Dictionary<string, string>();
        static Dictionary<string, string> fallback = new Dictionary<string, string>();
        static string loaded;

        static string LangFile(string code) => Path.Combine(Paths.AppDir, "lang", code + ".json");

        static Dictionary<string, string> Read(string code)
        {
            var result = new Dictionary<string, string>();
            var json = Json.ReadFile(LangFile(code));
            if (json != null) foreach (var kv in json) result[kv.Key] = kv.Value as string;
            return result;
        }

        // Language from the settings, otherwise the Windows display language, otherwise English
        public static void Load(Dictionary<string, object> settings)
        {
            string code = settings.Str("Language");
            if (code.Length == 0) code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (!File.Exists(LangFile(code))) code = "en";
            if (code == loaded) return;
            fallback = Read("en");
            strings = code == "en" ? fallback : Read(code);
            loaded = code;
        }

        public static string T(string key, params object[] args)
        {
            if (!strings.TryGetValue(key, out string text) || string.IsNullOrEmpty(text))
                if (!fallback.TryGetValue(key, out text) || string.IsNullOrEmpty(text)) text = key;
            return args.Length > 0 ? string.Format(CultureInfo.CurrentCulture, text, args) : text;
        }
    }
}
