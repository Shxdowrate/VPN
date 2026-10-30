using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace NeonVpn.Core
{
    /// <summary>
    /// Разбирает ссылки серверов в нейтральный ServerNode:
    /// vless://, vmess://, trojan://, ss://, hysteria2:// (hy2://), tuic://, socks:// (socks5://).
    /// </summary>
    public static class LinkParser
    {
        public static readonly string[] Schemes = { "vless", "vmess", "trojan", "ss", "hysteria2", "hy2", "tuic", "socks", "socks5" };

        public static bool LooksLikeLink(string line)
        {
            int i = line.IndexOf("://", StringComparison.Ordinal);
            return i > 0 && Schemes.Contains(line.Substring(0, i).Trim().ToLowerInvariant());
        }

        /// <summary>Разбирает тело подписки или вставленный текст: base64-список, обычный список, по ссылке в строке.</summary>
        public static List<ServerNode> ParseMany(string text, out int skipped)
        {
            skipped = 0;
            List<ServerNode> result = new List<ServerNode>();
            text = (text ?? "").Trim().TrimStart('﻿');

            if (!text.Contains("://"))
            {
                text = TryBase64(text) ?? text;
            }

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();

                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                {
                    continue;
                }

                try
                {
                    ServerNode node = Parse(line);

                    if (node != null)
                    {
                        result.Add(node);
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (Exception e)
                {
                    skipped++;
                    Log.Write("Не разобрал ссылку " + line.Split(':')[0] + ": " + e.Message);
                }
            }

            return result;
        }

        public static ServerNode Parse(string line)
        {
            int colon = line.IndexOf("://", StringComparison.Ordinal);

            if (colon <= 0)
            {
                return null;
            }

            string scheme = line.Substring(0, colon).ToLowerInvariant();

            ServerNode node = scheme switch
            {
                "vless" => Generic("vless", line),
                "trojan" => Generic("trojan", line),
                "hysteria2" or "hy2" => Generic("hysteria2", line),
                "tuic" => Generic("tuic", line),
                "socks" or "socks5" => Socks(line),
                "vmess" => Vmess(line),
                "ss" => Shadowsocks(line),
                _ => null
            };

            if (node == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(node.Host) || node.Port <= 0 || node.Port > 65535)
            {
                throw new FormatException("нет адреса или порта");
            }

            node.Link = line;

            if (string.IsNullOrWhiteSpace(node.Name))
            {
                node.Name = node.Host + ":" + node.Port;
            }

            return node;
        }

        // ------------------------------------------------------------------ разбор

        /// <summary>scheme://user@host:port?query#name — вручную: System.Uri портит нестандартные схемы.</summary>
        private static ServerNode Split(string protocol, string line)
        {
            ServerNode n = new ServerNode { Protocol = protocol };
            string rest = line.Substring(line.IndexOf("://", StringComparison.Ordinal) + 3);

            int hash = rest.IndexOf('#');

            if (hash >= 0)
            {
                n.Name = Unescape(rest.Substring(hash + 1)).Trim();
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
                        n.Params[key] = parsed[key] ?? "";
                    }
                }

                rest = rest.Substring(0, q);
            }

            rest = rest.TrimEnd('/');
            int at = rest.LastIndexOf('@');

            if (at >= 0)
            {
                n.User = Unescape(rest.Substring(0, at));
                rest = rest.Substring(at + 1);
            }

            if (rest.StartsWith("["))
            {
                int end = rest.IndexOf(']');
                n.Host = rest.Substring(1, end - 1);
                rest = rest.Substring(end + 1);
                n.Port = rest.StartsWith(":") ? int.Parse(rest.Substring(1)) : 443;
            }
            else
            {
                int c = rest.LastIndexOf(':');
                n.Host = c >= 0 ? rest.Substring(0, c) : rest;
                n.Port = c >= 0 ? int.Parse(rest.Substring(c + 1)) : 443;
            }

            return n;
        }

        private static ServerNode Generic(string protocol, string line)
        {
            ServerNode n = Split(protocol, line);

            // Hysteria2 и TUIC всегда с TLS; у trojan TLS по умолчанию.
            if (protocol is "hysteria2" or "tuic")
            {
                n.Params["security"] = "tls";
            }

            // hy2: порты-«прыжки» (mport / ports) нам не нужны для первого подключения — основной порт остаётся.
            return n;
        }

        private static ServerNode Socks(string line)
        {
            ServerNode n = Split("socks", line);

            // socks://base64(user:pass)@host:port — некоторые клиенты кодируют логин.
            if (n.User.Length > 0 && !n.User.Contains(':'))
            {
                string decoded = TryBase64(n.User);

                if (decoded != null && decoded.Contains(':'))
                {
                    n.User = decoded;
                }
            }

            return n;
        }

        private static ServerNode Vmess(string line)
        {
            string body = line.Substring("vmess://".Length);
            int hashAt = body.IndexOf('#');
            string tailName = "";

            if (hashAt >= 0)
            {
                tailName = Unescape(body.Substring(hashAt + 1));
                body = body.Substring(0, hashAt);
            }

            string json = TryBase64(body);

            if (json == null || !json.TrimStart().StartsWith("{"))
            {
                // vmess://uuid@host:port?… (новый формат, как у vless)
                return Split("vmess", line);
            }

            JsonNode v = JsonNode.Parse(json);
            string S(string key) => v?[key] is JsonValue value ? value.ToString() : "";

            ServerNode n = new ServerNode
            {
                Protocol = "vmess",
                Host = S("add"),
                Port = int.TryParse(S("port"), out int port) ? port : 443,
                Name = S("ps").Length > 0 ? S("ps") : tailName,
                User = S("id")
            };

            string net = S("net").Length > 0 ? S("net") : "tcp";
            n.Params["type"] = net;
            n.Params["encryption"] = S("scy").Length > 0 ? S("scy") : "auto";
            n.Params["aid"] = S("aid");
            n.Params["host"] = S("host");
            n.Params["path"] = S("path");
            n.Params["headerType"] = S("type");
            n.Params["sni"] = S("sni");
            n.Params["fp"] = S("fp");
            n.Params["alpn"] = S("alpn");

            if (net == "grpc")
            {
                n.Params["serviceName"] = S("path");
                n.Params["mode"] = S("type") == "multi" ? "multi" : "gun";
            }

            if (S("tls") is "tls" or "reality")
            {
                n.Params["security"] = S("tls");
            }

            return n;
        }

        private static ServerNode Shadowsocks(string line)
        {
            string rest = line.Substring("ss://".Length);
            string name = "";
            int hash = rest.IndexOf('#');

            if (hash >= 0)
            {
                name = Unescape(rest.Substring(hash + 1));
                rest = rest.Substring(0, hash);
            }

            Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int q = rest.IndexOf('?');

            if (q >= 0)
            {
                var parsed = HttpUtility.ParseQueryString(rest.Substring(q + 1));

                foreach (string key in parsed.AllKeys)
                {
                    if (key != null) query[key] = parsed[key] ?? "";
                }

                rest = rest.Substring(0, q);
            }

            string userInfo;
            string hostPort;
            int at = rest.LastIndexOf('@');

            if (at >= 0)
            {
                // ss://base64(method:password)@host:port  или  ss://method:password@host:port
                string user = Unescape(rest.Substring(0, at));
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

            ServerNode n = Split("shadowsocks", "ss://x@" + hostPort.TrimEnd('/'));
            n.Name = name;
            int c = userInfo.IndexOf(':');

            if (c <= 0)
            {
                throw new FormatException("ss: нет метода шифрования");
            }

            n.Params["method"] = userInfo.Substring(0, c);
            n.User = userInfo.Substring(c + 1);

            foreach (var kv in query)
            {
                n.Params[kv.Key] = kv.Value;
            }

            return n;
        }

        // ------------------------------------------------------------------ мелочи

        private static string Unescape(string s)
        {
            try
            {
                return Uri.UnescapeDataString(s.Replace('+', ' '));
            }
            catch
            {
                return s;
            }
        }

        public static string TryBase64(string s)
        {
            try
            {
                s = s.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "").Replace(" ", "");

                switch (s.Length % 4)
                {
                    case 2: s += "=="; break;
                    case 3: s += "="; break;
                    case 1: return null;
                }

                string text = Encoding.UTF8.GetString(Convert.FromBase64String(s));
                return text.Contains('�') ? null : text;
            }
            catch
            {
                return null;
            }
        }

        public static bool Truthy(string v) => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
