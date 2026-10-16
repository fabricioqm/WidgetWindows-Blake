using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace WidgetWindowDemo.Views
{
    public enum DialogKind { Info, Success, Warning, Error, Confirm, Danger }

    /// <summary>
    /// App-styled replacement for MessageBox: alerts (one button) and confirmations (two buttons).
    /// Use the static <see cref="Alert"/> and <see cref="Confirm"/> helpers.
    /// </summary>
    public partial class AppDialog : Window
    {
        private AppDialog()
        {
            InitializeComponent();
        }

        /// <summary>Shows a message with a single button.</summary>
        public static void Alert(DialogKind kind, string title, string message,
            IEnumerable<string>? details = null, Window? owner = null, string buttonText = "Aceptar")
        {
            var dialog = Create(kind, title, message, details, owner);
            dialog.SecondaryButton.Visibility = Visibility.Collapsed;
            dialog.PrimaryButton.Content = buttonText;
            dialog.PrimaryButton.IsDefault = true;
            dialog.PrimaryButton.IsCancel = true; // Esc closes too
            dialog.Run(kind);
        }

        /// <summary>Asks a question. Returns true when the user confirms.</summary>
        public static bool Confirm(string title, string message, string confirmText, string cancelText = "Cancelar",
            bool danger = false, Window? owner = null)
        {
            var kind = danger ? DialogKind.Danger : DialogKind.Confirm;
            var dialog = Create(kind, title, message, null, owner);
            dialog.PrimaryButton.Content = confirmText;
            dialog.SecondaryButton.Content = cancelText;
            dialog.SecondaryButton.IsCancel = true;

            // Destructive actions default to "Cancel", so Enter can't delete by accident.
            if (danger)
            {
                dialog.PrimaryButton.Style = (Style)dialog.FindResource("DangerSolidButton");
                dialog.SecondaryButton.IsDefault = true;
            }
            else
            {
                dialog.PrimaryButton.IsDefault = true;
            }

            return dialog.Run(kind) == true;
        }

        private static AppDialog Create(DialogKind kind, string title, string message,
            IEnumerable<string>? details, Window? owner)
        {
            var dialog = new AppDialog();
            dialog.Title = title; // shown by assistive technologies
            dialog.TitleText.Text = title;
            dialog.MessageText.Text = message;
            dialog.MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;

            var lines = details?.ToList();
            if (lines is { Count: > 0 })
            {
                dialog.DetailsList.ItemsSource = lines;
                dialog.DetailsBox.Visibility = Visibility.Visible;
            }

            var (glyph, color) = kind switch
            {
                DialogKind.Success => ("", "#30A46C"),
                DialogKind.Warning => ("", "#F5A524"),
                DialogKind.Error => ("", "#E5484D"),
                DialogKind.Danger => ("", "#E5484D"),
                DialogKind.Confirm => ("", "#7F77FF"),
                _ => ("", "#7F77FF")
            };
            var accent = (Color)ColorConverter.ConvertFromString(color);
            dialog.IconGlyph.Text = glyph;
            dialog.IconGlyph.Foreground = new SolidColorBrush(accent);
            dialog.IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));

            // Centered on the manager when it is open; otherwise (app in the tray) centered on screen, on top.
            owner ??= System.Windows.Application.Current?.MainWindow;
            if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Topmost = true;
            }

            return dialog;
        }

        private bool? Run(DialogKind kind)
        {
            switch (kind)
            {
                case DialogKind.Error: SystemSounds.Hand.Play(); break;
                case DialogKind.Warning: SystemSounds.Exclamation.Play(); break;
            }

            return ShowDialog();
        }

        private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        /// <summary>The dialog has no title bar: drag it from anywhere on the card.</summary>
        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
