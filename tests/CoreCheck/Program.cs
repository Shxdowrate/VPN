using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NeonVpn.Core;

// Использование: CoreCheck <папка rules> <папка для конфигов>
// Разбирает набор ссылок всех видов, собирает конфиги для обоих ядер и всех режимов и кладёт их в папку —
// дальше их проверяют `sing-box check` и `xray run -test`.

string rules = args.Length > 0 ? args[0] : "rules";
string outDir = args.Length > 1 ? args[1] : "out-configs";
Directory.CreateDirectory(outDir);
Paths.Init(Path.GetFullPath(Path.Combine(rules, "..")), Path.GetFullPath(Path.Combine(outDir, "data")));

int failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    if (!ok) failures++;
}

string vmessJson = "{\"v\":\"2\",\"ps\":\"🇳🇱 NL vmess\",\"add\":\"nl.example.com\",\"port\":\"443\",\"id\":\"6f1c3a52-1b1e-4a5a-9c39-1a2b3c4d5e6f\",\"aid\":\"0\",\"scy\":\"auto\",\"net\":\"ws\",\"type\":\"none\",\"host\":\"cdn.example.com\",\"path\":\"/ws?ed=2048\",\"tls\":\"tls\",\"sni\":\"cdn.example.com\",\"fp\":\"chrome\"}";
string ssUser = Convert.ToBase64String(Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:pass123"));

string[] links =
{
    "vless://6f1c3a52-1b1e-4a5a-9c39-1a2b3c4d5e6f@de.example.com:443?type=tcp&security=reality&pbk=Y4Sx0F5bW0hKX2H3bS1n0yD2Cw7jQnS5j2U5aXJxVhY&sid=ab12&sni=www.microsoft.com&fp=chrome&flow=xtls-rprx-vision#%F0%9F%87%A9%F0%9F%87%AA%20Germany%20Reality",
    "vless://6f1c3a52-1b1e-4a5a-9c39-1a2b3c4d5e6f@fi.example.com:443?type=xhttp&security=tls&sni=fi.example.com&path=%2Fxh&mode=auto#FI%20xhttp",
    "vless://6f1c3a52-1b1e-4a5a-9c39-1a2b3c4d5e6f@us.example.com:443?type=grpc&security=tls&serviceName=grpcsvc&sni=us.example.com#US%20grpc",
    "vless://6f1c3a52-1b1e-4a5a-9c39-1a2b3c4d5e6f@[2001:db8::1]:8443?type=httpupgrade&security=tls&path=%2Fup&host=h.example.com#IPv6%20httpupgrade",
    "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmessJson)),
    "trojan://secretpass@tr.example.com:443?security=tls&sni=tr.example.com&type=ws&path=%2Ftr#Trojan%20WS",
    "ss://" + ssUser + "@ss.example.com:8388#SS",
    "hysteria2://hypass@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=ob1#%F0%9F%87%AF%F0%9F%87%B5%20Hy2",
    "tuic://6f1c3a52-1b1e-4a5a-9c39-1a2b3c4d5e6f:tpass@tu.example.com:443?sni=tu.example.com&congestion_control=bbr&alpn=h3#TUIC",
    "socks://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pw")) + "@so.example.com:1080#SOCKS"
};

Console.WriteLine("Разбор ссылок:");
string sub = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", links)));
List<ServerNode> nodes = LinkParser.ParseMany(sub, out int skipped);
Check(nodes.Count == links.Length && skipped == 0, $"base64-подписка: {nodes.Count} из {links.Length}, пропущено {skipped}");
Check(nodes[0].Country == "DE" && nodes[0].DisplayName == "Germany Reality", "флаг и имя: " + nodes[0].Country + " / " + nodes[0].DisplayName);
Check(nodes[3].Host == "2001:db8::1" && nodes[3].Port == 8443, "IPv6-адрес");
Check(nodes[4].Protocol == "vmess" && nodes[4].Transport == "ws" && nodes[4].Security == "tls", "vmess json");
Check(nodes[6].P("method") == "chacha20-ietf-poly1305" && nodes[6].User == "pass123", "ss base64 userinfo");
Check(nodes[9].User == "user:pw", "socks base64 логин");

// Подписка: стабильные ключи, выключенные серверы переживают обновление, новые можно выключать сразу.
Console.WriteLine("Подписки:");
SubscriptionInfo s1 = new SubscriptionInfo { Url = "https://panel.example.com/sub/abc" };
Subscriptions.Apply(s1, new Subscriptions.Fetched { Servers = LinkParser.ParseMany(sub, out _) });
string offKey = s1.Servers[2].Key;
s1.Excluded.Add(offKey);
s1.NewServersOff = true;
List<string> more = links.ToList();
more.Add("trojan://p2@new.example.com:443?security=tls#New");
var (added, removed) = Subscriptions.Apply(s1, new Subscriptions.Fetched { Servers = LinkParser.ParseMany(string.Join("\n", more), out _) });
Check(added == 1 && removed == 0, $"обновление: +{added} -{removed}");
Check(s1.Excluded.Contains(offKey), "выключенный сервер остался выключенным");
Check(s1.Excluded.Contains(s1.Servers.Last().Key), "новый сервер сразу выключен (NewServersOff)");
Check(s1.Active.Count() == links.Length - 1, "активных: " + s1.Active.Count());

