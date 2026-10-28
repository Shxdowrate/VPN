using System;
using System.IO;
using System.Linq;
using System.Text;
using VPN.Core;

// Строит конфиг из образцов ссылок (ключи фейковые) и сохраняет его — дальше его проверяет `sing-box check`.
// Заодно проверяет разбор ссылок для импорта и перенос старых настроек.
internal static class Program
{
    private static int failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);

        if (!ok)
        {
            failures++;
        }
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static int Main(string[] args)
    {
        Paths.RuleSets = Path.GetFullPath(args[0]);
        // Скачанных наборов в проверке нет — пусть смотрит в пустую папку.
        Paths.UserRuleSets = Path.Combine(Path.GetTempPath(), "vpn-check-rules-" + Guid.NewGuid().ToString("N"));
        string output = args[1];

        string vmess = B64("{\"v\":\"2\",\"ps\":\"VMess WS\",\"add\":\"vm.example.com\",\"port\":\"443\",\"id\":\"11111111-2222-3333-4444-555555555555\",\"aid\":\"0\",\"net\":\"ws\",\"path\":\"/ws\",\"host\":\"vm.example.com\",\"tls\":\"tls\",\"sni\":\"vm.example.com\"}");

        string links = string.Join("\n",
            "vless://11111111-2222-3333-4444-555555555555@de.example.com:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk=Fe7xJ0Y5rB9u9F0cXbJmC0wq0bW8x1ZQ1yLp3m4n5o0&sid=abcd1234&flow=xtls-rprx-vision&type=tcp#%F0%9F%87%A9%F0%9F%87%AA%20Germany",
            "vless://11111111-2222-3333-4444-555555555555@nl.example.com:8443?security=tls&sni=nl.example.com&type=grpc&serviceName=grpc#🇳🇱 Netherlands gRPC",
            "trojan://secret@tr.example.com:443?type=ws&path=%2Ftr&host=tr.example.com&sni=tr.example.com#Trojan WS",
            "vmess://" + vmess,
            "ss://" + B64("chacha20-ietf-poly1305:pass123") + "@ss.example.com:8388#SS",
            "hysteria2://pass@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=xyz&insecure=1#Hy2",
            "tuic://11111111-2222-3333-4444-555555555555:pw@tuic.example.com:443?congestion_control=bbr&alpn=h3&sni=tuic.example.com#TUIC",
            "socks://" + B64("user:pw") + "@socks.example.com:1080#Socks");

        var result = new SubscriptionResult();
        Subscription.Parse(B64(links), result);
        Console.WriteLine("Parsed servers: " + result.Servers.Count + ", skipped: " + result.Skipped);

        foreach (ServerProfile s in result.Servers)
        {
            Console.WriteLine($"  {s.Tag} [{s.Protocol}] {Subscription.CountryCode(s.Name)} {Subscription.CleanName(s.Name)} -> {s.Host}:{s.Port}");
        }

        Check(result.Servers.Count == 8, "8 servers parsed");

        // --- две подписки + ручной сервер ---
        var settings = new AppSettings();
        var subA = new SubscriptionEntry { Url = "https://a.example.com/sub/1", Servers = result.Servers.Take(4).ToList() };
        var subB = new SubscriptionEntry { Url = "https://b.example.com/sub/2", Servers = result.Servers.Skip(4).Take(3).ToList() };
        subA.Retag();
        subB.Retag();
        settings.Subscriptions.Add(subA);
        settings.Subscriptions.Add(subB);
        SubscriptionEntry manual = settings.Manual();
        manual.Servers.Add(result.Servers[7]);
        manual.Retag();
        settings.Flatten();
        Check(settings.Servers.Count == 8, "flatten: 8 servers");
        Check(settings.Servers.Select(s => s.Tag).Distinct().Count() == 8, "tags unique across subscriptions");
        Check(manual.Servers[0].Tag == "my-1", "manual tag is my-1");

        settings.SelectedServer = subA.Servers[0].Tag;
        settings.DefaultViaVpn = true;
        settings.RussiaDirect = true;
        settings.BlockAds = true;
        settings.Rules.Add(new RouteRule { Kind = RuleKind.App, Value = "Discord.exe", ViaVpn = true });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.App, Value = "chrome.exe", ViaVpn = false });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.App, Value = "spyware.exe", Block = true });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.Site, Value = "https://www.YouTube.com/watch?v=1", ViaVpn = true });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.Site, Value = "vk.com", ViaVpn = false });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.Site, Value = "пример.рф", ViaVpn = false });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.Site, Value = "ads.example.com", Block = true });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.Ip, Value = "91.108.4.0/22", ViaVpn = true });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.Ip, Value = "8.8.8.8", ViaVpn = false });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.RuleSet, Value = "geosite-category-ru", ViaVpn = false });
        settings.Rules.Add(new RouteRule { Kind = RuleKind.RuleSet, Value = "geoip-ru", ViaVpn = false });
        // Такого набора на диске нет — должен тихо пропасть, а не уронить движок.
        settings.Rules.Add(new RouteRule { Kind = RuleKind.RuleSet, Value = "geosite-youtube", ViaVpn = true });

        string full = ConfigBuilder.Build(settings, "testsecret", 19091);
        Check(full.Contains("\"probe-in\""), "probe inbound present");
        Check(full.Contains("xn--e1afmkfd.xn--p1ai"), "cyrillic domain converted to punycode");
        Check(full.Contains("91.108.4.0/22") && full.Contains("8.8.8.8/32"), "ip rules present");
        Check(full.Contains("spyware.exe") && full.Contains("ads.example.com"), "block rules present");
        Check(!full.Contains("geosite-youtube"), "missing rule set skipped");
        Check(full.Contains("\"my-1\""), "manual server in config");
        File.WriteAllText(output, full);

        // Второй вариант: «всё напрямую, через VPN только выбранное» — тоже должен быть валидным.
        settings.DefaultViaVpn = false;
        settings.BlockAds = false;
        File.WriteAllText(Path.ChangeExtension(output, ".direct.json"), ConfigBuilder.Build(settings, "testsecret"));

        Check(ConfigBuilder.NormalizeDomain("https://www.YouTube.com/watch?v=1") == "youtube.com", "domain normalize");

        // --- импорт ссылок ---
        ImportResult r1 = LinkImport.Classify("happ://add/https%3A%2F%2Fpanel.example.com%2Fsub%2Fabc");
        Check(r1.Kind == ImportKind.Subscription && r1.Url == "https://panel.example.com/sub/abc", "happ://add/ → subscription");

        ImportResult r2 = LinkImport.Classify("sing-box://import-remote-profile?url=https%3A%2F%2Fx.example.com%2Fs%2F1#name");
        Check(r2.Kind == ImportKind.Subscription && r2.Url == "https://x.example.com/s/1", "sing-box:// → subscription");

        ImportResult r3 = LinkImport.Classify(links.Split('\n')[0]);
        Check(r3.Kind == ImportKind.Servers && r3.Servers.Servers.Count == 1, "single vless link → server");

        ImportResult r4 = LinkImport.Classify(B64(links));
        Check(r4.Kind == ImportKind.Servers && r4.Servers.Servers.Count == 8, "base64 list → 8 servers");

        Check(LinkImport.Classify("happ://crypt3/AAAA").Kind == ImportKind.Encrypted, "happ://crypt → encrypted");
        Check(LinkImport.Classify("hello world").Kind == ImportKind.None, "garbage → none");

        string routingJson = "{\"Name\":\"Игры\",\"GlobalProxy\":\"false\",\"DirectSites\":[\"geosite:ru\",\"domain:steamcontent.com\"]," +
                             "\"ProxySites\":[\"geosite:youtube\",\"discord.com\",\"regexp:.*\\\\.ru$\"],\"BlockSites\":[\"geosite:category-ads-all\"]," +
                             "\"ProxyIp\":[\"91.108.56.0/22\"],\"DirectIp\":[\"geoip:ru\"]}";
        ImportResult r5 = LinkImport.Classify("happ://routing/onadd/" + B64(routingJson));
        Check(r5.Kind == ImportKind.Routing, "happ://routing → routing profile");
        RoutingProfile p = r5.Profile;
        Check(p != null && p.Name == "Игры" && !p.DefaultViaVpn && p.RussiaDirect && p.BlockAds, "routing flags (name, global, ru, ads)");
        Check(p != null && p.Rules.Any(x => x.Kind == RuleKind.RuleSet && x.Value == "geosite-youtube" && x.ViaVpn), "geosite:youtube → rule set via VPN");
        Check(p != null && p.Rules.Any(x => x.Kind == RuleKind.Site && x.Value == "steamcontent.com" && !x.ViaVpn), "domain: → direct site");
        Check(p != null && p.Rules.Any(x => x.Kind == RuleKind.Ip && x.Value == "91.108.56.0/22" && x.ViaVpn), "ProxyIp → ip rule");
        Check(r5.Skipped == 1, "regexp entry skipped");

        Check(LinkImport.GuessRule("Discord.exe")?.Kind == RuleKind.App, "guess: exe → app");
        Check(LinkImport.GuessRule("C:\\Games\\game.exe")?.Value == "game.exe", "guess: full path → exe name");
        Check(LinkImport.GuessRule("1.1.1.1")?.Value == "1.1.1.1/32", "guess: ip");
        Check(LinkImport.GuessRule("geosite:twitch")?.Value == "geosite-twitch", "guess: geosite");
        Check(LinkImport.GuessRule("https://www.twitch.tv/x")?.Value == "twitch.tv", "guess: url → site");
        Check(LinkImport.GuessRule("not a site") == null, "guess: junk → null");

        Check(LinkImport.LooksImportable("vless://abc@h:1"), "clipboard: vless looks importable");
        Check(!LinkImport.LooksImportable("https://www.youtube.com/watch?v=1"), "clipboard: plain web link ignored");
        Check(LinkImport.LooksImportable("https://panel.example.com/sub/abcdef"), "clipboard: subscription link offered");

        // --- перенос старых настроек ---
        string legacy = "{\"SubscriptionUrl\":\"https://old.example.com/sub\",\"SelectedServer\":\"s2\",\"DefaultViaVpn\":false,\"RussiaDirect\":true," +
                        "\"BlockAds\":true,\"Rules\":[{\"Kind\":\"Site\",\"Value\":\"vk.com\",\"Path\":\"\",\"ViaVpn\":false}],\"SubscriptionTitle\":\"Old\"," +
                        "\"TrafficUsed\":5,\"TrafficTotal\":10,\"ExpireUnix\":1900000000,\"WindowLeft\":\"NaN\",\"WindowWidth\":372," +
                        "\"Servers\":[{\"Tag\":\"s1\",\"Name\":\"A\",\"Protocol\":\"vless\",\"Host\":\"a\",\"Port\":1,\"Outbound\":\"{}\"}," +
                        "{\"Tag\":\"s2\",\"Name\":\"B\",\"Protocol\":\"vless\",\"Host\":\"b\",\"Port\":2,\"Outbound\":\"{}\"}]}";
        AppSettings migrated = AppSettings.FromJson(legacy);
        Check(migrated.Subscriptions.Count == 1 && migrated.Subscriptions[0].Url == "https://old.example.com/sub", "migration: subscription kept");
        Check(migrated.Servers.Count == 2, "migration: servers kept");
        Check(migrated.Servers.FirstOrDefault(x => x.Tag == migrated.SelectedServer)?.Name == "B", "migration: selected server kept");
        Check(!migrated.DefaultViaVpn && migrated.BlockAds && migrated.Rules.Count == 1, "migration: rules moved to profile");
        Check(migrated.Subscriptions[0].Title == "Old" && migrated.Subscriptions[0].Total == 10, "migration: traffic info kept");

        AppSettings again = AppSettings.FromJson(migrated.ToJson());
        Check(again.Servers.Count == 2 && again.Rules.Count == 1 && again.SelectedServer == migrated.SelectedServer, "round-trip of new format");
        Check(again.Servers.All(x => x.Group == again.Subscriptions[0].Id), "group restored after load");

        Console.WriteLine(failures == 0 ? "All checks passed" : failures + " check(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
