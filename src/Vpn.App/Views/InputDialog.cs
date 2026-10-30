using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NeonVpn.Controls;

namespace NeonVpn.Views
{
    /// <summary>Маленькое неоновое окно с полями ввода (подписка, группа, ссылки, переименование).</summary>
    public sealed class InputDialog : Window
    {
        private readonly List<TextBox> boxes = new List<TextBox>();
        private string[] result;

        public sealed class Field
        {
            public string Label = "";
            public string Value = "";
            public string Placeholder = "";
            public bool Multiline;
        }

        private InputDialog(string title, string hint, Field[] fields, string ok)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            Width = fields.Any(f => f.Multiline) ? 560 : 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = (FontFamily)Application.Current.Resources["Body"];

            Grid root = new Grid();
            NeonFrame frame = new NeonFrame { Shape = App.Vpn.Settings.FrameShape, Compact = true, GlowColor = Palette.Accent2, GlowColor2 = Palette.Accent, Intensity = App.Vpn.Settings.Glow };
            root.Children.Add(frame);

            StackPanel panel = new StackPanel { Margin = new Thickness(36, 30, 36, 34) };
            panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H2"], FontSize = 17, Margin = new Thickness(0, 0, 0, 6) });

            if (!string.IsNullOrEmpty(hint))
            {
                panel.Children.Add(new TextBlock { Text = hint, Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 0, 0, 10) });
            }

            foreach (Field f in fields)
            {
                if (f.Label.Length > 0)
                {
                    panel.Children.Add(new TextBlock { Text = f.Label, Style = (Style)Application.Current.Resources["Caps"], Margin = new Thickness(0, 8, 0, 4) });
                }

                TextBox box = new TextBox { Text = f.Value, Tag = f.Placeholder };

                if (f.Multiline)
                {
                    box.AcceptsReturn = true;
                    box.TextWrapping = TextWrapping.NoWrap;
                    box.Height = 170;
                    box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                    box.VerticalContentAlignment = VerticalAlignment.Top;
                    box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                    box.FontSize = 11.5;
                }

                boxes.Add(box);
                panel.Children.Add(box);
            }

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            Button cancel = new Button { Content = "ОТМЕНА", Style = (Style)Application.Current.Resources["NeonButton"], Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            Button okButton = new Button { Content = ok, Style = (Style)Application.Current.Resources["SolidButton"], IsDefault = !fields.Any(f => f.Multiline) };
            cancel.Click += (_, _) => Close();
            okButton.Click += (_, _) =>
            {
                result = boxes.Select(b => b.Text.Trim()).ToArray();
                Close();
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(okButton);
            panel.Children.Add(buttons);

            root.Children.Add(panel);
            Content = root;

            MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is not TextBox && e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch (InvalidOperationException) { }
                }
            };

            Loaded += (_, _) =>
            {
                boxes.FirstOrDefault()?.Focus();
                boxes.FirstOrDefault()?.SelectAll();
            };
        }

        /// <summary>Показывает окно. null — нажали «Отмена».</summary>
        public static string[] Ask(Window owner, string title, string hint, string ok, params Field[] fields)
        {
            InputDialog d = new InputDialog(title, hint, fields, ok);

            if (owner != null && owner.IsVisible)
            {
                d.Owner = owner;
            }
            else
            {
                d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            d.ShowDialog();
            return d.result;
        }

        public static string AskOne(Window owner, string title, string hint, string value, string placeholder, string ok = "ГОТОВО") =>
            Ask(owner, title, hint, ok, new Field { Value = value ?? "", Placeholder = placeholder })?.FirstOrDefault();

        /// <summary>Вопрос «да / нет».</summary>
        public static bool Confirm(Window owner, string title, string hint, string ok = "ДА")
        {
            InputDialog d = new InputDialog(title, hint, Array.Empty<Field>(), ok);

            if (owner != null && owner.IsVisible) d.Owner = owner;
            else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            d.ShowDialog();
            return d.result != null;
        }
    }
}
