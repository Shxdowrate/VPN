using System;
using System.Collections.Generic;
using System.Linq;

namespace NeonVpn.Core
{
    /// <summary>Пинг и потери по одному «пути» (через VPN или напрямую) за последние N проверок.</summary>
    public sealed class PathMeter
    {
        private readonly Queue<int> samples = new Queue<int>(); // -1 — потеря
        private readonly int window;

        public PathMeter(int window = 20)
        {
            this.window = window;
        }

        public int? Last { get; private set; }
        public int FailStreak { get; private set; }

        public void Add(int? ms)
        {
            lock (samples)
            {
                samples.Enqueue(ms ?? -1);

                while (samples.Count > window)
                {
                    samples.Dequeue();
                }
            }

            Last = ms;
            FailStreak = ms == null ? FailStreak + 1 : 0;
        }

        public void Reset()
        {
            lock (samples)
            {
                samples.Clear();
            }

            Last = null;
            FailStreak = 0;
        }

        /// <summary>Средний пинг по удачным проверкам.</summary>
        public int? Average
        {
            get
            {
                lock (samples)
                {
                    List<int> ok = samples.Where(x => x >= 0).ToList();
                    return ok.Count == 0 ? null : (int)ok.Average();
                }
            }
        }

        /// <summary>Разброс (джиттер): среднее отклонение соседних удачных замеров.</summary>
        public int? Jitter
        {
            get
            {
                lock (samples)
                {
                    List<int> ok = samples.Where(x => x >= 0).ToList();
                    return ok.Count < 2 ? null : (int)ok.Zip(ok.Skip(1), (a, b) => Math.Abs(a - b)).Average();
                }
            }
        }

        /// <summary>Потери, % (0–100). null — ещё нет данных.</summary>
        public double? Loss
        {
            get
            {
                lock (samples)
                {
                    return samples.Count == 0 ? null : 100.0 * samples.Count(x => x < 0) / samples.Count;
                }
            }
        }

        public int Count
        {
            get
            {
                lock (samples)
                {
                    return samples.Count;
                }
            }
        }
    }

    /// <summary>Показатели одного пути — снимок для окна.</summary>
    public sealed class PathStats
    {
        public long Down, Up;               // байт/с сейчас
        public long TotalDown, TotalUp;     // за сессию
        public int? Ping, PingAvg, Jitter;
        public double? Loss;
        public int Connections;
        public double[] DownHistory = Array.Empty<double>();
        public double[] UpHistory = Array.Empty<double>();
    }

    public sealed class LiveStats
    {
        public PathStats Vpn = new PathStats();
        public PathStats Direct = new PathStats();
        public TimeSpan Uptime;
        public static readonly LiveStats Empty = new LiveStats();
    }

    /// <summary>Считает скорость отдельно для VPN и прямого трафика по списку соединений sing-box.</summary>
    public sealed class TrafficSplitter
    {
        private readonly Dictionary<string, (long up, long down, bool direct)> seen = new Dictionary<string, (long, long, bool)>();
        private long lastUpTotal, lastDownTotal; // ядро каждый раз стартует с нуля
        private double vpnShare = 1;
        private readonly int historyLength;

        public long VpnTotalUp, VpnTotalDown, DirectTotalUp, DirectTotalDown;
        public readonly Queue<double> VpnDownHist = new Queue<double>(), VpnUpHist = new Queue<double>(), DirectDownHist = new Queue<double>(), DirectUpHist = new Queue<double>();

        public TrafficSplitter(int historyLength = 60)
        {
            this.historyLength = historyLength;
        }

        /// <summary>
        /// Раскладывает прирост общего счётчика между VPN и «напрямую». Общий счётчик точный (в нём и уже закрытые соединения),
        /// а по живым соединениям видно, какая доля пришлась на каждый путь.
        /// </summary>
        public (long vpnUp, long vpnDown, long directUp, long directDown, int vpnConns, int directConns) Feed(ClashApi.Snapshot snap, double seconds)
        {
            long obsVpnUp = 0, obsVpnDown = 0, obsDirUp = 0, obsDirDown = 0;
            int vpnConns = 0, dirConns = 0;
            HashSet<string> alive = new HashSet<string>();

            foreach (ClashApi.Connection c in snap.Connections)
            {
                alive.Add(c.Id);
                seen.TryGetValue(c.Id, out var prev);
                long du = Math.Max(0, c.Up - prev.up);
                long dd = Math.Max(0, c.Down - prev.down);
                seen[c.Id] = (c.Up, c.Down, c.Direct);

                if (c.Direct)
                {
                    obsDirUp += du;
                    obsDirDown += dd;
                    dirConns++;
                }
                else
                {
                    obsVpnUp += du;
                    obsVpnDown += dd;
                    vpnConns++;
                }
            }

            foreach (string gone in seen.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                seen.Remove(gone);
            }

            long totalUp = Math.Max(0, snap.UploadTotal - lastUpTotal);
            long totalDown = Math.Max(0, snap.DownloadTotal - lastDownTotal);
            lastUpTotal = snap.UploadTotal;
            lastDownTotal = snap.DownloadTotal;

            long obs = obsVpnUp + obsVpnDown + obsDirUp + obsDirDown;

            if (obs > 0)
            {
                vpnShare = (double)(obsVpnUp + obsVpnDown) / obs;
            }

            (long vu, long du2) = Split(totalUp, obsVpnUp, obsDirUp);
            (long vd, long dd2) = Split(totalDown, obsVpnDown, obsDirDown);

            VpnTotalUp += vu;
            VpnTotalDown += vd;
            DirectTotalUp += du2;
            DirectTotalDown += dd2;

            double k = seconds <= 0 ? 1 : 1 / seconds;
            long rvu = (long)(vu * k), rvd = (long)(vd * k), rdu = (long)(du2 * k), rdd = (long)(dd2 * k);

            Push(VpnUpHist, rvu);
            Push(VpnDownHist, rvd);
            Push(DirectUpHist, rdu);
            Push(DirectDownHist, rdd);
            return (rvu, rvd, rdu, rdd, vpnConns, dirConns);
        }

        private (long vpn, long direct) Split(long total, long obsVpn, long obsDirect)
        {
            long obs = obsVpn + obsDirect;

            if (total <= obs || obs == 0)
            {
                return obs == 0 ? ((long)(total * vpnShare), total - (long)(total * vpnShare)) : (obsVpn, obsDirect);
            }

            // То, что прошло через уже закрытые соединения, делим в той же пропорции.
            long extra = total - obs;
            long extraVpn = (long)(extra * ((double)obsVpn / obs));
            return (obsVpn + extraVpn, obsDirect + extra - extraVpn);
        }

        private void Push(Queue<double> q, double v)
        {
            q.Enqueue(v);

            while (q.Count > historyLength)
            {
                q.Dequeue();
            }
        }

        public void Reset()
        {
            seen.Clear();
            lastUpTotal = lastDownTotal = 0;
            vpnShare = 1;
            VpnTotalUp = VpnTotalDown = DirectTotalUp = DirectTotalDown = 0;
            VpnDownHist.Clear();
            VpnUpHist.Clear();
            DirectDownHist.Clear();
            DirectUpHist.Clear();
        }
    }
}