Settings settings = new Settings();
settings.Subscriptions.Add(s1);
settings.Groups.Add(new ServerGroup { Name = "Игры", Members = { s1.Servers[0].Key, s1.Servers[7].Key, offKey } });
Check(settings.InScope(ScopeKind.Group, settings.Groups[0].Id).Count == 2, "группа: выключенные не попадают в авто-выбор");
Check(settings.InScope(ScopeKind.Subscription, s1.Id).Count == links.Length - 1, "область подписки");

settings.Tunnel.Rules.AddRange(new[]
{
    new RouteRule { Kind = RuleKind.App, Value = "Discord.exe", Action = RouteAction.Vpn },
    new RouteRule { Kind = RuleKind.App, Value = @"C:\Games\Steam\steam.exe", Action = RouteAction.Direct },
    new RouteRule { Kind = RuleKind.App, Value = "spyware.exe", Action = RouteAction.Block },
    new RouteRule { Kind = RuleKind.Domain, Value = "https://www.YouTube.com/watch?v=1", Action = RouteAction.Vpn },
    new RouteRule { Kind = RuleKind.Domain, Value = "google.com", Action = RouteAction.Vpn },
    new RouteRule { Kind = RuleKind.Domain, Value = "mail.google.com", Action = RouteAction.Direct },
    new RouteRule { Kind = RuleKind.Domain, Value = "сайт.рф", Action = RouteAction.Direct },
    new RouteRule { Kind = RuleKind.Ip, Value = "8.8.8.8", Action = RouteAction.Vpn },
    new RouteRule { Kind = RuleKind.Ip, Value = "10.20.0.0/16", Action = RouteAction.Direct }
});
settings.Tunnel.BlockAds = true;

Console.WriteLine("Конфиги:");
var variants = new (string name, CoreKind core, bool tun, bool proxy, bool probe, bool frag, RouteAction def)[]
{
    ("singbox-tun", CoreKind.SingBox, true, false, false, false, RouteAction.Vpn),
    ("singbox-mixed", CoreKind.SingBox, true, true, false, false, RouteAction.Direct),
    ("xray-tun", CoreKind.Xray, true, false, false, true, RouteAction.Vpn),
    ("xray-proxy", CoreKind.Xray, false, true, false, false, RouteAction.Vpn),
    ("probe-singbox", CoreKind.SingBox, false, false, true, false, RouteAction.Vpn),
    ("probe-xray", CoreKind.Xray, false, false, true, false, RouteAction.Vpn)
};

foreach (var v in variants)
{
    settings.Core = v.core;
    settings.UseTun = v.tun;
    settings.UseSystemProxy = v.proxy;
    settings.Tunnel.Default = v.def;

    Plan plan = ConfigBuilder.Build(settings, s1.Active, s1.Servers[0].Key, new ConfigBuilder.Options { ProbeOnly = v.probe, XrayFragment = v.frag });
    File.WriteAllText(Path.Combine(outDir, v.name + ".singbox.json"), plan.SingBoxJson);

    if (plan.XrayJson != null)
    {
        File.WriteAllText(Path.Combine(outDir, v.name + ".xray.json"), plan.XrayJson);
    }

    int viaXray = plan.Nodes.Count(n => n.Engine == Engine.Xray);
    Check(plan.Nodes.Count == links.Length - 1 && plan.Skipped.Count == 0,
        $"{v.name}: серверов {plan.Nodes.Count}, через Xray {viaXray}, пропущено {plan.Skipped.Count}");

    if (v.core == CoreKind.SingBox)
    {
        // В режиме sing-box xhttp всё равно должен уйти в Xray.
        Check(viaXray == 1 && plan.Nodes.Single(n => n.Engine == Engine.Xray).Server.Transport == "xhttp", v.name + ": xhttp ушёл в Xray");
    }
    else
    {
        Check(plan.Nodes.Where(n => n.Server.Protocol is "hysteria2" or "tuic").All(n => n.Engine == Engine.SingBox), v.name + ": Hy2/TUIC остались на sing-box");
    }

    Check(plan.InitialTag == "n0", v.name + ": стартовый сервер");
}

Check(ConfigBuilder.NormalizeDomain("https://www.YouTube.com/watch") == "youtube.com", "нормализация домена");
Check(ConfigBuilder.NormalizeDomain("сайт.рф") == "xn--80aswg.xn--p1ai", "кириллический домен → punycode");

Console.WriteLine(failures == 0 ? "Всё в порядке." : $"Ошибок: {failures}");
return failures == 0 ? 0 : 1;
