using System;
using System.Net;
using System.Threading.Tasks;

namespace GameSwitcher.Gui
{
    // Asks GitHub for the latest release (only api.github.com is contacted)
    static class UpdateChecker
    {
        // Calls done with the tag of the latest release (e.g. "v2.4.0") or null if the check failed
        public static void LatestTag(Action<string> done)
        {
            Task.Run(() =>
            {
                string tag = null;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var web = new WebClient())
                    {
                        web.Headers[HttpRequestHeader.UserAgent] = "GameSwitcher";
                        tag = Json.Parse(web.DownloadString($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest")).Str("tag_name");
                        if (tag.Length == 0) tag = null;
                    }
                }
                catch { }
                done(tag);
            });
        }
    }
}
