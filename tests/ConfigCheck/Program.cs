using System;
using System.IO;
using System.Linq;
using VPN.Core;

// Строит конфиг из образцов ссылок (ключи фейковые) и сохраняет его — дальше его проверяет `sing-box check`.
internal static class Program
{
    private static int Main(string[] args)
    {
        Paths.RuleSets = Path.GetFullPath(args[0]);
        string output = args[1];

        string vmess = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            "{\"v\":\"2\",\"ps\":\"VMess WS\",\"add\":\"vm.example.com\",\"port\":\"443\",\"id\":\"11111111-2222-3333-4444-555555555555\",\"aid\":\"0\",\"net\":\"ws\",\"path\":\"/ws\",\"host\":\"vm.example.com\",\"tls\":\"tls\",\"sni\":\"vm.example.com\"}"));

        string links = string.Join("\n",
            "vless://11111111-2222-3333-4444-555555555555@de.example.com:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk=Fe7xJ0Y5rB9u9F0cXbJmC0wq0bW8x1ZQ1yLp3m4n5o0&sid=abcd1234&flow=xtls-rprx-vision&type=tcp#%F0%9F%87%A9%F0%9F%87%AA%20Germany",
            "vless://11111111-2222-3333-4444-555555555555@nl.example.com:8443?security=tls&sni=nl.example.com&type=grpc&serviceName=grpc#🇳🇱 Netherlands gRPC",
            "trojan://secret@tr.example.com:443?type=ws&path=%2Ftr&host=tr.example.com&sni=tr.example.com#Trojan WS",
            "vmess://" + vmess,
            "ss://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:pass123")) + "@ss.example.com:8388#SS",
            "hysteria2://pass@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=xyz&insecure=1#Hy2",
            "tuic://11111111-2222-3333-4444-555555555555:pw@tuic.example.com:443?congestion_control=bbr&alpn=h3&sni=tuic.example.com#TUIC");

        var result = new SubscriptionResult();
        Subscription.Parse(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(links)), result);
        Console.WriteLine("Parsed servers: " + result.Servers.Count + ", skipped: " + result.Skipped);

        foreach (ServerProfile s in result.Servers)
        {
            Console.WriteLine($"  {s.Tag} [{s.Protocol}] {Subscription.CountryCode(s.Name)} {Subscription.CleanName(s.Name)} -> {s.Host}:{s.Port}");
        }

        if (result.Servers.Count != 7)
        {
            Console.Error.WriteLine("Expected 7 servers");
            return 1;
        }

        var settings = new AppSettings
        {
            Servers = result.Servers,
            SelectedServer = "s1",
            DefaultViaVpn = true,
            RussiaDirect = true,
            BlockAds = true,
            Rules =
            {
                new RouteRule { Kind = RuleKind.App, Value = "Discord.exe", ViaVpn = true },
                new RouteRule { Kind = RuleKind.App, Value = "chrome.exe", ViaVpn = false },
                new RouteRule { Kind = RuleKind.Site, Value = "https://www.YouTube.com/watch?v=1", ViaVpn = true },
                new RouteRule { Kind = RuleKind.Site, Value = "vk.com", ViaVpn = false }
            }
        };

        string full = ConfigBuilder.Build(settings, "testsecret", 19091);

        if (!full.Contains("\"probe-in\""))
        {
            Console.Error.WriteLine("Probe inbound missing");
            return 1;
        }

        File.WriteAllText(output, full);

        // Второй вариант: «всё напрямую, через VPN только выбранное» — тоже должен быть валидным.
        settings.DefaultViaVpn = false;
        settings.BlockAds = false;
        File.WriteAllText(Path.ChangeExtension(output, ".direct.json"), ConfigBuilder.Build(settings, "testsecret"));

        Console.WriteLine("Domain check: " + ConfigBuilder.NormalizeDomain("https://www.YouTube.com/watch?v=1"));
        return ConfigBuilder.NormalizeDomain("https://www.YouTube.com/watch?v=1") == "youtube.com" ? 0 : 1;
    }
}
