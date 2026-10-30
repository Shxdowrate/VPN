using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace NeonVpn.Controls
{
    /// <summary>
    /// Кнопка включения: круг с тонкой линией. Включено — круг заливается акцентом,
    /// идёт подключение — по кругу бежит короткая дуга.
    /// </summary>
    public sealed class PowerButton : Grid
    {
        private readonly Ellipse body = new Ellipse();
        private readonly Ellipse arc = new Ellipse { IsHitTestVisible = false, StrokeDashCap = PenLineCap.Round, Opacity = 0 };
        private readonly TextBlock icon = new TextBlock { Text = "", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        private readonly RotateTransform arcRotate = new RotateTransform();
        private readonly ScaleTransform press = new ScaleTransform(1, 1);

        public event RoutedEventHandler Click;

        public static readonly DependencyProperty StateColorProperty =
            DependencyProperty.Register(nameof(StateColor), typeof(Color), typeof(PowerButton), new PropertyMetadata(Colors.Gray, (d, _) => ((PowerButton)d).Recolor()));

        public static readonly DependencyProperty BusyProperty =
            DependencyProperty.Register(nameof(Busy), typeof(bool), typeof(PowerButton), new PropertyMetadata(false, (d, _) => ((PowerButton)d).UpdateBusy()));

        public static readonly DependencyProperty LitProperty =
            DependencyProperty.Register(nameof(Lit), typeof(bool), typeof(PowerButton), new PropertyMetadata(false, (d, _) => ((PowerButton)d).Recolor()));

        public Color StateColor
        {
            get => (Color)GetValue(StateColorProperty);
            set => SetValue(StateColorProperty, value);
        }

        public bool Busy
        {
            get => (bool)GetValue(BusyProperty);
            set => SetValue(BusyProperty, value);
        }

        public bool Lit
        {
            get => (bool)GetValue(LitProperty);
            set => SetValue(LitProperty, value);
        }

        public PowerButton()
        {
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = press;

            body.StrokeThickness = 1.5;
            arc.StrokeThickness = 2;
            arc.Margin = new Thickness(-7);
            arc.RenderTransformOrigin = new Point(0.5, 0.5);
            arc.RenderTransform = arcRotate;
            icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

            Children.Add(body);
            Children.Add(arc);
            Children.Add(icon);

            SizeChanged += (_, _) =>
            {
                icon.FontSize = Math.Max(14, ActualWidth * 0.26);
                double c = Math.PI * (ActualWidth + 14);
                arc.StrokeDashArray = new DoubleCollection { c * 0.18 / arc.StrokeThickness, c * 0.82 / arc.StrokeThickness };
            };

            MouseEnter += (_, _) => Recolor();
            MouseLeave += (_, _) => { Scale(1); Recolor(); };
            MouseLeftButtonDown += (_, e) =>
            {
                Scale(0.96);
                CaptureMouse();
                e.Handled = true;
            };
            MouseLeftButtonUp += (_, e) =>
            {
                bool inside = IsMouseOver;
                ReleaseMouseCapture();
                Scale(1);

                if (inside)
                {
                    Click?.Invoke(this, new RoutedEventArgs());
                }

                e.Handled = true;
            };

            Recolor();
        }

        private void Scale(double to)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120));
            press.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            press.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        private void Recolor()
        {
            Color c = StateColor;
            Color bg = Color.FromRgb(0x0F, 0x10, 0x12);

            if (Lit)
            {
                body.Fill = new SolidColorBrush(c);
                body.Stroke = new SolidColorBrush(c);
                icon.Foreground = new SolidColorBrush(bg);
            }
            else
            {
                body.Fill = new SolidColorBrush(IsMouseOver ? Color.FromRgb(0x1A, 0x1B, 0x1F) : Color.FromRgb(0x14, 0x15, 0x18));
                body.Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x32));
                icon.Foreground = new SolidColorBrush(c);
            }

            arc.Stroke = new SolidColorBrush(Color.FromRgb(0xEC, 0xED, 0xEF));
        }

        private void UpdateBusy()
        {
            if (Busy)
            {
                arc.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(150)));
                arcRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                arc.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
                arcRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }
    }
}
