using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NeonVpn.Core
{
    /// <summary>
    /// Запускает sing-box (и Xray, если план просит) и следит за ними.
    /// Процессы привязаны к «заданию» Windows: если программа упадёт, ядра закроются вместе с ней и не оставят TUN висеть.
    /// </summary>
    public sealed class CoreRunner
    {
        private readonly string name;
        private Process singBox;
        private Process xray;
        private readonly Queue<string> lastLines = new Queue<string>();
        private volatile bool stopping;

        public Plan Plan { get; private set; }
        public ClashApi Api { get; private set; }
        public bool Running => singBox != null && !singBox.HasExited;

        /// <summary>Ядро закрылось само (не по нашей команде). Аргумент — последние строки его журнала.</summary>
        public event Action<string> Crashed;

        /// <param name="name">main / probe — для имён файлов и журнала.</param>
        public CoreRunner(string name)
        {
            this.name = name;
        }

        public async Task StartAsync(Plan plan, CancellationToken token = default)
        {
            await StopAsync();
            stopping = false;
            Plan = plan;
            lastLines.Clear();

            if (!File.Exists(Paths.SingBox))
            {
                throw new FileNotFoundException("нет ядра sing-box: " + Paths.SingBox);
            }

            Directory.CreateDirectory(Paths.Run);
            string sbConfig = Path.Combine(Paths.Run, name + ".singbox.json");
            File.WriteAllText(sbConfig, plan.SingBoxJson);

            if (plan.XrayJson != null)
            {
                if (!File.Exists(Paths.Xray))
                {
                    throw new FileNotFoundException("нет ядра Xray: " + Paths.Xray);
                }

                string xrConfig = Path.Combine(Paths.Run, name + ".xray.json");
                File.WriteAllText(xrConfig, plan.XrayJson);
                xray = Launch(Paths.Xray, $"run -c \"{xrConfig}\"", "xray");
            }

            singBox = Launch(Paths.SingBox, $"run -c \"{sbConfig}\" -D \"{Paths.Run}\" --disable-color", "sing-box");
            Api = new ClashApi(plan.ApiPort, plan.Secret);

            // Ждём, пока поднимется API (а с TUN — адаптер), но не дольше 15 секунд.
            DateTime until = DateTime.UtcNow.AddSeconds(15);

            while (DateTime.UtcNow < until)
            {
                token.ThrowIfCancellationRequested();

                if (singBox.HasExited || (xray != null && xray.HasExited))
                {
                    string tail = Tail();
                    await StopAsync();
                    throw new Exception("ядро не запустилось: " + (tail.Length > 0 ? tail : "без объяснений"));
                }

                if (await Api.AliveAsync(token))
                {
                    singBox.EnableRaisingEvents = true;
                    singBox.Exited += OnExited;

                    if (xray != null)
                    {
                        xray.EnableRaisingEvents = true;
                        xray.Exited += OnExited;
                    }

                    return;
                }

                await Task.Delay(150, token);
            }

            await StopAsync();
            throw new Exception("ядро не отвечает 15 секунд");
        }

        private void OnExited(object sender, EventArgs e)
        {
            if (stopping)
            {
                return;
            }

            stopping = true;
            string tail = Tail();
            Log.Write($"[{name}] ядро закрылось само. {tail}");
            _ = StopAsync();
            Crashed?.Invoke(tail);
        }

        public async Task StopAsync()
        {
            stopping = true;
            Api?.Dispose();
            Api = null;

            foreach (Process p in new[] { singBox, xray })
            {
                if (p == null)
                {
                    continue;
                }

                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill(true);
                        await Task.Run(() => p.WaitForExit(5000));
                    }
                }
                catch
                {
                    // уже закрыт
                }
                finally
                {
                    p.Dispose();
                }
            }

            singBox = null;
            xray = null;
        }

        private Process Launch(string exe, string args, string label)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Paths.CoreDir
            };

            Process p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => OnLine(label, e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(label, e.Data);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Job.Attach(p);
            return p;
        }

        private void OnLine(string label, string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (lastLines)
            {
                lastLines.Enqueue(label + ": " + line);

                while (lastLines.Count > 12)
                {
                    lastLines.Dequeue();
                }
            }

            string l = line.ToLowerInvariant();

            // В журнал — только важное: предупреждения о каждом соединении забьют его за минуты.
            if (l.Contains("fatal") || l.Contains("panic") || l.Contains("start service") || l.Contains("failed to start")
                || (l.Contains("error") && !l.Contains("connection") && !l.Contains("dns:")))
            {
                Log.Write($"[{name}/{label}] {line}");
            }
        }

        private string Tail()
        {
            lock (lastLines)
            {
                return string.Join(" | ", lastLines.Reverse().Take(4).Reverse().Select(Shorten));
            }
        }

        private static string Shorten(string s) => s.Length > 220 ? s.Substring(0, 220) + "…" : s;

        /// <summary>Закрывает ядра, оставшиеся от прошлого запуска (если программа была убита).</summary>
        public static void KillLeftovers()
        {
            foreach (string exe in new[] { "sing-box", "xray" })
            {
                foreach (Process p in Process.GetProcessesByName(exe))
                {
                    try
                    {
                        string path = p.MainModule?.FileName ?? "";

                        if (path.StartsWith(Paths.CoreDir, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill(true);
                            Log.Write("Закрыл оставшееся ядро " + exe + " (pid " + p.Id + ")");
                        }
                    }
                    catch
                    {
                        // нет доступа — не наш
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
        }

        /// <summary>Свободные подряд идущие порты на 127.0.0.1, начиная с base.</summary>
        public static int FreePortRange(int start, int count)
        {
            for (int basePort = start; basePort < 60000; basePort += Math.Max(count, 1) + 7)
            {
                if (Enumerable.Range(basePort, Math.Max(count, 1)).All(IsFree))
                {
                    return basePort;
                }
            }

            return start;
        }

        public static bool IsFree(int port)
        {
            try
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                l.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Объект-задание Windows с «убить всех при закрытии».</summary>
        private static class Job
        {
            private static IntPtr handle = IntPtr.Zero;

            public static void Attach(Process p)
            {
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                try
                {
                    if (handle == IntPtr.Zero)
                    {
                        handle = CreateJobObject(IntPtr.Zero, null);
                        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        int size = Marshal.SizeOf(info);
                        IntPtr ptr = Marshal.AllocHGlobal(size);
                        Marshal.StructureToPtr(info, ptr, false);
                        SetInformationJobObject(handle, 9, ptr, (uint)size);
                        Marshal.FreeHGlobal(ptr);
                    }

                    AssignProcessToJobObject(handle, p.Handle);
                }
                catch (Exception e)
                {
                    Log.Write("Не привязал ядро к заданию: " + e.Message);
                }
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

            [DllImport("kernel32.dll")]
            private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

            [DllImport("kernel32.dll")]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IO_COUNTERS
            {
                public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }
        }
    }
}
