using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPN.Core
{
    /// <summary>
    /// Собирает конфиг sing-box 1.14. Весь трафик идёт через виртуальный адаптер (TUN),
    /// а правила решают, что пустить через VPN, а что напрямую:
    /// 1) правила пользователя (программы и сайты) — в приоритете;
    /// 2) «российские сайты напрямую» (наборы geoip-ru / geosite-category-ru и зоны .ru/.su/.рф);
    /// 3) всё остальное — как выбрано в «Остальной трафик».
    /// </summary>
    public static class ConfigBuilder
    {
        public const string ApiAddress = "127.0.0.1:19090";
        public const string TestUrl = "https://www.gstatic.com/generate_204";

        public static string Build(AppSettings s, string apiSecret)
        {
            List<ServerProfile> servers = s.Servers;
            JsonArray outbounds = new JsonArray();
            JsonArray serverTags = new JsonArray(servers.Select(x => (JsonNode)x.Tag).ToArray());

            bool hasSelected = servers.Any(x => x.Tag == s.SelectedServer);

            JsonArray selectorItems = new JsonArray { "auto" };

            foreach (ServerProfile server in servers)
            {
                selectorItems.Add(server.Tag);
            }

            outbounds.Add(new JsonObject
            {
                ["type"] = "selector",
                ["tag"] = "proxy",
                ["outbounds"] = selectorItems,
                ["default"] = hasSelected ? s.SelectedServer : "auto",
                ["interrupt_exist_connections"] = true
            });

            outbounds.Add(new JsonObject
            {
                ["type"] = "urltest",
                ["tag"] = "auto",
                ["outbounds"] = serverTags,
                ["url"] = TestUrl,
                ["interval"] = "3m",
                ["tolerance"] = 80
            });

            foreach (ServerProfile server in servers)
            {
                JsonObject o = JsonNode.Parse(server.Outbound).AsObject();
                o["tag"] = server.Tag;
                outbounds.Add(o);
            }

            outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

            // --- Правила ---
            List<string> appsVpn = Values(s, RuleKind.App, true);
            List<string> appsDirect = Values(s, RuleKind.App, false);
            List<string> sitesVpn = Values(s, RuleKind.Site, true);
            List<string> sitesDirect = Values(s, RuleKind.Site, false);

            JsonArray rules = new JsonArray
            {
                new JsonObject { ["action"] = "sniff" },
                new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
                // Сам движок и наша программа (обновление подписки) — всегда напрямую.
                new JsonObject { ["process_name"] = Arr("sing-box.exe", "VPN.exe"), ["outbound"] = "direct" }
            };

            JsonArray ruleSets = new JsonArray();

            if (s.BlockAds && RuleSetExists("geosite-category-ads-all"))
            {
                ruleSets.Add(LocalRuleSet("geosite-category-ads-all"));
                rules.Add(new JsonObject { ["rule_set"] = "geosite-category-ads-all", ["action"] = "reject" });
            }

            // Сайты точнее программ: «youtube через VPN» сработает и внутри браузера, пущенного напрямую.
            if (sitesDirect.Count > 0)
            {
                rules.Add(new JsonObject { ["domain_suffix"] = Arr(sitesDirect), ["outbound"] = "direct" });
            }

            if (sitesVpn.Count > 0)
            {
                rules.Add(new JsonObject { ["domain_suffix"] = Arr(sitesVpn), ["outbound"] = "proxy" });
            }

            if (appsDirect.Count > 0)
            {
                rules.Add(new JsonObject { ["process_name"] = Arr(appsDirect), ["outbound"] = "direct" });
            }

            if (appsVpn.Count > 0)
            {
                rules.Add(new JsonObject { ["process_name"] = Arr(appsVpn), ["outbound"] = "proxy" });
            }

            JsonArray russiaSets = new JsonArray();

            if (s.RussiaDirect)
            {
                foreach (string tag in new[] { "geoip-ru", "geosite-category-ru" })
                {
                    if (RuleSetExists(tag))
                    {
                        ruleSets.Add(LocalRuleSet(tag));
                        russiaSets.Add(tag);
                    }
                }

                rules.Add(new JsonObject { ["domain_suffix"] = Arr("ru", "su", "xn--p1ai", "xn--d1acj3b"), ["outbound"] = "direct" });

                if (russiaSets.Count > 0)
                {
                    rules.Add(new JsonObject { ["rule_set"] = russiaSets.DeepClone(), ["outbound"] = "direct" });
                }
            }

            // --- DNS: адреса сайтов, идущих напрямую, узнаём у российского DNS, остальные — через VPN ---
            JsonArray dnsRules = new JsonArray();

            if (sitesDirect.Count > 0)
            {
                dnsRules.Add(new JsonObject { ["domain_suffix"] = Arr(sitesDirect), ["server"] = "local" });
            }

            if (sitesVpn.Count > 0)
            {
                dnsRules.Add(new JsonObject { ["domain_suffix"] = Arr(sitesVpn), ["server"] = "remote" });
            }

            if (appsDirect.Count > 0)
            {
                dnsRules.Add(new JsonObject { ["process_name"] = Arr(appsDirect), ["server"] = "local" });
            }

            if (appsVpn.Count > 0)
            {
                dnsRules.Add(new JsonObject { ["process_name"] = Arr(appsVpn), ["server"] = "remote" });
            }

            if (s.RussiaDirect)
            {
                dnsRules.Add(new JsonObject { ["domain_suffix"] = Arr("ru", "su", "xn--p1ai", "xn--d1acj3b"), ["server"] = "local" });

                if (RuleSetExists("geosite-category-ru"))
                {
                    dnsRules.Add(new JsonObject { ["rule_set"] = "geosite-category-ru", ["server"] = "local" });
                }
            }

            JsonObject config = new JsonObject
            {
                ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
                ["dns"] = new JsonObject
                {
                    ["servers"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = "1.1.1.1", ["detour"] = "proxy" },
                        new JsonObject { ["type"] = "https", ["tag"] = "local", ["server"] = "77.88.8.8" }
                    },
                    ["rules"] = dnsRules,
                    ["final"] = s.DefaultViaVpn ? "remote" : "local",
                    ["strategy"] = "ipv4_only"
                },
                ["inbounds"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tun",
                        ["tag"] = "tun-in",
                        ["interface_name"] = "ShxdowrateVPN",
                        ["address"] = Arr("172.19.0.1/30"),
                        ["mtu"] = 9000,
                        ["auto_route"] = true,
                        ["strict_route"] = true,
                        ["stack"] = "mixed"
                    }
                },
                ["outbounds"] = outbounds,
                ["route"] = new JsonObject
                {
                    ["rules"] = rules,
                    ["rule_set"] = ruleSets,
                    ["final"] = s.DefaultViaVpn ? "proxy" : "direct",
                    ["auto_detect_interface"] = true,
                    ["find_process"] = true,
                    ["default_domain_resolver"] = "local"
                },
                ["experimental"] = new JsonObject
                {
                    ["clash_api"] = new JsonObject
                    {
                        ["external_controller"] = ApiAddress,
                        ["secret"] = apiSecret
                    },
                    ["cache_file"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["path"] = Paths.Cache
                    }
                }
            };

            return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        private static List<string> Values(AppSettings s, RuleKind kind, bool viaVpn) =>
            s.Rules.Where(r => r.Kind == kind && r.ViaVpn == viaVpn && !string.IsNullOrWhiteSpace(r.Value))
                .Select(r => kind == RuleKind.Site ? NormalizeDomain(r.Value) : r.Value.Trim())
                .Distinct()
                .ToList();

        /// <summary>«https://www.YouTube.com/watch» → «youtube.com».</summary>
        public static string NormalizeDomain(string input)
        {
            string d = input.Trim().ToLowerInvariant();
            int scheme = d.IndexOf("://", System.StringComparison.Ordinal);

            if (scheme >= 0)
            {
                d = d.Substring(scheme + 3);
            }

            int slash = d.IndexOfAny(new[] { '/', '?', '#', ':' });

            if (slash >= 0)
            {
                d = d.Substring(0, slash);
            }

            if (d.StartsWith("www."))
            {
                d = d.Substring(4);
            }

            return d.Trim('.');
        }

        private static JsonArray Arr(params string[] values) => new JsonArray(values.Select(v => (JsonNode)v).ToArray());

        private static JsonArray Arr(List<string> values) => Arr(values.ToArray());

        private static string RuleSetPath(string tag) => Path.Combine(Paths.RuleSets, tag + ".srs");

        private static bool RuleSetExists(string tag) => File.Exists(RuleSetPath(tag));

        private static JsonObject LocalRuleSet(string tag) => new JsonObject
        {
            ["type"] = "local",
            ["tag"] = tag,
            ["format"] = "binary",
            ["path"] = RuleSetPath(tag)
        };
    }
}
