using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web;

namespace VPN.Core
{
    /// <summary>Результат загрузки подписки.</summary>
    public sealed class SubscriptionResult
    {
        public List<ServerProfile> Servers { get; } = new List<ServerProfile>();
        public string Title { get; set; } = "";
        public long Used { get; set; }
        public long Total { get; set; }
        public long Expire { get; set; }
        public int Skipped { get; set; }
    }

    /// <summary>
    /// Загружает подписку и превращает её в серверы sing-box. Понимает base64-список ссылок,
    /// обычный список ссылок и JSON-конфиг sing-box. Ссылки: vless, vmess, trojan, ss, hysteria2/hy2, tuic, socks.
    /// </summary>
    public static class Subscription
    {
        private static readonly string[] Supported = { "vless", "vmess", "trojan", "shadowsocks", "hysteria2", "tuic", "anytls", "socks", "http" };

        public static async Task<SubscriptionResult> LoadAsync(string url)
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(25) };
            // Панели отдают формат по User-Agent; этот получает обычный список ссылок.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN/7.10");

            using HttpResponseMessage response = await http.GetAsync(url.Trim());
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync();

            SubscriptionResult result = new SubscriptionResult();
            ReadHeaders(response, result);
            Parse(body, result);
            return result;
        }

        private static void ReadHeaders(HttpResponseMessage response, SubscriptionResult result)
        {
            if (response.Headers.TryGetValues("profile-title", out var titles))
            {
                string title = titles.FirstOrDefault() ?? "";

                if (title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
                {
                    title = TryBase64(title.Substring(7)) ?? title;
                }

                result.Title = title;
            }

            if (response.Headers.TryGetValues("subscription-userinfo", out var infos))
            {
                // upload=123; download=456; total=789; expire=1700000000
                long up = 0, down = 0;

                foreach (string part in (infos.FirstOrDefault() ?? "").Split(';'))
                {
                    string[] kv = part.Trim().Split('=');

                    if (kv.Length != 2 || !long.TryParse(kv[1].Trim(), out long value))
                    {
                        continue;
                    }

                    switch (kv[0].Trim().ToLowerInvariant())
                    {
                        case "upload": up = value; break;
                        case "download": down = value; break;
                        case "total": result.Total = value; break;
                        case "expire": result.Expire = value; break;
                    }
                }

                result.Used = up + down;
            }
        }

        public static void Parse(string body, SubscriptionResult result)
        {
            body = body.Trim();

            if (body.StartsWith("{"))
            {
                ParseSingBoxJson(body, result);
                return;
            }

            if (!body.Contains("://"))
            {
                body = TryBase64(body) ?? body;
            }

            foreach (string raw in body.Split('\n'))
            {
                string line = raw.Trim();

                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                try
                {
                    ServerProfile server = ParseUri(line);

                    if (server != null)
                    {
                        AddUnique(result, server);
                    }
                    else
                    {
                        result.Skipped++;
                    }
                }
                catch (Exception e)
                {
                    result.Skipped++;
                    Log.Write("Не разобрал ссылку (" + line.Split(':')[0] + "): " + e.Message);
                }
            }
        }

        private static void AddUnique(SubscriptionResult result, ServerProfile server)
        {
            server.Tag = "s" + (result.Servers.Count + 1);

            if (string.IsNullOrWhiteSpace(server.Name))
            {
                server.Name = server.Host;
            }

            result.Servers.Add(server);
        }

        private static void ParseSingBoxJson(string body, SubscriptionResult result)
        {
            JsonNode root = JsonNode.Parse(body);

            if (root?["outbounds"] is not JsonArray outbounds)
            {
                return;
            }

            foreach (JsonNode node in outbounds)
            {
                string type = node?["type"]?.GetValue<string>() ?? "";

                if (!Supported.Contains(type))
                {
                    continue;
                }

                JsonObject outbound = node.DeepClone().AsObject();
                string name = outbound["tag"]?.GetValue<string>() ?? "";
                outbound.Remove("tag");

                AddUnique(result, new ServerProfile
                {
                    Name = name,
                    Protocol = type,
                    Host = outbound["server"]?.GetValue<string>() ?? "",
                    Port = outbound["server_port"]?.GetValue<int>() ?? 0,
                    Outbound = outbound.ToJsonString()
                });
            }
        }

        // ================================================================== ссылки

        public static ServerProfile ParseUri(string line)
        {
            int colon = line.IndexOf("://", StringComparison.Ordinal);

            if (colon <= 0)
            {
                return null;
            }

            string scheme = line.Substring(0, colon).ToLowerInvariant();

            return scheme switch
            {
                "vless" => Vless(line),
                "trojan" => Trojan(line),
                "vmess" => Vmess(line),
                "ss" => Shadowsocks(line),
                "hysteria2" or "hy2" => Hysteria2(line),
                "tuic" => Tuic(line),
                "socks" or "socks5" => Socks(line),
                _ => null
            };
        }

        private sealed class Link
        {
            public string User = "";
            public string Host = "";
            public int Port;
            public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Name = "";

            public string Q(string key, string fallback = "") =>
                Query.TryGetValue(key, out string v) && !string.IsNullOrEmpty(v) ? v : fallback;
        }

        /// <summary>scheme://user@host:port?query#name — разбираем вручную: System.Uri портит нестандартные схемы.</summary>
        private static Link Split(string line)
        {
            Link link = new Link();
            string rest = line.Substring(line.IndexOf("://", StringComparison.Ordinal) + 3);

            int hash = rest.IndexOf('#');

            if (hash >= 0)
            {
                link.Name = Uri.UnescapeDataString(rest.Substring(hash + 1).Replace('+', ' ')).Trim();
                rest = rest.Substring(0, hash);
            }

            int q = rest.IndexOf('?');

            if (q >= 0)
            {
                var parsed = HttpUtility.ParseQueryString(rest.Substring(q + 1));

                foreach (string key in parsed.AllKeys)
                {
                    if (key != null)
                    {
                        link.Query[key] = parsed[key] ?? "";
                    }
                }

                rest = rest.Substring(0, q);
            }

            rest = rest.TrimEnd('/');
            int at = rest.LastIndexOf('@');

            if (at >= 0)
            {
                link.User = Uri.UnescapeDataString(rest.Substring(0, at));
                rest = rest.Substring(at + 1);
            }

            // IPv6: [::1]:443
            if (rest.StartsWith("["))
            {
                int end = rest.IndexOf(']');
                link.Host = rest.Substring(1, end - 1);
                rest = rest.Substring(end + 1);
                link.Port = rest.StartsWith(":") ? int.Parse(rest.Substring(1)) : 443;
            }
            else
            {
                int c = rest.LastIndexOf(':');
                link.Host = c >= 0 ? rest.Substring(0, c) : rest;
                link.Port = c >= 0 ? int.Parse(rest.Substring(c + 1)) : 443;
            }

            return link;
        }

        private static ServerProfile Profile(string protocol, Link link, JsonObject outbound)
        {
            outbound["server"] = link.Host;
            outbound["server_port"] = link.Port;

            return new ServerProfile
            {
                Name = link.Name,
                Protocol = protocol,
                Host = link.Host,
                Port = link.Port,
                Outbound = outbound.ToJsonString()
            };
        }

        private static ServerProfile Vless(string line)
        {
            Link l = Split(line);
            JsonObject o = new JsonObject { ["type"] = "vless", ["uuid"] = l.User };

            string flow = l.Q("flow");

            if (flow.Length > 0)
            {
                o["flow"] = flow;
            }

            o["packet_encoding"] = l.Q("packetEncoding", "xudp");
            ApplyTls(o, l, l.Q("security"));
            ApplyTransport(o, l.Q("type", "tcp"), l.Q("path"), l.Q("host"), l.Q("serviceName"));
            return Profile("vless", l, o);
        }

        private static ServerProfile Trojan(string line)
        {
            Link l = Split(line);
            JsonObject o = new JsonObject { ["type"] = "trojan", ["password"] = l.User };
            // У trojan TLS включён по умолчанию.
            ApplyTls(o, l, l.Q("security", "tls"));
            ApplyTransport(o, l.Q("type", "tcp"), l.Q("path"), l.Q("host"), l.Q("serviceName"));
            return Profile("trojan", l, o);
        }

        private static ServerProfile Vmess(string line)
        {
            string json = TryBase64(line.Substring("vmess://".Length)) ?? throw new FormatException("vmess: не base64");
            JsonNode v = JsonNode.Parse(json);

            string S(string key) => v?[key] is JsonValue value ? value.ToString() : "";

            Link l = new Link
            {
                Host = S("add"),
                Port = int.TryParse(S("port"), out int port) ? port : 443,
                Name = S("ps")
            };
            l.Query["sni"] = S("sni").Length > 0 ? S("sni") : S("host");
            l.Query["fp"] = S("fp");
            l.Query["alpn"] = S("alpn");

            JsonObject o = new JsonObject
            {
                ["type"] = "vmess",
                ["uuid"] = S("id"),
                ["alter_id"] = int.TryParse(S("aid"), out int aid) ? aid : 0,
                ["security"] = S("scy").Length > 0 ? S("scy") : "auto"
            };

            if (S("tls") == "tls")
            {
                ApplyTls(o, l, "tls");
            }

            ApplyTransport(o, S("net").Length > 0 ? S("net") : "tcp", S("path"), S("host"), S("path"));
            return Profile("vmess", l, o);
        }

        private static ServerProfile Shadowsocks(string line)
        {
            string rest = line.Substring("ss://".Length);
            string name = "";
            int hash = rest.IndexOf('#');

            if (hash >= 0)
            {
                name = Uri.UnescapeDataString(rest.Substring(hash + 1));
                rest = rest.Substring(0, hash);
            }

            int q = rest.IndexOf('?');

            if (q >= 0)
            {
                rest = rest.Substring(0, q);
            }

            string userInfo;
            string hostPort;
            int at = rest.LastIndexOf('@');

            if (at >= 0)
            {
                // ss://base64(method:password)@host:port  или  ss://method:password@host:port
                string user = Uri.UnescapeDataString(rest.Substring(0, at));
                userInfo = user.Contains(':') ? user : TryBase64(user) ?? user;
                hostPort = rest.Substring(at + 1);
            }
            else
            {
                // ss://base64(method:password@host:port)
                string decoded = TryBase64(rest) ?? throw new FormatException("ss: не base64");
                int a = decoded.LastIndexOf('@');
                userInfo = decoded.Substring(0, a);
                hostPort = decoded.Substring(a + 1);
            }

            Link l = Split("ss://x@" + hostPort.TrimEnd('/'));
            l.Name = name;
            int c = userInfo.IndexOf(':');

            JsonObject o = new JsonObject
            {
                ["type"] = "shadowsocks",
                ["method"] = userInfo.Substring(0, c),
                ["password"] = userInfo.Substring(c + 1)
            };

            return Profile("shadowsocks", l, o);
        }

        private static ServerProfile Hysteria2(string line)
        {
            Link l = Split(line);
            JsonObject o = new JsonObject { ["type"] = "hysteria2", ["password"] = l.User };

            string obfs = l.Q("obfs");

            if (obfs.Length > 0)
            {
                o["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = l.Q("obfs-password") };
            }

            JsonObject tls = new JsonObject { ["enabled"] = true, ["server_name"] = l.Q("sni", l.Host) };

            if (Truthy(l.Q("insecure")) || Truthy(l.Q("allowInsecure")))
            {
                tls["insecure"] = true;
            }

            tls["alpn"] = new JsonArray("h3");
            o["tls"] = tls;
            return Profile("hysteria2", l, o);
        }

        private static ServerProfile Tuic(string line)
        {
            Link l = Split(line);
            string[] up = l.User.Split(':', 2);

            JsonObject o = new JsonObject
            {
                ["type"] = "tuic",
                ["uuid"] = up[0],
                ["password"] = up.Length > 1 ? up[1] : "",
                ["congestion_control"] = l.Q("congestion_control", "bbr")
            };

            JsonObject tls = new JsonObject { ["enabled"] = true, ["server_name"] = l.Q("sni", l.Host) };
            string alpn = l.Q("alpn", "h3");
            tls["alpn"] = new JsonArray(alpn.Split(',').Select(a => (JsonNode)a.Trim()).ToArray());

            if (Truthy(l.Q("allow_insecure")) || Truthy(l.Q("insecure")))
            {
                tls["insecure"] = true;
            }

            o["tls"] = tls;
            return Profile("tuic", l, o);
        }

        private static ServerProfile Socks(string line)
        {
            Link l = Split(line);
            JsonObject o = new JsonObject { ["type"] = "socks", ["version"] = "5" };

            if (l.User.Length > 0)
            {
                // socks://base64(user:pass)@host:port  или  socks5://user:pass@host:port
                string user = l.User.Contains(':') ? l.User : TryBase64(l.User) ?? l.User;
                string[] up = user.Split(':', 2);
                o["username"] = up[0];

                if (up.Length > 1)
                {
                    o["password"] = up[1];
                }
            }

            return Profile("socks", l, o);
        }

        // ================================================================== общие куски

        private static void ApplyTls(JsonObject o, Link l, string security)
        {
            if (security != "tls" && security != "reality")
            {
                return;
            }

            JsonObject tls = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = l.Q("sni", l.Q("peer", l.Host))
            };

            if (Truthy(l.Q("allowInsecure")) || Truthy(l.Q("insecure")))
            {
                tls["insecure"] = true;
            }

            string alpn = l.Q("alpn");

            if (alpn.Length > 0)
            {
                tls["alpn"] = new JsonArray(alpn.Split(',').Select(a => (JsonNode)a.Trim()).ToArray());
            }

            string fp = l.Q("fp");

            // Reality в sing-box работает только с uTLS.
            if (fp.Length > 0 || security == "reality")
            {
                tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp.Length > 0 ? fp : "chrome" };
            }

            if (security == "reality")
            {
                tls["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = l.Q("pbk"),
                    ["short_id"] = l.Q("sid")
                };
            }

            o["tls"] = tls;
        }

        private static void ApplyTransport(JsonObject o, string type, string path, string host, string serviceName)
        {
            switch (type)
            {
                case "ws":
                {
                    JsonObject t = new JsonObject { ["type"] = "ws", ["path"] = string.IsNullOrEmpty(path) ? "/" : path };

                    if (!string.IsNullOrEmpty(host))
                    {
                        t["headers"] = new JsonObject { ["Host"] = host };
                    }

                    o["transport"] = t;
                    break;
                }
                case "grpc":
                    o["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = serviceName ?? "" };
                    break;
                case "http":
                case "h2":
                {
                    JsonObject t = new JsonObject { ["type"] = "http", ["path"] = string.IsNullOrEmpty(path) ? "/" : path };

                    if (!string.IsNullOrEmpty(host))
                    {
                        t["host"] = new JsonArray(host.Split(',').Select(h => (JsonNode)h.Trim()).ToArray());
                    }

                    o["transport"] = t;
                    break;
                }
                case "httpupgrade":
                {
                    JsonObject t = new JsonObject { ["type"] = "httpupgrade", ["path"] = string.IsNullOrEmpty(path) ? "/" : path };

                    if (!string.IsNullOrEmpty(host))
                    {
                        t["host"] = host;
                    }

                    o["transport"] = t;
                    break;
                }
                case "xhttp":
                case "splithttp":
                    throw new NotSupportedException("транспорт xhttp не поддерживается движком sing-box");
                default:
                    // tcp — без транспорта.
                    break;
            }
        }

        private static bool Truthy(string v) => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);

        public static string TryBase64(string s)
        {
            try
            {
                s = s.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");

                switch (s.Length % 4)
                {
                    case 2: s += "=="; break;
                    case 3: s += "="; break;
                }

                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Двухбуквенный код страны из флага-эмодзи в названии сервера (🇩🇪 → DE), если он есть.</summary>
        public static string CountryCode(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }

            var runes = name.EnumerateRunes().ToList();

            for (int i = 0; i + 1 < runes.Count; i++)
            {
                int a = runes[i].Value;
                int b = runes[i + 1].Value;

                if (a >= 0x1F1E6 && a <= 0x1F1FF && b >= 0x1F1E6 && b <= 0x1F1FF)
                {
                    return new string(new[] { (char)('A' + a - 0x1F1E6), (char)('A' + b - 0x1F1E6) });
                }
            }

            return "";
        }

        /// <summary>Название без флагов-эмодзи (Windows всё равно рисует их буквами).</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }

            var sb = new StringBuilder();

            foreach (Rune r in name.EnumerateRunes())
            {
                if (r.Value >= 0x1F1E6 && r.Value <= 0x1F1FF)
                {
                    continue;
                }

                sb.Append(r.ToString());
            }

            return sb.ToString().Trim();
        }
    }
}
