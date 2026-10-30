using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NeonVpn.Core;

// Живая проверка без окна и без TUN (режим «системный прокси», но сам системный прокси не трогаем).
// Нужно: <app>/core/sing-box и <app>/core/xray, запущенные `xray run -c server.json` и `python3 target.py cert.pem key.pem`
// (самоподписанный сертификат на test.local, а SSL_CERT_FILE указывает на набор с ним — Go и .NET ему доверятся),
// и строка «127.0.0.1 test.local» в hosts.
// Использование: LiveCheck <папка app>

string app = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
Paths.Init(app, Path.Combine(app, "data"));
File.Delete(Paths.Settings);

int failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    if (!ok) failures++;
}

const string Uuid = "6f1c3a52-1b1e-4a5a-9c39-1a2b3c4d5e6f";
const string Target = "http://test.local:18080"; // не localhost: .NET и curl к локальным адресам ходят мимо прокси

Settings s = new Settings
{
    UseTun = false,
    UseSystemProxy = true,
    ProxyPort = 18808,
    TestUrl = "https://test.local:18443/generate_204", // sing-box пингует только по https
    LocalDns = "system", // test.local есть только в hosts
    AutoTolerance = 0,
    Tunnel = new TunnelSettings { LanDirect = false, RussiaDirect = false }
};

Subscriptions.AddManual(s, string.Join("\n",
    $"vless://{Uuid}@127.0.0.1:30001?type=tcp&security=none#VLESS-tcp",
    "vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{{\"ps\":\"VMess-ws\",\"add\":\"127.0.0.1\",\"port\":\"30002\",\"id\":\"{Uuid}\",\"net\":\"ws\",\"path\":\"/ws\",\"tls\":\"\"}}")),
    "ss://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:pass123")) + "@127.0.0.1:30003#SS",
    $"vless://{Uuid}@127.0.0.1:30004?type=xhttp&security=none&path=%2Fxh&mode=packet-up#VLESS-xhttp",
    $"vless://{Uuid}@127.0.0.1:30999?type=tcp&security=none#Dead"));

var all = s.Manual().Servers;
string Key(string name) => all.First(x => x.Name == name).Key;
Check(all.Count == 5, "5 тестовых серверов");

VpnService vpn = new VpnService(s, null, new[] { "LiveCheck" }) { SettingsPath = Paths.Settings };
vpn.Notice += m => Console.WriteLine("    [сообщение] " + m);
vpn.AutoSwitched += (a, b, why) => Console.WriteLine($"    [авто] {a?.Name} → {b?.Name} ({why})");

