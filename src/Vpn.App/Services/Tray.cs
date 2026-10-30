using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using NeonVpn.Core;
using WF = System.Windows.Forms;

namespace NeonVpn.Services
{
    /// <summary>Значок в трее: цвет кольца — состояние, меню — всё главное без окна.</summary>
    public sealed class Tray : IDisposable
    {
        private readonly WF.NotifyIcon icon;
        private readonly VpnService vpn;
        private readonly Dictionary<string, Icon> cache = new Dictionary<string, Icon>();

        public event Action OpenMain;
        public event Action OpenMini;
        public event Action Exit;

        public Tray(VpnService vpn)
        {
            this.vpn = vpn;
            icon = new WF.NotifyIcon { Visible = true, Text = "Neon VPN" };
            icon.MouseClick += (_, e) =>
            {
                if (e.Button == WF.MouseButtons.Left)
                {
                    OpenMain?.Invoke();
                }
            };

            WF.ContextMenuStrip menu = new WF.ContextMenuStrip { Renderer = new DarkRenderer(), Font = new Font("Segoe UI", 9.5f) };
            menu.Opening += (_, _) => Fill(menu);
            icon.ContextMenuStrip = menu;
            Fill(menu);
            Update();
        }

        public void Update()
        {
            Color c = vpn.State switch
            {
                VpnState.On => Hex(vpn.Settings.Accent, Color.FromArgb(0x76, 0xFF, 0x03)),
                VpnState.Connecting or VpnState.Reconnecting or VpnState.Switching => Hex(vpn.Settings.Accent2, Color.FromArgb(0x00, 0xE5, 0xFF)),
                VpnState.Error => Color.FromArgb(0xFF, 0x1F, 0x4B),
                _ => Color.FromArgb(0x5A, 0x64, 0x80)
            };

            icon.Icon = Ring(c);
            string server = vpn.Current?.DisplayName ?? "";
            string text = "Neon VPN · " + vpn.StateText + (server.Length > 0 ? "\n" + server : "");
            icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        public void Notify(string title, string text, bool error = false)
        {
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = string.IsNullOrWhiteSpace(text) ? " " : text;
            icon.BalloonTipIcon = error ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.None;
            icon.ShowBalloonTip(4000);
        }

        private void Fill(WF.ContextMenuStrip menu)
        {
            menu.Items.Clear();
            Settings s = vpn.Settings;

            WF.ToolStripMenuItem status = new WF.ToolStripMenuItem(vpn.StateText + (vpn.Current != null ? " · " + vpn.Current.DisplayName : "")) { Enabled = false };
            menu.Items.Add(status);
            menu.Items.Add(new WF.ToolStripSeparator());

            menu.Items.Add(Item(vpn.IsOn || vpn.Busy ? "Отключить" : "Подключить", () => _ = vpn.ToggleAsync()));
            menu.Items.Add(Item("⚡ Лучший сервер (из всех)", () => _ = vpn.ConnectBestAsync(ScopeKind.All, "")));

            WF.ToolStripMenuItem auto = new WF.ToolStripMenuItem("Авто");
            auto.DropDownItems.Add(Item("Все серверы", () => _ = vpn.SetAutoAsync(ScopeKind.All, ""), s.Target.Auto && s.Target.Scope == ScopeKind.All));

            foreach (SubscriptionInfo sub in s.Subscriptions.Where(x => x.Active.Any()))
            {
                auto.DropDownItems.Add(Item("Подписка: " + sub.Name, () => _ = vpn.SetAutoAsync(ScopeKind.Subscription, sub.Id),
                    s.Target.Auto && s.Target.Scope == ScopeKind.Subscription && s.Target.ScopeId == sub.Id));
            }

            foreach (ServerGroup g in s.Groups)
            {
                auto.DropDownItems.Add(Item("Группа: " + g.Name, () => _ = vpn.SetAutoAsync(ScopeKind.Group, g.Id),
                    s.Target.Auto && s.Target.Scope == ScopeKind.Group && s.Target.ScopeId == g.Id));
            }

            menu.Items.Add(auto);

            WF.ToolStripMenuItem servers = new WF.ToolStripMenuItem("Сервер");

            foreach (ServerNode n in s.AllActive().OrderBy(x => vpn.PingRank(x.Key)).Take(20))
            {
                int? p = vpn.PingOf(n.Key);
                string label = (n.Country.Length > 0 ? n.Country + "  " : "") + n.DisplayName + (p > 0 ? $"  ·  {p} мс" : "");
                servers.DropDownItems.Add(Item(label, () => _ = vpn.SelectServerAsync(n.Key), !s.Target.Auto && s.Target.ServerKey == n.Key));
            }

            if (servers.DropDownItems.Count == 0)
            {
                servers.DropDownItems.Add(new WF.ToolStripMenuItem("Нет серверов") { Enabled = false });
            }

            menu.Items.Add(servers);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(Item("Мини-окно", () => OpenMini?.Invoke()));
            menu.Items.Add(Item("Открыть", () => OpenMain?.Invoke()));
            menu.Items.Add(Item("Выход", () => Exit?.Invoke()));
        }

        private static WF.ToolStripMenuItem Item(string text, Action click, bool check = false)
        {
            WF.ToolStripMenuItem i = new WF.ToolStripMenuItem(text) { Checked = check };
            i.Click += (_, _) => click();
            return i;
        }

        private static Color Hex(string hex, Color fallback)
        {
            try
            {
                return ColorTranslator.FromHtml(hex);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Значок: тёмный круг со светящимся кольцом цвета состояния.</summary>
        private Icon Ring(Color c)
        {
            string key = c.ToArgb().ToString();

            if (cache.TryGetValue(key, out Icon cached))
            {
                return cached;
            }

            using Bitmap bmp = new Bitmap(32, 32);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (SolidBrush glow = new SolidBrush(Color.FromArgb(70, c)))
                {
                    g.FillEllipse(glow, 0, 0, 31, 31);
                }

                using (SolidBrush bg = new SolidBrush(Color.FromArgb(255, 8, 10, 17)))
                {
                    g.FillEllipse(bg, 3, 3, 25, 25);
                }

                using (Pen ring = new Pen(c, 3.2f))
                {
                    g.DrawEllipse(ring, 4.5f, 4.5f, 22, 22);
                }

                // Значок питания.
                using (Pen p = new Pen(c, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(p, 10.5f, 10.5f, 11, 11, -55, 290);
                    g.DrawLine(p, 16, 8.5f, 16, 15.5f);
                }
            }

            IntPtr h = bmp.GetHicon();
            Icon result = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            cache[key] = result;
            return result;
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();

            foreach (Icon i in cache.Values)
            {
                i.Dispose();
            }
        }

        /// <summary>Тёмное меню трея в тон окну.</summary>
        private sealed class DarkRenderer : WF.ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors())
            {
            }

            protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? (e.Item.Selected ? Color.FromArgb(0x00, 0xE5, 0xFF) : Color.FromArgb(0xE9, 0xF2, 0xFF)) : Color.FromArgb(0x7D, 0x8A, 0xA6);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(WF.ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Color.FromArgb(0x7D, 0x8A, 0xA6);
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
            {
                using SolidBrush b = new SolidBrush(Color.FromArgb(0x76, 0xFF, 0x03));
                Rectangle r = e.ImageRectangle;
                e.Graphics.FillEllipse(b, r.X + r.Width / 2 - 3, r.Y + r.Height / 2 - 3, 6, 6);
            }
        }

        private sealed class DarkColors : WF.ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(0x0B, 0x0E, 0x16);
            private static readonly Color Hi = Color.FromArgb(0x13, 0x22, 0x2C);
            private static readonly Color Border = Color.FromArgb(0x1C, 0x23, 0x35);

            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hi;
            public override Color MenuItemSelected => Hi;
            public override Color MenuItemSelectedGradientBegin => Hi;
            public override Color MenuItemSelectedGradientEnd => Hi;
            public override Color MenuItemPressedGradientBegin => Hi;
            public override Color MenuItemPressedGradientEnd => Hi;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
            public override Color CheckBackground => Bg;
            public override Color CheckSelectedBackground => Hi;
        }
    }
}
