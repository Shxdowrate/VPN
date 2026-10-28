using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VPN.Core
{
    /// <summary>
    /// Наборы правил sing-box (.srs): geosite-youtube, geoip-ru и т. п. Часть идёт вместе с программой,
    /// остальные скачиваются по требованию из репозиториев SagerNet и лежат в папке данных.
    /// </summary>
    public static class RuleSets
    {
        private static readonly Regex TagPattern = new Regex(@"^(geosite|geoip)-[a-z0-9][a-z0-9!@._-]{0,80}$", RegexOptions.Compiled);

        public static bool IsValidTag(string tag) => !string.IsNullOrEmpty(tag) && TagPattern.IsMatch(tag);

        /// <summary>geoip-наборы содержат адреса, а в DNS-правилах такие нельзя — их туда не кладём.</summary>
        public static bool IsIpSet(string tag) => tag.StartsWith("geoip-", StringComparison.Ordinal);

        public static string PathFor(string tag)
        {
            string bundled = Path.Combine(Paths.RuleSets, tag + ".srs");

            if (File.Exists(bundled))
            {
                return bundled;
            }

            return Path.Combine(Paths.UserRuleSets, tag + ".srs");
        }

        public static bool Exists(string tag) => IsValidTag(tag) && File.Exists(PathFor(tag));

        private static string UrlFor(string tag) => IsIpSet(tag)
            ? "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/" + tag + ".srs"
            : "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/" + tag + ".srs";

        /// <summary>
        /// Скачать недостающие наборы. Возвращает теги, которые скачать не вышло (нет такого набора или нет сети).
        /// Скачанные больше двух недель назад тихо обновляет.
        /// </summary>
        public static async Task<List<string>> EnsureAsync(IEnumerable<string> tags)
        {
            var failed = new List<string>();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };

            foreach (string tag in tags.Where(IsValidTag).Distinct())
            {
                string path = PathFor(tag);
                bool have = File.Exists(path);
                bool bundled = path.StartsWith(Paths.RuleSets, StringComparison.OrdinalIgnoreCase);

                if (have && (bundled || DateTime.Now - File.GetLastWriteTime(path) < TimeSpan.FromDays(14)))
                {
                    continue;
                }

                try
                {
                    byte[] data = await http.GetByteArrayAsync(UrlFor(tag));

                    if (data.Length < 8)
                    {
                        throw new InvalidDataException("пустой файл");
                    }

                    Directory.CreateDirectory(Paths.UserRuleSets);
                    string target = Path.Combine(Paths.UserRuleSets, tag + ".srs");
                    string tmp = target + ".tmp";
                    await File.WriteAllBytesAsync(tmp, data);
                    File.Move(tmp, target, true);
                    Log.Write("Набор правил скачан: " + tag);
                }
                catch (Exception e)
                {
                    // Старая копия лучше, чем ничего.
                    if (!have)
                    {
                        failed.Add(tag);
                    }

                    Log.Write("Набор правил " + tag + ": " + e.Message);
                }
            }

            return failed;
        }

        /// <summary>Наборы, на которые ссылаются правила профиля.</summary>
        public static IEnumerable<string> Needed(RoutingProfile profile) =>
            profile.Rules.Where(r => r.Kind == RuleKind.RuleSet).Select(r => r.Value);
    }
}
