using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RimeUIEditor.View
{
    /// <summary>A one-line question (the name of a new screen): OK / Enter returns the text, Cancel / Esc returns null. Never used in a headless run.</summary>
    public static class NameDialog
    {
        public static string? Ask(Window p_Owner, string p_Title, string p_Prompt, string p_Default)
        {
            var s_Window = new Window
            {
                Title = p_Title, Owner = p_Owner, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(43, 43, 43)), ShowInTaskbar = false,
            };
            var s_Panel = new StackPanel { Margin = new Thickness(12) };
            s_Panel.Children.Add(new TextBlock { Text = p_Prompt, Margin = new Thickness(0, 0, 0, 6) });
            var s_Box = new TextBox { Text = p_Default, Margin = new Thickness(0, 0, 0, 10) };
            s_Panel.Children.Add(s_Box);
            var s_Buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var s_Ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var s_Cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            string? s_Result = null;
            s_Ok.Click += (_, _) => { s_Result = s_Box.Text; s_Window.DialogResult = true; };
            s_Buttons.Children.Add(s_Ok); s_Buttons.Children.Add(s_Cancel);
            s_Panel.Children.Add(s_Buttons);
            s_Window.Content = s_Panel;
            s_Window.Loaded += (_, _) => { s_Box.Focus(); s_Box.SelectAll(); };
            return s_Window.ShowDialog() == true ? s_Result : null;
        }
    }
}
