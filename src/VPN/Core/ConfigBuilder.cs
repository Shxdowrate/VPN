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
    /// 0) блокировка (реклама и то, что пользователь пометил «блок»);
    /// 1) правила активного профиля (сайты, IP, наборы geosite/geoip, программы) — в приоритете;
    /// 2) «российские сайты напрямую» (наборы geoip-ru / geosite-category-ru и зоны .ru/.su/.рф);
    /// 3) всё остальное — как выбрано в «Остальной трафик».
    /// </summary>
    public static class ConfigBuilder
    {
        public const string ApiAddress = "127.0.0.1:19090";
        public const string TestUrl = "https://www.gstatic.com/generate_204";

        /// <summary>Логин «зонда» — локального входа, через который программа сама ходит в интернет через VPN (узнать внешний IP).</summary>
        public const string ProbeUser = "vpn";

        /// <param name="probePort">Порт «зонда» на 127.0.0.1 (пароль — apiSecret). 0 — без зонда.</param>
        public static string Build(AppSettings s, string apiSecret, int probePort = 0)
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
            List<string> appsVpn = Values(s, RuleKind.App, Action.Vpn);
            List<string> appsDirect = Values(s, RuleKind.App, Action.Direct);
            List<string> appsBlock = Values(s, RuleKind.App, Action.Block);
            List<string> sitesVpn = Values(s, RuleKind.Site, Action.Vpn);
            List<string> sitesDirect = Values(s, RuleKind.Site, Action.Direct);
            List<string> sitesBlock = Values(s, RuleKind.Site, Action.Block);
            List<string> ipVpn = Values(s, RuleKind.Ip, Action.Vpn);
            List<string> ipDirect = Values(s, RuleKind.Ip, Action.Direct);
            List<string> ipBlock = Values(s, RuleKind.Ip, Action.Block);

            // Наборы (geosite-youtube…) — только те, что есть на диске: без файла движок не запустится.
            List<string> setsVpn = Values(s, RuleKind.RuleSet, Action.Vpn).Where(RuleSets.Exists).ToList();
            List<string> setsDirect = Values(s, RuleKind.RuleSet, Action.Direct).Where(RuleSets.Exists).ToList();
            List<string> setsBlock = Values(s, RuleKind.RuleSet, Action.Block).Where(RuleSets.Exists).ToList();

            JsonArray rules = new JsonArray
            {
                new JsonObject { ["action"] = "sniff" },
                new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
            };

            // Зонд — раньше правила «VPN.exe напрямую»: запросы через него программа делает сама, но они должны идти через VPN.
            if (probePort > 0)
            {
                rules.Add(new JsonObject { ["inbound"] = "probe-in", ["outbound"] = "proxy" });
            }

            foreach (JsonNode rule in new JsonNode[]
            {
                new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
                // Сам движок и наша программа (обновление подписки) — всегда напрямую.
                new JsonObject { ["process_name"] = Arr("sing-box.exe", "VPN.exe"), ["outbound"] = "direct" }
            })
            {
                rules.Add(rule);
            }

            JsonArray ruleSets = new JsonArray();
            var declared = new HashSet<string>();

            void Declare(string tag)
            {
                if (declared.Add(tag))
                {
                    ruleSets.Add(LocalRuleSet(tag));
                }
            }

            foreach (string tag in setsVpn.Concat(setsDirect).Concat(setsBlock))
            {
                Declare(tag);
            }

            // Блокировка — раньше всего остального.
            if (s.BlockAds && RuleSets.Exists("geosite-category-ads-all"))
            {
                Declare("geosite-category-ads-all");
                rules.Add(new JsonObject { ["rule_set"] = "geosite-category-ads-all", ["action"] = "reject" });
            }

            AddRule(rules, "domain_suffix", sitesBlock, null);
            AddRule(rules, "ip_cidr", ipBlock, null);
            AddRule(rules, "rule_set", setsBlock, null);
            AddRule(rules, "process_name", appsBlock, null);

            // Сайты точнее программ: «youtube через VPN» сработает и внутри браузера, пущенного напрямую.
            AddRule(rules, "domain_suffix", sitesDirect, "direct");
            AddRule(rules, "domain_suffix", sitesVpn, "proxy");
            AddRule(rules, "ip_cidr", ipDirect, "direct");
            AddRule(rules, "ip_cidr", ipVpn, "proxy");
            AddRule(rules, "rule_set", setsDirect, "direct");
            AddRule(rules, "rule_set", setsVpn, "proxy");
            AddRule(rules, "process_name", appsDirect, "direct");
            AddRule(rules, "process_name", appsVpn, "proxy");

            JsonArray russiaSets = new JsonArray();

            if (s.RussiaDirect)
            {
                foreach (string tag in new[] { "geoip-ru", "geosite-category-ru" })
                {
                    if (RuleSets.Exists(tag))
                    {
                        Declare(tag);
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
            AddDnsRule(dnsRules, "domain_suffix", sitesDirect, "local");
            AddDnsRule(dnsRules, "domain_suffix", sitesVpn, "remote");
            // В DNS-правилах можно только наборы доменов (geosite), не адресов.
            AddDnsRule(dnsRules, "rule_set", setsDirect.Where(t => !RuleSets.IsIpSet(t)).ToList(), "local");
            AddDnsRule(dnsRules, "rule_set", setsVpn.Where(t => !RuleSets.IsIpSet(t)).ToList(), "remote");
            AddDnsRule(dnsRules, "process_name", appsDirect, "local");
            AddDnsRule(dnsRules, "process_name", appsVpn, "remote");

            if (s.RussiaDirect)
            {
                dnsRules.Add(new JsonObject { ["domain_suffix"] = Arr("ru", "su", "xn--p1ai", "xn--d1acj3b"), ["server"] = "local" });

                if (RuleSets.Exists("geosite-category-ru"))
                {
                    dnsRules.Add(new JsonObject { ["rule_set"] = "geosite-category-ru", ["server"] = "local" });
                }
            }

            JsonArray inbounds = new JsonArray
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
            };

            if (probePort > 0)
            {
                inbounds.Add(new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "probe-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = probePort,
                    ["users"] = new JsonArray { new JsonObject { ["username"] = ProbeUser, ["password"] = apiSecret } }
                });
            }

            JsonObject config = new JsonObject
            {
                ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
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
                ["inbounds"] = inbounds,
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

            return config.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
            });
        }

        private enum Action
        {
            Vpn,
            Direct,
            Block
        }

        private static Action ActionOf(RouteRule r) => r.Block ? Action.Block : r.ViaVpn ? Action.Vpn : Action.Direct;

        private static List<string> Values(AppSettings s, RuleKind kind, Action action) =>
            s.Rules.Where(r => r.Kind == kind && ActionOf(r) == action && !string.IsNullOrWhiteSpace(r.Value))
                .Select(r => kind switch
                {
                    RuleKind.Site => NormalizeDomain(r.Value),
                    RuleKind.Ip => LinkImport.NormalizeIp(r.Value) ?? "",
                    RuleKind.RuleSet => r.Value.Trim().ToLowerInvariant(),
                    _ => r.Value.Trim()
                })
                .Where(v => v.Length > 0)
                .Distinct()
                .ToList();

        /// <summary>Правило маршрута; outbound null — заблокировать.</summary>
        private static void AddRule(JsonArray rules, string field, List<string> values, string outbound)
        {
            if (values.Count == 0)
            {
                return;
            }

            JsonObject rule = new JsonObject { [field] = Arr(values) };

            if (outbound == null)
            {
                rule["action"] = "reject";
            }
            else
            {
                rule["outbound"] = outbound;
            }

            rules.Add(rule);
        }

        private static void AddDnsRule(JsonArray rules, string field, List<string> values, string server)
        {
            if (values.Count > 0)
            {
                rules.Add(new JsonObject { [field] = Arr(values), ["server"] = server });
            }
        }

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

            d = d.Trim('.');

            // «пример.рф» → «xn--e1afmkfd.xn--p1ai»: движок понимает только такую запись.
            if (d.Any(c => c > 127))
            {
                try
                {
                    d = new System.Globalization.IdnMapping().GetAscii(d);
                }
                catch (System.ArgumentException)
                {
                    // не домен — оставим как есть, проверка отсеет
                }
            }

            return d;
        }

        private static JsonArray Arr(params string[] values) => new JsonArray(values.Select(v => (JsonNode)v).ToArray());

        private static JsonArray Arr(List<string> values) => Arr(values.ToArray());

        private static JsonObject LocalRuleSet(string tag) => new JsonObject
        {
            ["type"] = "local",
            ["tag"] = tag,
            ["format"] = "binary",
            ["path"] = RuleSets.PathFor(tag)
        };
    }
}
