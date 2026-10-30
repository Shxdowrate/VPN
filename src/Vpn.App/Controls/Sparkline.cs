using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace NeonVpn.Controls
{
    /// <summary>График скорости: загрузка — линия с лёгкой заливкой, отдача — бледная линия.</summary>
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty DownProperty =
            DependencyProperty.Register(nameof(Down), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty UpProperty =
            DependencyProperty.Register(nameof(Up), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ColorProperty =
            DependencyProperty.Register(nameof(Color), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Color.FromRgb(0x76, 0xFF, 0x03), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty Color2Property =
            DependencyProperty.Register(nameof(Color2), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Сколько точек показывать (окно по времени).</summary>
        public int Points { get; set; } = 60;

        // Не массив: свойства-массивы нельзя привязывать внутри шаблонов XAML (ошибка MC4102).
        public IReadOnlyList<double> Down
        {
            get => (IReadOnlyList<double>)GetValue(DownProperty);
            set => SetValue(DownProperty, value);
        }

        public IReadOnlyList<double> Up
        {
            get => (IReadOnlyList<double>)GetValue(UpProperty);
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
            Pen grid = new Pen(new SolidColorBrush(Color.FromArgb(14, 0xFF, 0xFF, 0xFF)), 1);
            grid.Freeze();

            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(grid, new Point(0, y), new Point(w, y));
            }

            IReadOnlyList<double> down = Down ?? Array.Empty<double>();
            IReadOnlyList<double> up = Up ?? Array.Empty<double>();
            double max = Math.Max(16 * 1024, Math.Max(down.DefaultIfEmpty(0).Max(), up.DefaultIfEmpty(0).Max())) * 1.15;

            DrawSeries(dc, down, max, w, h, Color, filled: true);
            DrawSeries(dc, up, max, w, h, Color2, filled: false);
        }

        private void DrawSeries(DrawingContext dc, IReadOnlyList<double> values, double max, double w, double h, Color c, bool filled)
        {
            if (values.Count < 2)
            {
                return;
            }

            int n = Math.Min(values.Count, Points);
            double step = w / (Points - 1);
            double x0 = w - step * (n - 1);

            StreamGeometry line = new StreamGeometry();
            StreamGeometry area = new StreamGeometry();

            Point P(int i) => new Point(x0 + step * i, h - 2 - (h - 4) * Math.Min(1, values[values.Count - n + i] / max));

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
                SolidColorBrush fillBrush = new SolidColorBrush(Color.FromArgb(22, c.R, c.G, c.B));
                fillBrush.Freeze();
                dc.DrawGeometry(fillBrush, null, area);
            }

            Pen core = new Pen(new SolidColorBrush(Color.FromArgb(filled ? (byte)230 : (byte)140, c.R, c.G, c.B)), 1.2) { LineJoin = PenLineJoin.Round };
            core.Freeze();
            dc.DrawGeometry(null, core, line);
        }
    }
}
