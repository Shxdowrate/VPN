using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using NeonVpn.Core;

namespace NeonVpn
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Показатели одного пути (VPN или напрямую) для окна.</summary>
    public sealed class PathVm : Observable
    {
        private string down = "—", up = "—", ping = "—", pingAvg = "—", jitter = "—", loss = "—", total = "—", conns = "0";
        private double[] downHist = Array.Empty<double>(), upHist = Array.Empty<double>();
        private Color pingColor = Colors.Gray, lossColor = Colors.Gray;

        public string Down { get => down; set => Set(ref down, value); }
        public string Up { get => up; set => Set(ref up, value); }
        public string Ping { get => ping; set => Set(ref ping, value); }
        public string PingAvg { get => pingAvg; set => Set(ref pingAvg, value); }
        public string Jitter { get => jitter; set => Set(ref jitter, value); }
        public string Loss { get => loss; set => Set(ref loss, value); }
        public string Total { get => total; set => Set(ref total, value); }
        public string Conns { get => conns; set => Set(ref conns, value); }
        public double[] DownHist { get => downHist; set => Set(ref downHist, value); }
        public double[] UpHist { get => upHist; set => Set(ref upHist, value); }
        public Color PingColor { get => pingColor; set => Set(ref pingColor, value); }
        public Color LossColor { get => lossColor; set => Set(ref lossColor, value); }

        public void From(PathStats s)
        {
            Down = Format.Speed(s.Down);
            Up = Format.Speed(s.Up);
            Ping = s.Ping == null ? (s.Loss == null ? "—" : "нет ответа") : s.Ping + " мс";
            PingAvg = s.PingAvg == null ? "—" : s.PingAvg + " мс";
            Jitter = s.Jitter == null ? "—" : "±" + s.Jitter + " мс";
            Loss = s.Loss == null ? "—" : $"{s.Loss:0.#}%";
            Total = "↓ " + Format.Size(s.TotalDown) + "   ↑ " + Format.Size(s.TotalUp);
            Conns = s.Connections.ToString();
            DownHist = s.DownHistory;
            UpHist = s.UpHistory;
            PingColor = Palette.ForPing(s.Ping, s.Loss != null);
            LossColor = s.Loss == null ? Palette.Dim : s.Loss < 1 ? Palette.Good : s.Loss < 10 ? Palette.Warn : Palette.Bad;
        }

        public void Clear() => From(new PathStats());
    }

    public static class Palette
    {
        public static Color Accent = Color.FromRgb(0x76, 0xFF, 0x03);
        public static Color Accent2 = Color.FromRgb(0x00, 0xE5, 0xFF);
        public static readonly Color Good = Color.FromRgb(0x76, 0xFF, 0x03);
        public static readonly Color Warn = Color.FromRgb(0xFF, 0xC4, 0x00);
        public static readonly Color Bad = Color.FromRgb(0xFF, 0x1F, 0x4B);
        public static readonly Color Dim = Color.FromRgb(0x7D, 0x8A, 0xA6);
        public static readonly Color Off = Color.FromRgb(0x46, 0x50, 0x69);
        public static readonly Color Magenta = Color.FromRgb(0xFF, 0x2B, 0xD6);

        public static Color ForPing(int? ms, bool measured = true) =>
            ms == null ? (measured ? Bad : Dim) : ms < 0 ? Bad : ms < 150 ? Good : ms < 350 ? Warn : Bad;

        public static Color ForState(VpnState s) => s switch
        {
            VpnState.On => Accent,
            VpnState.Connecting or VpnState.Reconnecting => Accent2,
            VpnState.Switching => Magenta,
            VpnState.Error => Bad,
            _ => Off
        };

        public static Color Parse(string hex, Color fallback)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                return fallback;
            }
        }
    }

    /// <summary>Состояние для главного и мини-окна.</summary>
    public sealed class MainVm : Observable
    {
        private readonly VpnService vpn;
        private string stateText = "Отключено", power = "ПОДКЛЮЧИТЬ", serverName = "Сервер не выбран", serverCountry = "", serverSummary = "",
            targetText = "", coreText = "", modeText = "", uptime = "00:00", serverPing = "—";
        private Color stateColor = Palette.Off;
        private bool busy, lit, pinnedBest, isAuto;

        public PathVm Vpn { get; } = new PathVm();
        public PathVm Direct { get; } = new PathVm();

        public string StateText { get => stateText; set => Set(ref stateText, value); }
        public string Power { get => power; set => Set(ref power, value); }
        public Color StateColor { get => stateColor; set => Set(ref stateColor, value); }
        public bool Busy { get => busy; set => Set(ref busy, value); }
        public bool Lit { get => lit; set => Set(ref lit, value); }
        public string ServerName { get => serverName; set => Set(ref serverName, value); }
        public string ServerCountry { get => serverCountry; set => Set(ref serverCountry, value); }
        public string ServerSummary { get => serverSummary; set => Set(ref serverSummary, value); }
        public string ServerPing { get => serverPing; set => Set(ref serverPing, value); }
        public string TargetText { get => targetText; set => Set(ref targetText, value); }
        public bool PinnedBest { get => pinnedBest; set => Set(ref pinnedBest, value); }
        public bool IsAuto { get => isAuto; set => Set(ref isAuto, value); }
        public string CoreText { get => coreText; set => Set(ref coreText, value); }
        public string ModeText { get => modeText; set => Set(ref modeText, value); }
        public string Uptime { get => uptime; set => Set(ref uptime, value); }

        public MainVm(VpnService vpn)
        {
            this.vpn = vpn;
            Vpn.Clear();
            Direct.Clear();
            Refresh();
        }

        public void Refresh()
        {
            Settings s = vpn.Settings;
            StateText = vpn.StateText;
            StateColor = Palette.ForState(vpn.State);
            Busy = vpn.Busy || vpn.State == VpnState.Switching;
            Lit = vpn.State == VpnState.On;
            Power = vpn.IsOn || vpn.Busy ? "ОТКЛЮЧИТЬ" : "ПОДКЛЮЧИТЬ";

            ServerNode shown = vpn.Current ?? (!s.Target.Auto ? s.FindServer(s.Target.ServerKey) : null);
            ServerName = shown?.DisplayName ?? (s.Target.Auto ? "Авто: выберу лучший" : "Сервер не выбран");
            ServerCountry = shown?.Country ?? "";
            ServerSummary = shown == null ? "" : shown.Summary + " · " + (s.Subscriptions.FirstOrDefault(x => x.Id == shown.SourceId)?.Name ?? "");
            int? p = shown != null ? vpn.PingOf(shown.Key) : null;
            ServerPing = p == null ? "—" : p < 0 ? "нет ответа" : p + " мс";

            TargetText = vpn.TargetText;
            PinnedBest = !s.Target.Auto && s.Target.PickedAsBest;
            IsAuto = s.Target.Auto;
            CoreText = s.Core == CoreKind.Xray ? "XRAY" : "SING-BOX";
            ModeText = vpn.ModeText.ToUpperInvariant();
        }

        public void RefreshStats()
        {
            LiveStats st = vpn.Stats;
            Vpn.From(st.Vpn);
            Direct.From(st.Direct);
            Uptime = vpn.IsOn ? Format.Duration(st.Uptime) : "00:00";
        }
    }

    /// <summary>Источник в списке серверов: все, подписка или группа.</summary>
    public sealed class SourceVm : Observable
    {
        private string name = "", info = "", count = "";
        private bool isAutoTarget;

        public ScopeKind Kind { get; init; }
        public string Id { get; init; } = "";
        public string Icon => Kind switch { ScopeKind.All => "", ScopeKind.Subscription => "", _ => "" };
        public string Name { get => name; set => Set(ref name, value); }
        public string Info { get => info; set => Set(ref info, value); }
        public string Count { get => count; set => Set(ref count, value); }

        /// <summary>«Авто» сейчас выбирает именно среди этого источника.</summary>
        public bool IsAutoTarget { get => isAutoTarget; set => Set(ref isAutoTarget, value); }
    }

    public sealed class ServerVm : Observable
    {
        private bool included, isCurrent, isFixed, pinging;
        private string ping = "", groups = "";
        private Color pingColor = Palette.Dim;

        public ServerNode Node { get; init; }
        public string Key => Node.Key;
        public string Name => Node.DisplayName;
        public string Country => Node.Country.Length > 0 ? Node.Country : "··";
        public string Summary { get; init; } = "";
        public string Source { get; init; } = "";

        public bool Included { get => included; set => Set(ref included, value); }
        public bool IsCurrent { get => isCurrent; set => Set(ref isCurrent, value); }
        public bool IsFixed { get => isFixed; set => Set(ref isFixed, value); }
        public bool Pinging { get => pinging; set => Set(ref pinging, value); }
        public string Ping { get => ping; set => Set(ref ping, value); }
        public Color PingColor { get => pingColor; set => Set(ref pingColor, value); }
        public string Groups { get => groups; set => Set(ref groups, value); }

        public void SetPing(int? ms)
        {
            Ping = ms == null ? "" : ms < 0 ? "нет ответа" : ms + " мс";
            PingColor = Palette.ForPing(ms, ms != null);
        }
    }

    public sealed class RuleVm : Observable
    {
        public RouteRule Rule { get; init; }
        public string Icon => Rule.Kind switch { RuleKind.App => "", RuleKind.Domain => "", _ => "" };
        public string Value => Rule.Value;
        public string KindText => Rule.Kind switch { RuleKind.App => "программа", RuleKind.Domain => "сайт", _ => "IP" };

        public string ActionText => Rule.Action switch { RouteAction.Vpn => "VPN", RouteAction.Direct => "НАПРЯМУЮ", _ => "БЛОК" };

        public Color ActionColor => Rule.Action switch
        {
            RouteAction.Vpn => Palette.Accent,
            RouteAction.Direct => Palette.Accent2,
            _ => Palette.Bad
        };

        public void Changed()
        {
            Raise(nameof(ActionText));
            Raise(nameof(ActionColor));
        }
    }
}
