using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace NeonVpn.Core
{
    /// <summary>Каким ядром ходить к серверам.</summary>
    public enum CoreKind
    {
        SingBox,
        Xray
    }

    /// <summary>Куда отправлять трафик по правилу.</summary>
    public enum RouteAction
    {
        Vpn,
        Direct,
        Block
    }

    public enum RuleKind
    {
        /// <summary>Программа: имя .exe (chrome.exe) или полный путь.</summary>
        App,

        /// <summary>Сайт: домен вместе с поддоменами (youtube.com).</summary>
        Domain,

        /// <summary>IP или подсеть (1.2.3.4, 10.0.0.0/8).</summary>
        Ip
    }

    /// <summary>Область, среди которой «Авто» выбирает сервер.</summary>
    public enum ScopeKind
    {
        All,
        Subscription,
        Group
    }

    /// <summary>Один сервер в нейтральном виде: из него собираются и outbound sing-box, и outbound Xray.</summary>
    public sealed class ServerNode
    {
        /// <summary>vless, vmess, trojan, shadowsocks, hysteria2, tuic, socks, http.</summary>
        public string Protocol { get; set; } = "";
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }

        /// <summary>uuid / пароль / логин — то, что в ссылке стоит до @.</summary>
        public string User { get; set; } = "";

        /// <summary>Все параметры ссылки (security, type, sni, pbk, sid, fp, path, host, flow…) — ключи без учёта регистра.</summary>
        public Dictionary<string, string> Params { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Исходная ссылка — чтобы можно было скопировать сервер обратно.</summary>
        public string Link { get; set; } = "";

        /// <summary>Стабильный ключ: не меняется при обновлении подписки, если сам сервер тот же.</summary>
        public string Key { get; set; } = "";

        /// <summary>Подписка, откуда сервер (Id), «manual» — добавлен ссылкой вручную.</summary>
        public string SourceId { get; set; } = "";

        public string P(string key, string fallback = "") =>
            Params.TryGetValue(key, out string v) && !string.IsNullOrEmpty(v) ? v : fallback;

        [JsonIgnore]
        public string Country => Names.CountryCode(Name);

        [JsonIgnore]
        public string DisplayName => Names.Clean(Name).Length > 0 ? Names.Clean(Name) : Host;

        [JsonIgnore]
        public string Transport => P("type", "tcp").ToLowerInvariant();

        [JsonIgnore]
        public string Security => P("security", Protocol == "trojan" ? "tls" : "none").ToLowerInvariant();

        /// <summary>Короткое описание для списка: «VLESS · Reality · tcp».</summary>
        [JsonIgnore]
        public string Summary
        {
            get
            {
                List<string> parts = new List<string> { ProtocolLabel };

                if (Security == "reality") parts.Add("Reality");
                else if (Security == "tls") parts.Add("TLS");

                if (Protocol is "vless" or "vmess" or "trojan") parts.Add(Transport);
                return string.Join(" · ", parts);
            }
        }

        [JsonIgnore]
        public string ProtocolLabel => Protocol switch
        {
            "vless" => "VLESS",
            "vmess" => "VMess",
            "trojan" => "Trojan",
            "shadowsocks" => "SS",
            "hysteria2" => "Hy2",
            "tuic" => "TUIC",
            "socks" => "SOCKS",
            "http" => "HTTP",
            _ => Protocol.ToUpperInvariant()
        };

        public void ComputeKey()
        {
            string id = string.Join("|", Protocol, Host.ToLowerInvariant(), Port, User, P("type"), P("path"), P("serviceName"), P("sni"));
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
            Key = SourceId + ":" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }
    }

    /// <summary>Подписка. Серверы, которые пользователь убрал, помнятся по ключу и не возвращаются при обновлении.</summary>
    public sealed class SubscriptionInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public bool Enabled { get; set; } = true;

        /// <summary>Ключи серверов, которые пользователь выключил (галочка снята).</summary>
        public HashSet<string> Excluded { get; set; } = new HashSet<string>();

        /// <summary>Если true — новые серверы, пришедшие с обновлением подписки, сразу выключены (берём только то, что выбрал сам).</summary>
        public bool NewServersOff { get; set; }

        public List<ServerNode> Servers { get; set; } = new List<ServerNode>();

        public DateTime? Updated { get; set; }
        public long Used { get; set; }
        public long Total { get; set; }
        public long Expire { get; set; }
        public string Error { get; set; } = "";

        /// <summary>Интервал автообновления (часы), 0 — не обновлять.</summary>
        public int UpdateHours { get; set; } = 12;

        public IEnumerable<ServerNode> Active => Enabled ? Servers.Where(s => !Excluded.Contains(s.Key)) : Enumerable.Empty<ServerNode>();
    }

    /// <summary>Своя группа серверов — набор ключей из любых подписок.</summary>
    public sealed class ServerGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name { get; set; } = "";
        public List<string> Members { get; set; } = new List<string>();
    }

    public sealed class RouteRule
    {
        public RuleKind Kind { get; set; }
        public string Value { get; set; } = "";
        public RouteAction Action { get; set; } = RouteAction.Vpn;
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Что подключать. Либо конкретный сервер (Fixed), либо «Авто» среди области.
    /// Кнопка «Лучший» превращает выбор в Fixed: находит лучший и больше его не меняет.
    /// </summary>
    public sealed class Target
    {
        public bool Auto { get; set; } = true;
        public ScopeKind Scope { get; set; } = ScopeKind.All;

        /// <summary>Id подписки или группы для Scope = Subscription/Group.</summary>
        public string ScopeId { get; set; } = "";

        /// <summary>Ключ сервера при Auto = false.</summary>
        public string ServerKey { get; set; } = "";

        /// <summary>Сервер выбран кнопкой «Лучший» — показываем значок фиксации.</summary>
        public bool PickedAsBest { get; set; }

        public Target Clone() => (Target)MemberwiseClone();
    }

    /// <summary>Настройки раздельного туннелирования.</summary>
    public sealed class TunnelSettings
    {
        /// <summary>Что делать со всем, что не попало в правила.</summary>
        public RouteAction Default { get; set; } = RouteAction.Vpn;

        public List<RouteRule> Rules { get; set; } = new List<RouteRule>();

        /// <summary>Российские сайты и IP — напрямую (geosite/geoip ru, зоны .ru .su .рф).</summary>
        public bool RussiaDirect { get; set; } = true;

        public bool BlockAds { get; set; }

        /// <summary>Локальная сеть (роутер, принтер, NAS) — всегда напрямую.</summary>
        public bool LanDirect { get; set; } = true;
    }
}
