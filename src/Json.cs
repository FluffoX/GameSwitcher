using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace GameSwitcher
{
    // JSON via the serializer built into .NET Framework; objects become dictionaries, arrays object[]
    static class Json
    {
        static JavaScriptSerializer NewSerializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        public static Dictionary<string, object> ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return NewSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        public static T ReadFile<T>(string path) where T : class
        {
            try { return File.Exists(path) ? NewSerializer().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch { return null; }
        }

        public static void WriteFile(string path, object value) =>
            File.WriteAllText(path, NewSerializer().Serialize(value), new UTF8Encoding(false));

        // Typed access with defaults
        public static object Get(this IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out object v) ? v : null;

        public static bool Bool(this IDictionary<string, object> d, string key, bool fallback = false) =>
            d.Get(key) is bool b ? b : fallback;

        public static int Int(this IDictionary<string, object> d, string key, int fallback = 0)
        {
            object v = d.Get(key);
            if (v == null || v is bool) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        public static double Double(this IDictionary<string, object> d, string key)
        {
            object v = d.Get(key);
            if (v == null || v is bool) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        public static string Str(this IDictionary<string, object> d, string key) => d.Get(key) as string ?? "";

        public static Dictionary<string, object> Dict(this IDictionary<string, object> d, string key) =>
            d.Get(key) as Dictionary<string, object>;

        // A JSON array (or a single value, as PowerShell sometimes writes one-element arrays) as a list
        public static List<object> List(this IDictionary<string, object> d, string key)
        {
            object v = d.Get(key);
            if (v == null) return new List<object>();
            if (v is string || v is IDictionary) return new List<object> { v };
            if (v is IEnumerable e) return e.Cast<object>().ToList();
            return new List<object> { v };
        }

        public static string[] Strings(this IDictionary<string, object> d, string key) =>
            d.List(key).OfType<string>().Where(s => s.Length > 0).ToArray();
    }
}
