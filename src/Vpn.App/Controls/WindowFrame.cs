using System.Windows;
using System.Windows.Controls;

namespace NeonVpn.Controls
{
    /// <summary>Корпус окна: скруглённый прямоугольник с тонкой линией, без теней и свечения.</summary>
    public sealed class WindowFrame : Border
    {
        public WindowFrame()
        {
            CornerRadius = new CornerRadius(12);
            BorderThickness = new Thickness(1);
            SetResourceReference(BackgroundProperty, "Bg");
            SetResourceReference(BorderBrushProperty, "Line");
        }
    }
}
