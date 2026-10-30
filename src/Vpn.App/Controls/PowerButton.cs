using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace NeonVpn.Controls
{
    /// <summary>
    /// Большая кнопка включения: светящееся кольцо цвета состояния, «дыхание» ореола, когда VPN включён,
    /// и бегущая дуга, пока идёт подключение.
    /// </summary>
    public sealed class PowerButton : Grid
    {
        private readonly Ellipse halo = new Ellipse { IsHitTestVisible = false };
        private readonly Ellipse ringGlow = new Ellipse { IsHitTestVisible = false };
        private readonly Ellipse ring = new Ellipse();
        private readonly Ellipse inner = new Ellipse();
        private readonly Ellipse arc = new Ellipse { IsHitTestVisible = false, StrokeDashCap = PenLineCap.Round };
        private readonly Path ticks = new Path { IsHitTestVisible = false };
        private readonly Viewbox ticksBox = new Viewbox { IsHitTestVisible = false, Opacity = 0.35, Stretch = Stretch.Fill };
        private readonly TextBlock icon = new TextBlock { Text = "", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        private readonly RotateTransform arcRotate = new RotateTransform();
        private readonly ScaleTransform press = new ScaleTransform(1, 1);
        private readonly BlurEffect ringBlur = new BlurEffect { Radius = 14 };

        public event RoutedEventHandler Click;

        public static readonly DependencyProperty StateColorProperty =
            DependencyProperty.Register(nameof(StateColor), typeof(Color), typeof(PowerButton), new PropertyMetadata(Color.FromRgb(0x46, 0x50, 0x69), (d, _) => ((PowerButton)d).Recolor()));

        public static readonly DependencyProperty BusyProperty =
            DependencyProperty.Register(nameof(Busy), typeof(bool), typeof(PowerButton), new PropertyMetadata(false, (d, _) => ((PowerButton)d).UpdateBusy()));

        public static readonly DependencyProperty LitProperty =
            DependencyProperty.Register(nameof(Lit), typeof(bool), typeof(PowerButton), new PropertyMetadata(false, (d, _) => ((PowerButton)d).UpdateLit()));

        public Color StateColor
        {
            get => (Color)GetValue(StateColorProperty);
            set => SetValue(StateColorProperty, value);
        }

        /// <summary>Идёт подключение — крутится дуга.</summary>
        public bool Busy
        {
            get => (bool)GetValue(BusyProperty);
            set => SetValue(BusyProperty, value);
        }

        /// <summary>VPN включён — ореол «дышит».</summary>
        public bool Lit
        {
            get => (bool)GetValue(LitProperty);
            set => SetValue(LitProperty, value);
        }

        public PowerButton()
        {
            Cursor = Cursors.Hand;
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = press;
            Background = Brushes.Transparent;

            halo.Margin = new Thickness(-28);
            ringGlow.StrokeThickness = 6;
            ringGlow.Effect = ringBlur;
            ring.StrokeThickness = 2.5;
            inner.Margin = new Thickness(14);
            inner.StrokeThickness = 1;
            arc.StrokeThickness = 3.5;
            arc.RenderTransformOrigin = new Point(0.5, 0.5);
            arc.RenderTransform = arcRotate;
            arc.Opacity = 0;
            arc.Margin = new Thickness(-9);
            ticksBox.Margin = new Thickness(-18);
            ticks.StrokeThickness = 1.2;
            ticksBox.Child = new Grid { Width = 100, Height = 100, Children = { ticks } };
            icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

            Children.Add(halo);
            Children.Add(ticksBox);
            Children.Add(ringGlow);
            Children.Add(ring);
            Children.Add(inner);
            Children.Add(arc);
            Children.Add(icon);

            ticks.Data = BuildTicks();

            SizeChanged += (_, _) =>
            {
                icon.FontSize = Math.Max(16, ActualWidth * 0.3);

                // Дуга на четверть круга — штрих пунктира по окружности.
                double c = Math.PI * (ActualWidth + 18);
                double t = arc.StrokeThickness;
                arc.StrokeDashArray = new DoubleCollection { c * 0.22 / t, c * 0.78 / t };
            };
            MouseEnter += (_, _) => Scale(1.04);
            MouseLeave += (_, _) => Scale(1);
            MouseLeftButtonDown += (_, e) =>
            {
                Scale(0.95);
                CaptureMouse();
                e.Handled = true;
            };
            MouseLeftButtonUp += (_, e) =>
            {
                bool inside = IsMouseOver;
                ReleaseMouseCapture();
                Scale(inside ? 1.04 : 1);

                if (inside)
                {
                    Click?.Invoke(this, new RoutedEventArgs());
                }

                e.Handled = true;
            };

            Recolor();
        }

        private static Geometry BuildTicks()
        {
            // Риски по кругу, как на шкале.
            StreamGeometry g = new StreamGeometry();

            using (StreamGeometryContext c = g.Open())
            {
                for (int i = 0; i < 48; i++)
                {
                    double a = i * Math.PI * 2 / 48;
                    double r1 = i % 4 == 0 ? 44 : 47;
                    c.BeginFigure(new Point(50 + Math.Cos(a) * r1, 50 + Math.Sin(a) * r1), false, false);
                    c.LineTo(new Point(50 + Math.Cos(a) * 50, 50 + Math.Sin(a) * 50), true, false);
                }
            }

            g.Freeze();
            return g;
        }

        private void Scale(double to)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() };
            press.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            press.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        private void Recolor()
        {
            Color c = StateColor;
            SolidColorBrush b = new SolidColorBrush(c);

            ring.Stroke = b;
            ringGlow.Stroke = b;
            arc.Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
            ticks.Stroke = b;
            icon.Foreground = b;
            icon.Effect = new DropShadowEffect { Color = c, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.9 };

            RadialGradientBrush h = new RadialGradientBrush();
            h.GradientStops.Add(new GradientStop(Color.FromArgb(70, c.R, c.G, c.B), 0.45));
            h.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            halo.Fill = h;

            RadialGradientBrush f = new RadialGradientBrush { GradientOrigin = new Point(0.35, 0.3) };
            f.GradientStops.Add(new GradientStop(Color.FromRgb(0x16, 0x1B, 0x2A), 0));
            f.GradientStops.Add(new GradientStop(Color.FromRgb(0x07, 0x09, 0x10), 1));
            inner.Fill = f;
            inner.Stroke = new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B));
        }

        public void SetArcColor(Color c) => arc.Stroke = new SolidColorBrush(c);

        private void UpdateBusy()
        {
            if (Busy)
            {
                arc.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
                arcRotate.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                arc.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
                arcRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        private void UpdateLit()
        {
            if (Lit)
            {
                halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(2.2))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
                ringBlur.Radius = 16;
            }
            else
            {
                halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(400)));
                ringBlur.Radius = 8;
            }
        }
    }
}
