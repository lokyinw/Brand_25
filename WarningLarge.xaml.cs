using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    /// <summary>
    /// Larger variant of Warning for long-form messages (e.g. consolidation
    /// confirm/complete reports) that don't fit in the compact Warning dialog.
    /// Same constructor signature as Warning, so it's a drop-in replacement.
    /// </summary>
    public partial class WarningLarge : Window
    {
        private readonly string _revealPath;

        // revealPath: optional file or folder to reveal via the "Open Log Folder"
        // button. Leave it null (the default) and the button stays hidden — existing
        // calls elsewhere in the codebase are unaffected.
        public WarningLarge(string title, string message, string footerText = "Default Warning",
                             string revealPath = null)
        {
            InitializeComponent();
            SetMessage(message, revealPath);
            TitleText.Text = title;
            FooterText.Text = footerText;
            _revealPath = revealPath;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            if (!string.IsNullOrEmpty(_revealPath))
                OpenFolderButton.Visibility = Visibility.Visible;
        }

        // Builds MessageText via Inlines (rather than a plain Text assignment) so the
        // reveal path, when supplied, can be appended underneath the main message in a
        // much smaller, muted font - without needing any changes to the XAML.
        private void SetMessage(string message, string revealPath)
        {
            MessageText.Inlines.Clear();
            MessageText.Inlines.Add(new Run(message));

            if (!string.IsNullOrEmpty(revealPath))
            {
                MessageText.Inlines.Add(new LineBreak());
                MessageText.Inlines.Add(new Run(revealPath)
                {
                    FontSize = 12,
                    Foreground = Brushes.DimGray
                });
            }
        }

        private BitmapImage LoadEmbeddedImage(string imageName)
        {
            string resourcePath = $"Brand_25.Resources.Images.{imageName}";
            Assembly assembly = Assembly.GetExecutingAssembly();

            using (Stream stream = assembly.GetManifestResourceStream(resourcePath))
            {
                if (stream == null)
                    throw new Exception($"Embedded resource not found: {resourcePath}");

                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                return image;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void buttonMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void buttonMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            maximizeImage.Source = LoadEmbeddedImage(
                WindowState == WindowState.Maximized ? "restore_32.png" : "maximize_32.png");
        }

        private void buttonClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void CloseDialog(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_revealPath)) return;

            try
            {
                if (File.Exists(_revealPath))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_revealPath}\"");
                else if (Directory.Exists(_revealPath))
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{_revealPath}\"");
                else
                    new Warning("Not Found", $"Could not find:\n{_revealPath}", "WarningLarge").ShowDialog();
            }
            catch (Exception ex)
            {
                new Warning("Could Not Open Folder", ex.Message, "WarningLarge").ShowDialog();
            }
        }
    }
}
