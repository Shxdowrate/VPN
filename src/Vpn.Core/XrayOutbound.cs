using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace NeonVpn.Core
{
    /// <summary>ServerNode → outbound Xray. Hysteria2 и TUIC оставляем sing-box: у него они надёжнее.</summary>
    public static class XrayOutbound
    {
        public static bool Supports(ServerNode n) =>
            n.Protocol is "vless" or "vmess" or "trojan" or "shadowsocks" or "socks";

        /// <param name="fragment">Резать TLS ClientHello на куски — помогает на мобильном интернете с DPI.</param>
        public static JsonObject Build(ServerNode n, string tag, bool fragment)
        {
            JsonObject o = new JsonObject { ["tag"] = tag };

            switch (n.Protocol)
            {
                case "vless":
                {
                    JsonObject user = new JsonObject { ["id"] = n.User, ["encryption"] = n.P("encryption", "none") };

                    if (n.P("flow").Length > 0)
                    {
                        user["flow"] = n.P("flow");
                    }

                    o["protocol"] = "vless";
                    o["settings"] = Vnext(n, user);
                    break;
                }

                case "vmess":
                    o["protocol"] = "vmess";
                    o["settings"] = Vnext(n, new JsonObject { ["id"] = n.User, ["security"] = n.P("encryption", "auto") });
                    break;

                case "trojan":
                    o["protocol"] = "trojan";
                    o["settings"] = new JsonObject
                    {
                        ["servers"] = new JsonArray { new JsonObject { ["address"] = n.Host, ["port"] = n.Port, ["password"] = n.User } }
                    };
                    break;

                case "shadowsocks":
                    o["protocol"] = "shadowsocks";
                    o["settings"] = new JsonObject
                    {
                        ["servers"] = new JsonArray
                        {
                            new JsonObject { ["address"] = n.Host, ["port"] = n.Port, ["method"] = n.P("method"), ["password"] = n.User, ["uot"] = true }
                        }
                    };
                    break;

                case "socks":
                {
                    JsonObject server = new JsonObject { ["address"] = n.Host, ["port"] = n.Port };
                    string[] up = n.User.Split(':', 2);

                    if (up[0].Length > 0)
                    {
                        server["users"] = new JsonArray { new JsonObject { ["user"] = up[0], ["pass"] = up.Length > 1 ? up[1] : "" } };
                    }

                    o["protocol"] = "socks";
                    o["settings"] = new JsonObject { ["servers"] = new JsonArray { server } };
                    break;
                }

                default:
                    throw new NotSupportedException(n.Protocol);
            }

            JsonObject stream = Stream(n);

            if (fragment && n.Security is "tls" or "reality" or "none" && n.Transport is not ("kcp" or "mkcp" or "quic"))
            {
                stream["sockopt"] = new JsonObject { ["dialerProxy"] = "fragment" };
            }

            o["streamSettings"] = stream;

            if (n.Protocol is "vless" or "vmess" && n.P("flow").Length == 0 && LinkParser.Truthy(n.P("mux")))
            {
                o["mux"] = new JsonObject { ["enabled"] = true, ["concurrency"] = 8, ["xudpConcurrency"] = 16 };
            }

            return o;
        }

        /// <summary>Вспомогательный outbound, через который идут соединения с фрагментацией и/или «шумом».</summary>
        public static JsonObject FragmentOutbound(bool fragment = true, string packets = "tlshello", string length = "100-200", string interval = "10-20", JsonArray noises = null)
        {
            JsonObject settings = new JsonObject();

            if (fragment)
            {
                settings["fragment"] = new JsonObject { ["packets"] = packets, ["length"] = length, ["interval"] = interval };
            }

            if (noises != null && noises.Count > 0)
            {
                settings["noises"] = noises.DeepClone();
            }

            return new JsonObject { ["tag"] = "fragment", ["protocol"] = "freedom", ["settings"] = settings };
        }

        private static JsonObject Vnext(ServerNode n, JsonObject user) => new JsonObject
        {
            ["vnext"] = new JsonArray
            {
                new JsonObject { ["address"] = n.Host, ["port"] = n.Port, ["users"] = new JsonArray { user } }
            }
        };

        private static JsonObject Stream(ServerNode n)
        {
            string network = n.Protocol is "shadowsocks" or "socks" ? "raw" : n.Transport;

            network = network switch
            {
                "tcp" => "raw",
                "splithttp" => "xhttp",
                "h2" => "http",
                "mkcp" => "kcp",
                _ => network
            };

            JsonObject s = new JsonObject { ["network"] = network };
            string path = n.P("path");
            string host = n.P("host");

            switch (network)
            {
                case "ws":
                {
                    JsonObject ws = new JsonObject { ["path"] = path.Length == 0 ? "/" : path };
                    if (host.Length > 0) ws["host"] = host;
                    s["wsSettings"] = ws;
                    break;
                }

                case "grpc":
                    s["grpcSettings"] = new JsonObject
                    {
                        ["serviceName"] = n.P("serviceName", path),
                        ["multiMode"] = n.P("mode") == "multi",
                        ["authority"] = n.P("authority")
                    };
                    break;

                case "httpupgrade":
                {
                    JsonObject hu = new JsonObject { ["path"] = path.Length == 0 ? "/" : path };
                    if (host.Length > 0) hu["host"] = host;
                    s["httpupgradeSettings"] = hu;
                    break;
                }

                case "xhttp":
                {
                    JsonObject x = new JsonObject { ["path"] = path.Length == 0 ? "/" : path, ["mode"] = n.P("mode", "auto") };
                    if (host.Length > 0) x["host"] = host;

                    // extra — JSON с тонкими настройками xhttp прямо в ссылке.
                    string extra = n.P("extra");

                    if (extra.Length > 0)
                    {
                        try
                        {
                            x["extra"] = JsonNode.Parse(extra);
                        }
                        catch
                        {
                            Log.Write("xhttp extra не JSON, пропускаю: " + n.DisplayName);
                        }
                    }

                    s["xhttpSettings"] = x;
                    break;
                }

                case "http":
                {
                    JsonObject h = new JsonObject { ["path"] = path.Length == 0 ? "/" : path };
                    if (host.Length > 0) h["host"] = new JsonArray(host.Split(',').Select(x => (JsonNode)x.Trim()).ToArray());
                    s["httpSettings"] = h;
                    break;
                }

                case "kcp":
                {
                    JsonObject k = new JsonObject { ["header"] = new JsonObject { ["type"] = n.P("headerType", "none") } };
                    if (n.P("seed").Length > 0) k["seed"] = n.P("seed");
                    s["kcpSettings"] = k;
                    break;
                }

                default:
                    if (n.P("headerType") == "http")
                    {
                        s["rawSettings"] = new JsonObject
                        {
                            ["header"] = new JsonObject
                            {
                                ["type"] = "http",
                                ["request"] = new JsonObject
                                {
                                    ["path"] = new JsonArray((JsonNode)(path.Length == 0 ? "/" : path)),
                                    ["headers"] = new JsonObject { ["Host"] = new JsonArray((JsonNode)(host.Length == 0 ? n.Host : host)) }
                                }
                            }
                        };
                    }

                    break;
            }

            string security = n.Security;
            string sni = n.P("sni", n.P("peer", host.Split(',')[0].Length > 0 ? host.Split(',')[0] : n.Host));
            string fp = n.P("fp", "chrome");

            if (security == "tls")
            {
                JsonObject tls = new JsonObject { ["serverName"] = sni, ["fingerprint"] = fp == "none" ? "chrome" : fp };

                if (LinkParser.Truthy(n.P("allowInsecure")) || LinkParser.Truthy(n.P("insecure")))
                {
                    tls["allowInsecure"] = true;
                }

                string alpn = n.P("alpn");

                if (alpn.Length > 0)
                {
                    tls["alpn"] = new JsonArray(alpn.Split(',').Select(a => (JsonNode)a.Trim()).ToArray());
                }

                s["security"] = "tls";
                s["tlsSettings"] = tls;
            }
            else if (security == "reality")
            {
                s["security"] = "reality";
                s["realitySettings"] = new JsonObject
                {
                    ["serverName"] = sni,
                    ["fingerprint"] = fp == "none" ? "chrome" : fp,
                    ["publicKey"] = n.P("pbk"),
                    ["shortId"] = n.P("sid"),
                    ["spiderX"] = n.P("spx", "/")
                };
            }
            else
            {
                s["security"] = "none";
            }

            return s;
        }
    }
}
