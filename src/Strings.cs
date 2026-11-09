using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GameSwitcher
{
    // Translations from app\lang\<code>.json. Missing keys fall back to English, then to the key itself.
    static class Strings
    {
        static Dictionary<string, string> strings = new Dictionary<string, string>();
        static Dictionary<string, string> fallback = new Dictionary<string, string>();

        public static string Code { get; private set; }

        static string LangDir => Path.Combine(Paths.AppDir, "lang");
        static string LangFile(string code) => Path.Combine(LangDir, code + ".json");

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
            LoadCode(code);
        }

        public static void LoadCode(string code)
        {
            if (!File.Exists(LangFile(code))) code = "en";
            if (code == Code) return;
            fallback = Read("en");
            strings = code == "en" ? fallback : Read(code);
            Code = code;
        }

        // All available languages (code and the language's own name)
        public static List<KeyValuePair<string, string>> Languages() =>
            Directory.GetFiles(LangDir, "*.json").OrderBy(f => f)
                .Select(f => new KeyValuePair<string, string>(Path.GetFileNameWithoutExtension(f), Json.ReadFile(f).Str("_name")))
                .ToList();

        // Languages with their own script (e.g. CJK) can name a matching Windows font via "_font"
        public static string FontName => strings.TryGetValue("_font", out string f) && !string.IsNullOrEmpty(f) ? f : "Segoe UI";

        public static string T(string key, params object[] args)
        {
            if (!strings.TryGetValue(key, out string text) || string.IsNullOrEmpty(text))
                if (!fallback.TryGetValue(key, out text) || string.IsNullOrEmpty(text)) text = key;
            return args.Length > 0 ? string.Format(CultureInfo.CurrentCulture, text, args) : text;
        }
    }
}