HttpClient viaProxy = new HttpClient(new HttpClientHandler { Proxy = new WebProxy("http://127.0.0.1:18808"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(10) };

async Task<bool> Fetch(string path = "/generate_204")
{
    try
    {
        using var r = await viaProxy.GetAsync(Target + path);
        if (path.StartsWith("/big") || path.StartsWith("/slow")) await r.Content.ReadAsByteArrayAsync();
        return r.IsSuccessStatusCode;
    }
    catch (Exception e)
    {
        Console.WriteLine("    запрос: " + e.Message);
        return false;
    }
}

foreach (CoreKind core in new[] { CoreKind.SingBox, CoreKind.Xray })
{
    s.Core = core;
    Console.WriteLine($"== ядро {core}");

    // 1. Пинг без подключения — через «тихое» ядро.
    var pings = await vpn.PingAsync(all);
    Console.WriteLine("    пинги: " + string.Join(", ", all.Select(x => $"{x.Name}={Format.Ping(pings[x.Key])}")));
    Check(pings[Key("Dead")] == null, "мёртвый сервер не отвечает");
    Check(all.Where(x => x.Name != "Dead").All(x => pings[x.Key] != null), "живые отвечают (VLESS, VMess-ws, SS, xhttp)");

    // 2. «Лучший» — подключает и фиксирует.
    await vpn.ConnectBestAsync(ScopeKind.All, "");
    Check(vpn.State == VpnState.On && vpn.Current != null && vpn.Current.Name != "Dead", "«Лучший»: подключено к " + vpn.Current?.Name);
    Check(!s.Target.Auto && s.Target.PickedAsBest && s.Target.ServerKey == vpn.Current?.Key, "«Лучший» зафиксирован");
    Check(await Fetch(), "запрос через VPN проходит");
    Check(await Fetch("/big"), "скачивание 3 МБ через VPN");
    await Task.Delay(2500);
    Check(vpn.Stats.Vpn.TotalDown > 2_000_000, $"счётчик VPN: {Format.Size(vpn.Stats.Vpn.TotalDown)} (напрямую {Format.Size(vpn.Stats.Direct.TotalDown)}, работает {vpn.Stats.Uptime})");

    // 3. Раздельный трафик: тот же адрес, но правило «напрямую».
    s.Tunnel.Rules = new() { new RouteRule { Kind = RuleKind.Domain, Value = "test.local", Action = RouteAction.Direct } };
    string before = vpn.Current.Key;
    await vpn.ReconnectAsync();
    Check(vpn.Current?.Key == before, "после применения туннелей сервер тот же");
    Check(await Fetch("/slow"), "скачивание напрямую (3 МБ за 3 с)");
    await Task.Delay(2500);
    Check(vpn.Stats.Direct.TotalDown > 2_000_000 && vpn.Stats.Vpn.TotalDown < 500_000,
        $"раздельный счёт: напрямую {Format.Size(vpn.Stats.Direct.TotalDown)}, VPN {Format.Size(vpn.Stats.Vpn.TotalDown)}");
    s.Tunnel.Rules.Clear();
    await vpn.ReconnectAsync();

    // 4. Ручной выбор — переключение без переподключения.
    await vpn.SelectServerAsync(Key("SS"));
    Check(vpn.Current?.Name == "SS" && !s.Target.Auto, "ручной выбор SS");
    Check(await Fetch(), "SS работает");
    await vpn.SelectServerAsync(Key("VLESS-xhttp"));
    Check(vpn.Current?.Name == "VLESS-xhttp" && await Fetch(), "xhttp работает (" + core + " → Xray)");

    // 5. «Авто» по группе: в группе мёртвый и VMess — должен уйти на VMess.
    ServerGroup g = new ServerGroup { Name = "Тест", Members = { Key("Dead"), Key("VMess-ws") } };
    s.Groups.Clear();
    s.Groups.Add(g);
    await vpn.SetAutoAsync(ScopeKind.Group, g.Id);
    await Task.Delay(500);
    Check(s.Target.Auto && vpn.Current?.Name == "VMess-ws", "«Авто» в группе выбрал " + vpn.Current?.Name);

    // 6. Пинг и потери по обоим путям.
    await Task.Delay(7000);
    Check(vpn.Stats.Vpn.Ping != null && vpn.Stats.Vpn.Loss == 0, $"VPN: пинг {Format.Ping(vpn.Stats.Vpn.Ping)}, потери {vpn.Stats.Vpn.Loss}%");
    Check(vpn.Stats.Direct.Ping != null, $"напрямую: пинг {Format.Ping(vpn.Stats.Direct.Ping)}, потери {vpn.Stats.Direct.Loss}%");

    // 7. Падение ядра — само поднимается.
    foreach (Process p in Process.GetProcessesByName("sing-box"))
    {
        try
        {
            if (File.ReadAllText($"/proc/{p.Id}/cmdline").Contains("main.singbox")) p.Kill();
        }
        catch
        {
        }
    }

    for (int i = 0; i < 40 && vpn.State != VpnState.On; i++) await Task.Delay(250);
    await Task.Delay(4000);
    Check(vpn.State == VpnState.On && await Fetch(), "после падения ядра переподключилось само");

    await vpn.DisconnectAsync();
    Check(vpn.State == VpnState.Off && !await Fetch(), "отключено — прокси закрыт");
}

await vpn.ShutdownAsync();
Console.WriteLine(failures == 0 ? "Живая проверка пройдена." : $"Ошибок: {failures}");
return failures == 0 ? 0 : 1;
