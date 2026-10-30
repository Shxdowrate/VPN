using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace NeonVpn.Core
{
    /// <summary>ServerNode → outbound sing-box 1.14.</summary>
    public static class SingBoxOutbound
    {
        /// <summary>Умеет ли sing-box этот сервер (xhttp и tcp с http-заголовком — только Xray).</summary>
        public static bool Supports(ServerNode n, out string why)
        {
            why = "";

            if (n.Protocol is "vless" or "vmess" or "trojan")
            {
                string t = n.Transport;

                if (t is "xhttp" or "splithttp")
                {
                    why = "транспорт xhttp есть только в Xray";
                    return false;
                }

                if (t is "kcp" or "mkcp" or "quic")
                {
                    why = "транспорт " + t + " есть только в Xray";
                    return false;
                }

                if ((t is "tcp" or "raw") && n.P("headerType") == "http")
                {
                    why = "маскировка tcp под http есть только в Xray";
                    return false;
                }
            }

            if (n.Protocol == "vless" && n.P("encryption", "none") != "none")
            {
                why = "VLESS Encryption есть только в Xray";
                return false;
            }

            if (n.Protocol == "shadowsocks" && n.P("plugin").Length > 0)
            {
                why = "плагины Shadowsocks не поддерживаются";
                return false;
            }

            return n.Protocol is "vless" or "vmess" or "trojan" or "shadowsocks" or "hysteria2" or "tuic" or "socks";
        }

        public static JsonObject Build(ServerNode n, string tag)
        {
            JsonObject o = new JsonObject { ["tag"] = tag };

            switch (n.Protocol)
            {
                case "vless":
                    o["type"] = "vless";
                    o["uuid"] = n.User;

                    if (n.P("flow").Length > 0)
                    {
                        o["flow"] = n.P("flow");
                    }

                    o["packet_encoding"] = n.P("packetEncoding", "xudp");
                    break;

                case "vmess":
                    o["type"] = "vmess";
                    o["uuid"] = n.User;
                    o["security"] = n.P("encryption", "auto");
                    o["alter_id"] = int.TryParse(n.P("aid"), out int aid) ? aid : 0;
                    o["packet_encoding"] = "xudp";
                    break;

                case "trojan":
                    o["type"] = "trojan";
                    o["password"] = n.User;
                    break;

                case "shadowsocks":
                    o["type"] = "shadowsocks";
                    o["method"] = n.P("method");
                    o["password"] = n.User;
                    break;

                case "hysteria2":
                {
                    o["type"] = "hysteria2";
                    o["password"] = n.User;

                    string obfs = n.P("obfs");

                    if (obfs.Length > 0 && obfs != "none")
                    {
                        o["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = n.P("obfs-password") };
                    }

                    if (int.TryParse(n.P("upmbps", n.P("up")), out int up) && up > 0) o["up_mbps"] = up;
                    if (int.TryParse(n.P("downmbps", n.P("down")), out int down) && down > 0) o["down_mbps"] = down;
                    break;
                }

                case "tuic":
                {
                    string[] up = n.User.Split(':', 2);
                    o["type"] = "tuic";
                    o["uuid"] = up[0];
                    o["password"] = up.Length > 1 ? up[1] : n.P("password");
                    o["congestion_control"] = n.P("congestion_control", "bbr");
                    o["udp_relay_mode"] = n.P("udp_relay_mode", "native");
                    break;
                }

                case "socks":
                {
                    o["type"] = "socks";
                    o["version"] = "5";
                    string[] up = n.User.Split(':', 2);

                    if (up[0].Length > 0)
                    {
                        o["username"] = up[0];
                        o["password"] = up.Length > 1 ? up[1] : "";
                    }

                    break;
                }

                default:
                    throw new NotSupportedException(n.Protocol);
            }

            o["server"] = n.Host;
            o["server_port"] = n.Port;

            JsonObject tls = Tls(n);

            if (tls != null)
            {
                o["tls"] = tls;
            }

            if (n.Protocol is "vless" or "vmess" or "trojan")
            {
                JsonObject transport = Transport(n);

                if (transport != null)
                {
                    o["transport"] = transport;
                }
            }

            return o;
        }

        private static JsonObject Tls(ServerNode n)
        {
            string security = n.Security;

            if (security != "tls" && security != "reality")
            {
                return null;
            }

            JsonObject tls = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = n.P("sni", n.P("peer", n.P("host", n.Host).Split(',')[0]))
            };

            if (LinkParser.Truthy(n.P("allowInsecure")) || LinkParser.Truthy(n.P("insecure")) || LinkParser.Truthy(n.P("allow_insecure")))
            {
                tls["insecure"] = true;
            }

            string alpn = n.P("alpn");

            if (n.Protocol == "hysteria2" && alpn.Length == 0)
            {
                alpn = "h3";
            }

            if (alpn.Length > 0)
            {
                tls["alpn"] = new JsonArray(alpn.Split(',').Select(a => (JsonNode)a.Trim()).Where(a => a.ToString().Length > 0).ToArray());
            }

            // QUIC-протоколы uTLS не используют.
            if (n.Protocol is not ("hysteria2" or "tuic"))
            {
                string fp = n.P("fp");

                // Reality в sing-box работает только с uTLS.
                if ((fp.Length > 0 && fp != "none") || security == "reality")
                {
                    tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp.Length > 0 && fp != "none" ? fp : "chrome" };
                }
            }

            if (security == "reality")
            {
                tls["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = n.P("pbk"),
                    ["short_id"] = n.P("sid")
                };
            }

            return tls;
        }

        private static JsonObject Transport(ServerNode n)
        {
            string path = n.P("path");
            string host = n.P("host");

            switch (n.Transport)
            {
                case "ws":
                {
                    // ?ed=2048 в пути — early data, как у v2ray.
                    int ed = 0;
                    int edAt = path.IndexOf("?ed=", StringComparison.Ordinal);

                    if (edAt >= 0)
                    {
                        int.TryParse(path.Substring(edAt + 4).Split('&')[0], out ed);
                        path = path.Substring(0, edAt);
                    }

                    JsonObject t = new JsonObject { ["type"] = "ws", ["path"] = path.Length == 0 ? "/" : path };

                    if (host.Length > 0)
                    {
                        t["headers"] = new JsonObject { ["Host"] = host };
                    }

                    if (ed > 0)
                    {
                        t["max_early_data"] = ed;
                        t["early_data_header_name"] = "Sec-WebSocket-Protocol";
                    }

                    return t;
                }

                case "grpc":
                    return new JsonObject { ["type"] = "grpc", ["service_name"] = n.P("serviceName", path) };

                case "http":
                case "h2":
                {
                    JsonObject t = new JsonObject { ["type"] = "http", ["path"] = path.Length == 0 ? "/" : path };

                    if (host.Length > 0)
                    {
                        t["host"] = new JsonArray(host.Split(',').Select(h => (JsonNode)h.Trim()).ToArray());
                    }

                    return t;
                }

                case "httpupgrade":
                {
                    JsonObject t = new JsonObject { ["type"] = "httpupgrade", ["path"] = path.Length == 0 ? "/" : path };

                    if (host.Length > 0)
                    {
                        t["host"] = host;
                    }

                    return t;
                }

                default:
                    return null; // tcp / raw
            }
        }
    }
}
