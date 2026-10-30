using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace NeonVpn.Controls
{
    /// <summary>Светящийся график скорости: загрузка — сплошной заливкой, отдача — тонкой линией.</summary>
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty DownProperty =
            DependencyProperty.Register(nameof(Down), typeof(double[]), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty UpProperty =
            DependencyProperty.Register(nameof(Up), typeof(double[]), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ColorProperty =
            DependencyProperty.Register(nameof(Color), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Color.FromRgb(0x76, 0xFF, 0x03), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty Color2Property =
            DependencyProperty.Register(nameof(Color2), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Сколько точек показывать (окно по времени).</summary>
        public int Points { get; set; } = 60;

        public double[] Down
        {
            get => (double[])GetValue(DownProperty);
            set => SetValue(DownProperty, value);
        }

        public double[] Up
        {
            get => (double[])GetValue(UpProperty);
            set => SetValue(UpProperty, value);
        }

        public Color Color
        {
            get => (Color)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        public Color Color2
        {
            get => (Color)GetValue(Color2Property);
            set => SetValue(Color2Property, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;

            if (w < 4 || h < 4)
            {
                return;
            }

            // Едва заметная сетка.
            Pen grid = new Pen(new SolidColorBrush(Color.FromArgb(22, 0x9A, 0xB4, 0xE0)), 1);
            grid.Freeze();

            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(grid, new Point(0, y), new Point(w, y));
            }

            double[] down = Down ?? Array.Empty<double>();
            double[] up = Up ?? Array.Empty<double>();
            double max = Math.Max(16 * 1024, Math.Max(down.DefaultIfEmpty(0).Max(), up.DefaultIfEmpty(0).Max())) * 1.15;

            DrawSeries(dc, down, max, w, h, Color, filled: true);
            DrawSeries(dc, up, max, w, h, Color2, filled: false);
        }

        private void DrawSeries(DrawingContext dc, double[] values, double max, double w, double h, Color c, bool filled)
        {
            if (values.Length < 2)
            {
                return;
            }

            int n = Math.Min(values.Length, Points);
            double step = w / (Points - 1);
            double x0 = w - step * (n - 1);

            StreamGeometry line = new StreamGeometry();
            StreamGeometry area = new StreamGeometry();

            Point P(int i) => new Point(x0 + step * i, h - 2 - (h - 4) * Math.Min(1, values[values.Length - n + i] / max));

            using (StreamGeometryContext l = line.Open())
            using (StreamGeometryContext a = area.Open())
            {
                l.BeginFigure(P(0), false, false);
                a.BeginFigure(new Point(x0, h), true, true);
                a.LineTo(P(0), false, false);

                for (int i = 1; i < n; i++)
                {
                    // Плавная кривая между точками.
                    Point p0 = P(i - 1), p1 = P(i);
                    double mx = (p0.X + p1.X) / 2;
                    l.BezierTo(new Point(mx, p0.Y), new Point(mx, p1.Y), p1, true, true);
                    a.BezierTo(new Point(mx, p0.Y), new Point(mx, p1.Y), p1, false, true);
                }

                a.LineTo(new Point(w, h), false, false);
            }

            line.Freeze();
            area.Freeze();

            if (filled)
            {
                LinearGradientBrush fillBrush = new LinearGradientBrush(Color.FromArgb(90, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
                fillBrush.Freeze();
                dc.DrawGeometry(fillBrush, null, area);
            }

            // Свечение: несколько полупрозрачных обводок, потом яркая сердцевина.
            foreach ((double t, byte alpha) in new[] { (7.0, (byte)18), (4.0, (byte)40), (2.2, (byte)90) })
            {
                Pen glow = new Pen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)), t) { LineJoin = PenLineJoin.Round };
                glow.Freeze();
                dc.DrawGeometry(null, glow, line);
            }

            Pen core = new Pen(new SolidColorBrush(c), filled ? 1.4 : 1.1) { LineJoin = PenLineJoin.Round };
            core.Freeze();
            dc.DrawGeometry(null, core, line);
        }
    }
}
