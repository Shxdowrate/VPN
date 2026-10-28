using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace VPN.Core
{
    public enum ImportKind
    {
        /// <summary>Ничего понятного.</summary>
        None,

        /// <summary>Ссылка на подписку (https://…, happ://add/…, sing-box://import-remote-profile…).</summary>
        Subscription,

        /// <summary>Один или несколько серверов (vless://…, список ссылок, base64, JSON sing-box).</summary>
        Servers,

        /// <summary>Профиль маршрутизации (happ://routing/…, JSON с DirectSites/ProxySites).</summary>
        Routing,

        /// <summary>Зашифрованная ссылка Happ — расшифровать её может только сам Happ.</summary>
        Encrypted
    }

    public sealed class ImportResult
    {
        public ImportKind Kind { get; set; }
        public string Url { get; set; } = "";
        public SubscriptionResult Servers { get; set; }
        public RoutingProfile Profile { get; set; }

        /// <summary>Сколько записей профиля маршрутизации пропущено (regexp:, keyword: и т. п.).</summary>
        public int Skipped { get; set; }
    }

    /// <summary>
    /// Разбирает то, что вставили или передали ссылкой: подписку, сервер, профиль маршрутизации.
    /// Понимает ссылки-обёртки других клиентов: happ://add/, happ://routing/, sing-box://, hiddify://, v2raytun://, streisand://, clash://.
    /// </summary>
    public static class LinkImport
    {
        private static readonly string[] ServerSchemes = { "vless://", "vmess://", "trojan://", "ss://", "hysteria2://", "hy2://", "tuic://", "socks://", "socks5://" };

        /// <summary>Быстрая проверка без разбора: похоже ли на то, что стоит предложить добавить (для буфера обмена).</summary>
        public static bool LooksImportable(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200_000)
            {
                return false;
            }

            string t = text.TrimStart();

            if (ServerSchemes.Any(s => t.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            foreach (string prefix in new[] { "happ://", "shxvpn://", "sing-box://", "hiddify://", "v2raytun://", "streisand://", "clash://" })
            {
                if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Обычные ссылки в буфере бывают постоянно — предлагаем только то, что похоже на подписку.
            if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !t.Contains(' ') && !t.Contains('\n'))
            {
                return Regex.IsMatch(t, @"/(sub|subscription|api/v1/client|link|s)/|[?&](token|sub|flag)=|/sub\b", RegexOptions.IgnoreCase);
            }

            return false;
        }

        public static ImportResult Classify(string text)
        {
            var result = new ImportResult();

            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            string t = text.Trim().Trim('"', '\'');
            string lower = t.ToLowerInvariant();

            // --- обёртки других клиентов ---
            if (lower.StartsWith("happ://crypt"))
            {
                result.Kind = ImportKind.Encrypted;
                return result;
            }

            if (lower.StartsWith("happ://routing/"))
            {
                // happ://routing/onadd/<base64>  или  happ://routing/add/<base64>
                string payload = t.Substring(t.IndexOf('/', "happ://routing/".Length) + 1);
                string json = Subscription.TryBase64(Uri.UnescapeDataString(payload)) ?? Uri.UnescapeDataString(payload);
                return RoutingFromJson(json) ?? result;
            }

            foreach (string prefix in new[] { "happ://add/", "shxvpn://add/", "hiddify://import/", "v2raytun://import/", "streisand://import/", "v2rayng://install-sub/" })
            {
                if (lower.StartsWith(prefix))
                {
                    return Sub(StripName(Uri.UnescapeDataString(t.Substring(prefix.Length))));
                }
            }

            if (lower.StartsWith("sing-box://import-remote-profile") || lower.StartsWith("clash://install-config") || lower.StartsWith("shxvpn://import"))
            {
                int q = t.IndexOf('?');
                string url = q >= 0 ? HttpUtility.ParseQueryString(t.Substring(q + 1))["url"] ?? "" : "";
                return url.Length > 0 ? Sub(StripName(url)) : result;
            }

            // --- обычная ссылка: подписка ---
            if ((lower.StartsWith("https://") || lower.StartsWith("http://")) && !t.Contains('\n') && !t.Contains(' '))
            {
                return Sub(t);
            }

            // --- JSON: профиль маршрутизации Happ или конфиг sing-box ---
            if (t.StartsWith("{"))
            {
                ImportResult routing = RoutingFromJson(t);

                if (routing != null)
                {
                    return routing;
                }
            }

            // --- серверы: ссылки, base64-список, JSON sing-box ---
            var servers = new SubscriptionResult();

            try
            {
                Subscription.Parse(t, servers);
            }
            catch (Exception e)
            {
                Log.Write("Импорт: " + e.Message);
            }

            if (servers.Servers.Count > 0)
            {
                result.Kind = ImportKind.Servers;
                result.Servers = servers;
            }

            return result;
        }

        private static ImportResult Sub(string url)
        {
            url = url.Trim();

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                return new ImportResult();
            }

            return new ImportResult { Kind = ImportKind.Subscription, Url = url };
        }

        /// <summary>«https://x/sub#Имя» — хвост с именем подписке не нужен.</summary>
        private static string StripName(string url)
        {
            int hash = url.IndexOf('#');
            return hash > 0 ? url.Substring(0, hash) : url;
        }

        // ================================================================== профиль маршрутизации

        /// <summary>
        /// Профиль в формате Happ: {"Name":…, "GlobalProxy":"true", "DirectSites":[…], "ProxySites":[…], "BlockSites":[…],
        /// "DirectIp":[…], "ProxyIp":[…], "BlockIp":[…]}. Записи: geosite:xxx, geoip:xxx, domain:x.com, full:x.com, x.com, 1.2.3.0/24.
        /// </summary>
        public static ImportResult RoutingFromJson(string json)
        {
            JsonObject root;

            try
            {
                root = JsonNode.Parse(json) as JsonObject;
            }
            catch
            {
                return null;
            }

            if (root == null)
            {
                return null;
            }

            // Ключи без учёта регистра.
            var map = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in root)
            {
                map[pair.Key] = pair.Value;
            }

            string[] listKeys = { "DirectSites", "ProxySites", "BlockSites", "DirectIp", "ProxyIp", "BlockIp" };

            if (!listKeys.Any(map.ContainsKey))
            {
                return null;
            }

            var profile = new RoutingProfile
            {
                Name = Text(map, "Name") is string n && n.Length > 0 ? n : "Импорт",
                DefaultViaVpn = !map.ContainsKey("GlobalProxy") || IsTrue(map["GlobalProxy"]),
                RussiaDirect = false,
                BlockAds = false
            };

            var result = new ImportResult { Kind = ImportKind.Routing, Profile = profile };

            void AddAll(string key, bool viaVpn, bool block)
            {
                if (!map.TryGetValue(key, out JsonNode node) || node is not JsonArray arr)
                {
                    return;
                }

                foreach (JsonNode item in arr)
                {
                    string entry = item?.ToString().Trim() ?? "";

                    if (entry.Length == 0)
                    {
                        continue;
                    }

                    RouteRule rule = RuleFromEntry(entry, profile, viaVpn, block);

                    if (rule == null)
                    {
                        if (!IsHandledFlag(entry))
                        {
                            result.Skipped++;
                        }

                        continue;
                    }

                    if (!profile.Rules.Any(r => r.Kind == rule.Kind && r.Value.Equals(rule.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        profile.Rules.Add(rule);
                    }
                }
            }

            // Блокировка важнее, затем «напрямую», затем «через VPN» — дубли берут первое.
            AddAll("BlockSites", false, true);
            AddAll("BlockIp", false, true);
            AddAll("DirectSites", false, false);
            AddAll("DirectIp", false, false);
            AddAll("ProxySites", true, false);
            AddAll("ProxyIp", true, false);
            return result;
        }

        /// <summary>Одна запись профиля → правило. Российские и рекламные наборы включают готовые переключатели.</summary>
        public static RouteRule RuleFromEntry(string entry, RoutingProfile profile, bool viaVpn, bool block)
        {
            string e = entry.Trim();
            string lower = e.ToLowerInvariant();

            if (lower.StartsWith("geosite:") || lower.StartsWith("geoip:"))
            {
                bool site = lower.StartsWith("geosite:");
                string name = lower.Substring(lower.IndexOf(':') + 1).Trim();

                if (profile != null && !viaVpn && !block && (name == "ru" || name == "category-ru" || name == "category-gov-ru"))
                {
                    profile.RussiaDirect = true;
                    return null;
                }

                if (profile != null && block && site && (name == "category-ads-all" || name == "category-ads"))
                {
                    profile.BlockAds = true;
                    return null;
                }

                string tag = (site ? "geosite-" : "geoip-") + name;
                return RuleSets.IsValidTag(tag) ? new RouteRule { Kind = RuleKind.RuleSet, Value = tag, ViaVpn = viaVpn, Block = block } : null;
            }

            if (lower.StartsWith("regexp:") || lower.StartsWith("keyword:") || lower.StartsWith("ext:"))
            {
                return null;
            }

            if (lower.StartsWith("domain:") || lower.StartsWith("full:"))
            {
                e = e.Substring(e.IndexOf(':') + 1);
            }

            if (NormalizeIp(e) is string ip)
            {
                return new RouteRule { Kind = RuleKind.Ip, Value = ip, ViaVpn = viaVpn, Block = block };
            }

            string domain = ConfigBuilder.NormalizeDomain(e);
            return IsDomain(domain) ? new RouteRule { Kind = RuleKind.Site, Value = domain, ViaVpn = viaVpn, Block = block } : null;
        }

        private static bool IsHandledFlag(string entry)
        {
            string l = entry.ToLowerInvariant();
            return l is "geosite:ru" or "geosite:category-ru" or "geosite:category-gov-ru" or "geoip:ru" or "geosite:category-ads-all" or "geosite:category-ads";
        }

        // ================================================================== быстрое добавление правила

        /// <summary>
        /// Что ввели в строку «Добавить»: программа (.exe), IP/подсеть, набор (geosite:youtube), иначе сайт.
        /// </summary>
        public static RouteRule GuessRule(string input)
        {
            string v = input.Trim().Trim('"');

            if (v.Length == 0)
            {
                return null;
            }

            string lower = v.ToLowerInvariant();

            if (lower.EndsWith(".exe"))
            {
                string name = System.IO.Path.GetFileName(v.Replace('/', '\\').Split('\\').Last());
                return new RouteRule { Kind = RuleKind.App, Value = name, Path = v.Contains('\\') ? v : "" };
            }

            if (lower.StartsWith("geosite:") || lower.StartsWith("geoip:"))
            {
                return RuleFromEntry(v, null, true, false);
            }

            if (lower.StartsWith("geosite-") || lower.StartsWith("geoip-"))
            {
                return RuleSets.IsValidTag(lower) ? new RouteRule { Kind = RuleKind.RuleSet, Value = lower } : null;
            }

            if (NormalizeIp(v) is string ip)
            {
                return new RouteRule { Kind = RuleKind.Ip, Value = ip };
            }

            string domain = ConfigBuilder.NormalizeDomain(v);
            return IsDomain(domain) ? new RouteRule { Kind = RuleKind.Site, Value = domain } : null;
        }

        /// <summary>«1.2.3.4» → «1.2.3.4/32», «10.0.0.0/8» как есть. Не IP — null.</summary>
        public static string NormalizeIp(string s)
        {
            s = s.Trim();
            string addr = s;
            int prefix = -1;
            int slash = s.IndexOf('/');

            if (slash > 0)
            {
                addr = s.Substring(0, slash);

                if (!int.TryParse(s.Substring(slash + 1), out prefix))
                {
                    return null;
                }
            }

            if (!IPAddress.TryParse(addr, out IPAddress ip))
            {
                return null;
            }

            // «1.2» тоже разбирается как IP — такое не принимаем.
            bool v4 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

            if (v4 && addr.Count(c => c == '.') != 3)
            {
                return null;
            }

            int max = v4 ? 32 : 128;

            if (prefix < 0)
            {
                prefix = max;
            }

            return prefix >= 0 && prefix <= max ? ip + "/" + prefix : null;
        }

        public static bool IsDomain(string d) =>
            d.Length > 0 && d.Length <= 253 && Regex.IsMatch(d, @"^([a-z0-9Ѐ-ӿ]([a-z0-9Ѐ-ӿ-]{0,61}[a-z0-9Ѐ-ӿ])?\.)*[a-z0-9Ѐ-ӿ-]{2,63}$");

        private static string Text(Dictionary<string, JsonNode> map, string key) =>
            map.TryGetValue(key, out JsonNode n) && n != null ? n.ToString() : "";

        private static bool IsTrue(JsonNode n)
        {
            try
            {
                return n is JsonValue v && (v.TryGetValue(out bool b) ? b : v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
}
