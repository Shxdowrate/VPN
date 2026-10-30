using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace NeonVpn.Controls
{
    /// <summary>
    /// Неоновый корпус окна без прямоугольной рамки: силуэт со срезами и ступеньками, светящийся контур
    /// с бегущим переливом и мягкая подсветка внутри. Прозрачное вокруг силуэта не ловит мышь —
    /// окно буквально такой формы.
    /// </summary>
    public sealed class NeonFrame : Grid
    {
        /// <summary>Отступ силуэта от края окна — место для внешнего свечения.</summary>
        public const double Inset = 14;

        private readonly Path fill = new Path();
        private readonly Path bloom = new Path();
        private readonly Path line = new Path();
        private readonly Path sheen = new Path();
        private readonly LinearGradientBrush running;
        private readonly RotateTransform runningRotate = new RotateTransform(0, 0.5, 0.5);
        private readonly BlurEffect bloomBlur = new BlurEffect { Radius = 16, KernelType = KernelType.Gaussian };

        public static readonly DependencyProperty ShapeProperty =
            DependencyProperty.Register(nameof(Shape), typeof(string), typeof(NeonFrame), new PropertyMetadata("Blade", (d, _) => ((NeonFrame)d).Rebuild()));

        public static readonly DependencyProperty GlowColorProperty =
            DependencyProperty.Register(nameof(GlowColor), typeof(Color), typeof(NeonFrame), new PropertyMetadata(Color.FromRgb(0x76, 0xFF, 0x03), (d, _) => ((NeonFrame)d).Recolor()));

        public static readonly DependencyProperty GlowColor2Property =
            DependencyProperty.Register(nameof(GlowColor2), typeof(Color), typeof(NeonFrame), new PropertyMetadata(Color.FromRgb(0x00, 0xE5, 0xFF), (d, _) => ((NeonFrame)d).Recolor()));

        public static readonly DependencyProperty IntensityProperty =
            DependencyProperty.Register(nameof(Intensity), typeof(double), typeof(NeonFrame), new PropertyMetadata(1.0, (d, _) => ((NeonFrame)d).Recolor()));

        public static readonly DependencyProperty CompactProperty =
            DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(NeonFrame), new PropertyMetadata(false, (d, _) => ((NeonFrame)d).Rebuild()));

        public string Shape
        {
            get => (string)GetValue(ShapeProperty);
            set => SetValue(ShapeProperty, value);
        }

        public Color GlowColor
        {
            get => (Color)GetValue(GlowColorProperty);
            set => SetValue(GlowColorProperty, value);
        }

        public Color GlowColor2
        {
            get => (Color)GetValue(GlowColor2Property);
            set => SetValue(GlowColor2Property, value);
        }

        public double Intensity
        {
            get => (double)GetValue(IntensityProperty);
            set => SetValue(IntensityProperty, value);
        }

        /// <summary>Мини-окно: срезы и ступеньки помельче.</summary>
        public bool Compact
        {
            get => (bool)GetValue(CompactProperty);
            set => SetValue(CompactProperty, value);
        }

        public NeonFrame()
        {
            IsHitTestVisible = true;

            running = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = runningRotate };

            fill.Stretch = Stretch.None;
            bloom.StrokeThickness = 7;
            bloom.Effect = bloomBlur;
            bloom.IsHitTestVisible = false;
            line.StrokeThickness = 1.6;
            line.IsHitTestVisible = false;
            line.Stroke = running;
            sheen.StrokeThickness = 1;
            sheen.IsHitTestVisible = false;
            sheen.Opacity = 0.18;
            sheen.Stroke = Brushes.White;
            sheen.Margin = new Thickness(2.5);

            Children.Add(bloom);
            Children.Add(fill);
            Children.Add(sheen);
            Children.Add(line);

            SizeChanged += (_, _) => Rebuild();
            Loaded += (_, _) =>
            {
                Recolor();
                StartRunning(true);
            };
        }

        /// <summary>Бегущий перелив по контуру. Выключается в настройках («Анимации»).</summary>
        public void StartRunning(bool on)
        {
            if (on)
            {
                DoubleAnimation spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever };
                runningRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
            }
            else
            {
                runningRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        /// <summary>Вспышка свечения: при подключении, смене сервера.</summary>
        public void Pulse()
        {
            DoubleAnimation a = new DoubleAnimation(Math.Min(1, 0.55 * Intensity + 0.45), 0.55 * Math.Min(Intensity, 1.6), TimeSpan.FromMilliseconds(900))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            bloom.BeginAnimation(OpacityProperty, a);
        }

        private void Recolor()
        {
            Color a = GlowColor;
            Color b = GlowColor2;
            double k = Math.Max(0.2, Math.Min(1.6, Intensity));

            running.GradientStops.Clear();
            running.GradientStops.Add(new GradientStop(a, 0));
            running.GradientStops.Add(new GradientStop(b, 0.35));
            running.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, a.R, a.G, a.B), 0.55));
            running.GradientStops.Add(new GradientStop(a, 0.8));
            running.GradientStops.Add(new GradientStop(b, 1));

            LinearGradientBrush bloomBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            bloomBrush.GradientStops.Add(new GradientStop(a, 0));
            bloomBrush.GradientStops.Add(new GradientStop(b, 0.5));
            bloomBrush.GradientStops.Add(new GradientStop(a, 1));
            bloom.Stroke = bloomBrush;
            bloom.Opacity = 0.55 * k;
            bloomBlur.Radius = 10 + 10 * k;

            // Корпус: непрозрачный почти чёрный, с подсветкой цвета акцента из угла.
            // Все цвета градиента — непрозрачные: прозрачная точка давала «дыру», сквозь которую видно рабочий стол.
            Color dark = Color.FromRgb(0x08, 0x0A, 0x11);
            RadialGradientBrush inner = new RadialGradientBrush
            {
                Center = new Point(0.12, 0.05),
                GradientOrigin = new Point(0.12, 0.05),
                RadiusX = 0.9,
                RadiusY = 1.1
            };
            inner.GradientStops.Add(new GradientStop(Blend(dark, a, 0.10 * k), 0));
            inner.GradientStops.Add(new GradientStop(dark, 0.45));
            inner.GradientStops.Add(new GradientStop(Color.FromRgb(0x05, 0x06, 0x0A), 1));
            fill.Fill = inner;
        }

        private static Color Blend(Color from, Color to, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromRgb((byte)(from.R + (to.R - from.R) * t), (byte)(from.G + (to.G - from.G) * t), (byte)(from.B + (to.B - from.B) * t));
        }

        private void Rebuild()
        {
            if (ActualWidth < 40 || ActualHeight < 40)
            {
                return;
            }

            Rect r = new Rect(Inset, Inset, ActualWidth - Inset * 2, ActualHeight - Inset * 2);
            Geometry g = Build(Shape, r, Compact);
            fill.Data = g;
            bloom.Data = g;
            line.Data = g;

            // Тонкий внутренний блик — второй контур чуть внутри.
            Rect ri = new Rect(r.X + 3, r.Y + 3, Math.Max(1, r.Width - 6), Math.Max(1, r.Height - 6));
            sheen.Data = Build(Shape, ri, Compact);
            sheen.Margin = new Thickness(0);
        }

        /// <summary>Силуэт корпуса в прямоугольнике r.</summary>
        public static Geometry Build(string shape, Rect r, bool compact)
        {
            double k = compact ? 0.55 : 1;
            double L = r.Left, T = r.Top, R = r.Right, B = r.Bottom, W = r.Width, H = r.Height;
            Point[] pts;

            switch (shape)
            {
                case "Capsule":
                {
                    double rad = Math.Min(28 * k + 6, Math.Min(W, H) / 2);
                    return new RectangleGeometry(r, rad, rad);
                }

                case "Shard":
                {
                    double c1 = 46 * k, c2 = 64 * k, s = 14 * k;
                    pts = new[]
                    {
                        new Point(L + c1, T),
                        new Point(R - 120 * k, T),
                        new Point(R - 120 * k + s, T + s),
                        new Point(R - 10 * k, T + s),
                        new Point(R, T + s + 10 * k),
                        new Point(R, T + H * 0.52),
                        new Point(R - s, T + H * 0.52 + s),
                        new Point(R - s, B - c2),
                        new Point(R - s - c2, B),
                        new Point(L + W * 0.38, B),
                        new Point(L + W * 0.38 - s, B - s),
                        new Point(L + 16 * k, B - s),
                        new Point(L, B - s - 16 * k),
                        new Point(L, T + c1)
                    };
                    break;
                }

                default: // Blade
                {
                    double s = 12 * k;
                    double tab = compact ? W * 0.62 : Math.Min(W * 0.46, 430);
                    double notch = compact ? W * 0.3 : Math.Min(W * 0.3, 260);
                    pts = new[]
                    {
                        new Point(L + 24 * k, T),
                        new Point(L + tab, T),
                        new Point(L + tab + s, T + s),
                        new Point(R - 10 * k, T + s),
                        new Point(R, T + s + 10 * k),
                        new Point(R, B - 38 * k),
                        new Point(R - 38 * k, B),
                        new Point(L + notch + s, B),
                        new Point(L + notch, B - s),
                        new Point(L + 10 * k, B - s),
                        new Point(L, B - s - 10 * k),
                        new Point(L, T + 24 * k)
                    };
                    break;
                }
            }

            StreamGeometry geo = new StreamGeometry();

            using (StreamGeometryContext ctx = geo.Open())
            {
                ctx.BeginFigure(pts[0], true, true);
                ctx.PolyLineTo(pts[1..], true, true);
            }

            geo.Freeze();
            return geo;
        }
    }
}
