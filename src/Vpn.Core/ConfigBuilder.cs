using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace NeonVpn.Core
{
    /// <summary>Каким движком ходит конкретный сервер.</summary>
    public enum Engine
    {
        SingBox,
        Xray
    }

    public sealed class PlanNode
    {
        public string Tag { get; set; }
        public ServerNode Server { get; set; }
        public Engine Engine { get; set; }

        /// <summary>Порт socks-входа Xray для этого сервера (Engine = Xray).</summary>
        public int XrayPort { get; set; }
    }

    /// <summary>Готовый набор конфигов для запуска.</summary>
    public sealed class Plan
    {
        public List<PlanNode> Nodes { get; } = new List<PlanNode>();
        public List<(ServerNode server, string why)> Skipped { get; } = new List<(ServerNode, string)>();
        public string SingBoxJson { get; set; }

        /// <summary>null — Xray не нужен (все серверы через sing-box).</summary>
        public string XrayJson { get; set; }

        public int ApiPort { get; set; }
        public string Secret { get; set; }
        public string InitialTag { get; set; }
        public bool ProbeOnly { get; set; }

        public PlanNode ByKey(string key) => Nodes.FirstOrDefault(n => n.Server.Key == key);
        public PlanNode ByTag(string tag) => Nodes.FirstOrDefault(n => n.Tag == tag);
    }

    /// <summary>
    /// Собирает конфиги. Схема одна для обоих ядер:
    ///
    ///   программы → [TUN и/или системный прокси] → sing-box: DNS, правила туннелей, статистика
    ///        ├─ напрямую
    ///        └─ «proxy» (переключатель серверов) → сервер
    ///                 ├─ ядро sing-box: сразу к серверу
    ///                 └─ ядро Xray: socks 127.0.0.1:порт → Xray → сервер
    ///
    /// Так раздельное туннелирование, переключение без переподключения и счётчики одинаковы для обоих ядер,
    /// а с сервером разговаривает выбранное ядро. Что одно ядро не умеет, берёт другое (xhttp → Xray, Hysteria2/TUIC → sing-box).
    /// </summary>
    public static class ConfigBuilder
    {
        public const string Proxy = "proxy";
        public const string Direct = "direct";
        public const string TunName = "NeonVPN";

        public sealed class Options
        {
            public bool ProbeOnly;
            public int ApiPort = 19190;
            public string Secret = Guid.NewGuid().ToString("N");
            public int XrayPortBase = 21100;
            public string XrayUser = "nv";
            public string XrayPass = Guid.NewGuid().ToString("N").Substring(0, 16);

            /// <summary>Имена наших процессов: их трафик всегда напрямую, иначе TUN поймает сам себя.</summary>
            public string[] SelfProcesses = { "VPN.exe" };

            public bool XrayFragment;
            public bool HaveXray = true;
            public bool HaveSingBox = true;
        }

        public static Plan Build(Settings s, IEnumerable<ServerNode> servers, string initialKey, Options opt)
        {
            Plan plan = new Plan { ApiPort = opt.ApiPort, Secret = opt.Secret, ProbeOnly = opt.ProbeOnly };
            int i = 0;
            int xrayPort = opt.XrayPortBase;

            foreach (ServerNode server in servers)
            {
                Engine? engine = PickEngine(s.Core, server, opt, out string why);

                if (engine == null)
                {
                    plan.Skipped.Add((server, why));
                    continue;
                }

                PlanNode node = new PlanNode { Tag = "n" + i++, Server = server, Engine = engine.Value };

                if (engine == Engine.Xray)
                {
                    node.XrayPort = xrayPort++;
                }

                plan.Nodes.Add(node);
            }

            plan.InitialTag = plan.ByKey(initialKey)?.Tag ?? plan.Nodes.FirstOrDefault()?.Tag;
            plan.SingBoxJson = Serialize(SingBox(s, plan, opt));
            plan.XrayJson = plan.Nodes.Any(n => n.Engine == Engine.Xray) ? Serialize(Xray(plan, opt)) : null;
            return plan;
        }

        /// <summary>Выбранное ядро, если оно умеет сервер; иначе второе; иначе — пропуск.</summary>
        public static Engine? PickEngine(CoreKind preferred, ServerNode n, Options opt, out string why)
        {
            bool sb = SingBoxOutbound.Supports(n, out string sbWhy) && opt.HaveSingBox;
            bool xr = XrayOutbound.Supports(n) && opt.HaveXray;
            why = "";

            // Hysteria2/TUIC в Xray нет (или он их только учится делать) — всегда sing-box.
            if (preferred == CoreKind.Xray && xr) return Engine.Xray;
            if (sb) return Engine.SingBox;
            if (xr) return Engine.Xray;

            why = sbWhy.Length > 0 ? sbWhy + (opt.HaveXray ? "" : ", а Xray нет в папке core") : "протокол " + n.Protocol + " не поддерживается";
            return null;
        }

        // ================================================================== sing-box

        private static JsonObject SingBox(Settings s, Plan plan, Options opt)
        {
            TunnelSettings t = s.Tunnel;
            JsonArray outbounds = new JsonArray();
            JsonArray tags = new JsonArray(plan.Nodes.Select(n => (JsonNode)n.Tag).ToArray());

            if (plan.Nodes.Count > 0)
            {
                outbounds.Add(new JsonObject
                {
                    ["type"] = "selector",
                    ["tag"] = Proxy,
                    ["outbounds"] = tags,
                    ["default"] = plan.InitialTag,
                    ["interrupt_exist_connections"] = true
                });
            }
            else
            {
                // Серверов нет — «proxy» ведёт в никуда, чтобы ничего не утекло напрямую по ошибке.
                outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = Proxy });
            }

            foreach (PlanNode n in plan.Nodes)
            {
                outbounds.Add(n.Engine == Engine.SingBox
                    ? SingBoxOutbound.Build(n.Server, n.Tag)
                    : new JsonObject
                    {
                        ["type"] = "socks",
                        ["tag"] = n.Tag,
                        ["server"] = "127.0.0.1",
                        ["server_port"] = n.XrayPort,
                        ["version"] = "5",
                        ["username"] = opt.XrayUser,
                        ["password"] = opt.XrayPass
                    });
            }

            outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = Direct });

            JsonObject config = new JsonObject
            {
                ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
                ["outbounds"] = outbounds,
                ["experimental"] = new JsonObject
                {
                    ["clash_api"] = new JsonObject
                    {
                        ["external_controller"] = "127.0.0.1:" + opt.ApiPort,
                        ["secret"] = opt.Secret
                    }
                }
            };

            if (opt.ProbeOnly)
            {
                // Только пинговать серверы: без входов и правил.
                config["dns"] = new JsonObject
                {
                    ["servers"] = new JsonArray { new JsonObject { ["type"] = "local", ["tag"] = "local" } }
                };
                config["route"] = new JsonObject { ["final"] = Direct, ["default_domain_resolver"] = "local" };
                return config;
            }

            // --- входы ---
            JsonArray inbounds = new JsonArray();

            if (s.UseTun)
            {
                inbounds.Add(new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = TunName,
                    ["address"] = Arr("172.19.0.1/30"),
                    ["mtu"] = 9000,
                    ["auto_route"] = true,
                    ["strict_route"] = true,
                    ["stack"] = "mixed"
                });
            }

            if (s.UseSystemProxy || !s.UseTun)
            {
                inbounds.Add(new JsonObject
                {
                    ["type"] = "mixed",
                    ["tag"] = "mixed-in",
                    ["listen"] = s.AllowLan ? "0.0.0.0" : "127.0.0.1",
                    ["listen_port"] = s.ProxyPort
                });
            }

            config["inbounds"] = inbounds;

            // --- правила ---
            JsonArray rules = new JsonArray
            {
                new JsonObject { ["action"] = "sniff" },
                new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
            };

            if (t.LanDirect)
            {
                rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = Direct });
            }

            // Сами ядра и наша программа — всегда напрямую (обновление подписки, пинги «напрямую»).
            rules.Add(new JsonObject
            {
                ["process_name"] = Arr(opt.SelfProcesses.Concat(new[] { "sing-box.exe", "xray.exe" }).Distinct().ToArray()),
                ["outbound"] = Direct
            });

            JsonArray ruleSets = new JsonArray();

            if (t.BlockAds && RuleSetExists("geosite-category-ads-all"))
            {
                ruleSets.Add(LocalRuleSet("geosite-category-ads-all"));
                rules.Add(new JsonObject { ["rule_set"] = "geosite-category-ads-all", ["action"] = "reject" });
            }

            List<RouteRule> user = t.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Value)).ToList();

            // Сайты: длинные раньше коротких — «mail.google.com напрямую» важнее «google.com через VPN».
            foreach (var group in user.Where(r => r.Kind == RuleKind.Domain)
                         .Select(r => (domain: NormalizeDomain(r.Value), r.Action))
                         .Where(r => r.domain.Length > 0)
                         .GroupBy(r => r.domain).Select(g => g.Last())
                         .OrderByDescending(r => r.domain.Count(c => c == '.')).ThenByDescending(r => r.domain.Length)
                         .GroupBy(r => r.domain.Count(c => c == '.')))
            {
                foreach (var byAction in group.GroupBy(r => r.Action))
                {
                    rules.Add(Rule(new JsonObject { ["domain_suffix"] = Arr(byAction.Select(r => r.domain).ToArray()) }, byAction.Key));
                }
            }

            foreach (var byAction in user.Where(r => r.Kind == RuleKind.Ip).GroupBy(r => r.Action))
            {
                rules.Add(Rule(new JsonObject { ["ip_cidr"] = Arr(byAction.Select(r => NormalizeCidr(r.Value)).Distinct().ToArray()) }, byAction.Key));
            }

            // Программы: Block, потом «напрямую», потом VPN.
            foreach (RouteAction action in new[] { RouteAction.Block, RouteAction.Direct, RouteAction.Vpn })
            {
                List<string> apps = user.Where(r => r.Kind == RuleKind.App && r.Action == action).Select(r => r.Value.Trim()).Distinct().ToList();
                List<string> names = apps.Where(a => !a.Contains('\\') && !a.Contains('/')).ToList();
                List<string> fullPaths = apps.Except(names).ToList();

                if (names.Count > 0) rules.Add(Rule(new JsonObject { ["process_name"] = Arr(names.ToArray()) }, action));
                if (fullPaths.Count > 0) rules.Add(Rule(new JsonObject { ["process_path"] = Arr(fullPaths.ToArray()) }, action));
            }

            if (t.RussiaDirect)
            {
                rules.Add(new JsonObject { ["domain_suffix"] = Arr(RuZones), ["outbound"] = Direct });
                JsonArray ru = new JsonArray();

                foreach (string tag in new[] { "geosite-category-ru", "geoip-ru" })
                {
                    if (RuleSetExists(tag))
                    {
                        ruleSets.Add(LocalRuleSet(tag));
                        ru.Add(tag);
                    }
                }

                if (ru.Count > 0)
                {
                    rules.Add(new JsonObject { ["rule_set"] = ru, ["outbound"] = Direct });
                }
            }

            bool defaultVpn = t.Default != RouteAction.Direct;

            config["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["rule_set"] = ruleSets,
                ["final"] = t.Default == RouteAction.Block ? Proxy : defaultVpn ? Proxy : Direct,
                ["auto_detect_interface"] = true,
                ["find_process"] = true,
                ["default_domain_resolver"] = "local"
            };

            // --- DNS: имена сайтов, идущих напрямую, — у обычного DNS, остальные — через VPN ---
            JsonArray dnsRules = new JsonArray();
            List<string> directDomains = user.Where(r => r.Kind == RuleKind.Domain && r.Action == RouteAction.Direct).Select(r => NormalizeDomain(r.Value)).Where(d => d.Length > 0).Distinct().ToList();
            List<string> vpnDomains = user.Where(r => r.Kind == RuleKind.Domain && r.Action == RouteAction.Vpn).Select(r => NormalizeDomain(r.Value)).Where(d => d.Length > 0).Distinct().ToList();

            if (directDomains.Count > 0) dnsRules.Add(new JsonObject { ["domain_suffix"] = Arr(directDomains.ToArray()), ["server"] = "local" });
            if (vpnDomains.Count > 0) dnsRules.Add(new JsonObject { ["domain_suffix"] = Arr(vpnDomains.ToArray()), ["server"] = "remote" });

            if (t.RussiaDirect)
            {
                dnsRules.Add(new JsonObject { ["domain_suffix"] = Arr(RuZones), ["server"] = "local" });

                if (RuleSetExists("geosite-category-ru"))
                {
                    dnsRules.Add(new JsonObject { ["rule_set"] = "geosite-category-ru", ["server"] = "local" });
                }
            }

            config["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = s.RemoteDns, ["detour"] = Proxy },
                    LocalDnsServer(s.LocalDns)
                },
                ["rules"] = dnsRules,
                ["final"] = defaultVpn && plan.Nodes.Count > 0 ? "remote" : "local",
                ["strategy"] = "ipv4_only"
            };

            return config;
        }

        /// <summary>«system» — DNS Windows (учитывает hosts и DNS провайдера), иначе DoH на указанный адрес.</summary>
        private static JsonObject LocalDnsServer(string value) =>
            string.IsNullOrWhiteSpace(value) || value.Equals("system", StringComparison.OrdinalIgnoreCase)
                ? new JsonObject { ["type"] = "local", ["tag"] = "local" }
                : new JsonObject { ["type"] = "https", ["tag"] = "local", ["server"] = value.Trim() };

        private static JsonObject Rule(JsonObject match, RouteAction action)
        {
            if (action == RouteAction.Block)
            {
                match["action"] = "reject";
            }
            else
            {
                match["outbound"] = action == RouteAction.Direct ? Direct : Proxy;
            }

            return match;
        }

        // ================================================================== Xray

        private static JsonObject Xray(Plan plan, Options opt)
        {
            JsonArray inbounds = new JsonArray();
            JsonArray outbounds = new JsonArray { new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" } };
            JsonArray rules = new JsonArray();

            foreach (PlanNode n in plan.Nodes.Where(n => n.Engine == Engine.Xray))
            {
                inbounds.Add(new JsonObject
                {
                    ["tag"] = "in-" + n.Tag,
                    ["listen"] = "127.0.0.1",
                    ["port"] = n.XrayPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject
                    {
                        ["auth"] = "password",
                        ["accounts"] = new JsonArray { new JsonObject { ["user"] = opt.XrayUser, ["pass"] = opt.XrayPass } },
                        ["udp"] = true,
                        ["ip"] = "127.0.0.1"
                    }
                });

                outbounds.Add(XrayOutbound.Build(n.Server, n.Tag, opt.XrayFragment));
                rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = Arr("in-" + n.Tag), ["outboundTag"] = n.Tag });
            }

            if (opt.XrayFragment)
            {
                outbounds.Add(XrayOutbound.FragmentOutbound());
            }

            return new JsonObject
            {
                ["log"] = new JsonObject { ["loglevel"] = "warning" },
                ["inbounds"] = inbounds,
                ["outbounds"] = outbounds,
                ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = rules }
            };
        }

        // ================================================================== мелочи

        private static readonly string[] RuZones = { "ru", "su", "xn--p1ai", "xn--d1acj3b", "xn--80asehdb", "xn--80aswg" };

        /// <summary>«https://www.YouTube.com/watch» → «youtube.com».</summary>
        public static string NormalizeDomain(string input)
        {
            string d = (input ?? "").Trim().ToLowerInvariant();
            int scheme = d.IndexOf("://", StringComparison.Ordinal);

            if (scheme >= 0) d = d.Substring(scheme + 3);

            int cut = d.IndexOfAny(new[] { '/', '?', '#', ':' });

            if (cut >= 0) d = d.Substring(0, cut);
            if (d.StartsWith("*.")) d = d.Substring(2);
            if (d.StartsWith("www.")) d = d.Substring(4);

            d = d.Trim('.');

            try
            {
                // кириллические домены → punycode (сайт.рф → xn--80aswg.xn--p1ai)
                d = new System.Globalization.IdnMapping().GetAscii(d);
            }
            catch
            {
                // оставим как есть
            }

            return d;
        }

        public static string NormalizeCidr(string input)
        {
            string v = (input ?? "").Trim();
            return v.Contains('/') ? v : v.Contains(':') ? v + "/128" : v + "/32";
        }

        private static JsonArray Arr(params string[] values) => new JsonArray(values.Select(v => (JsonNode)v).ToArray());

        public static string RuleSetPath(string tag) => Path.Combine(Paths.RuleSets, tag + ".srs");

        public static bool RuleSetExists(string tag) => File.Exists(RuleSetPath(tag));

        private static JsonObject LocalRuleSet(string tag) => new JsonObject
        {
            ["type"] = "local",
            ["tag"] = tag,
            ["format"] = "binary",
            ["path"] = RuleSetPath(tag)
        };

        private static string Serialize(JsonObject o) =>
            o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() });
    }
}
