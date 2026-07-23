using System;
//using System.Collections.Generic;
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
    /// Interaction logic for Warning.xaml
    /// </summary>
    public partial class Warning : Window
    {
        // Optional file or folder to reveal via the "Open Folder" button. Leave it
        // null (the default) and the button stays hidden — existing calls elsewhere
        // in the codebase are unaffected.
        private readonly string _revealPath;

        public Warning(string title, string message, string footerText = "Default Warning",
                        double messageFontSize = 20, string revealPath = null)

        {
            InitializeComponent();
            MessageText.FontSize = messageFontSize;
            SetMessage(message, revealPath);
            TitleText.Text = title;
            FooterText.Text = footerText;  // Set footer text dynamically
            _revealPath = revealPath;

            //minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            //maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            if (!string.IsNullOrEmpty(_revealPath))
                OpenFolderButton.Visibility = Visibility.Visible;

            //string fontPath = ExtractEmbeddedFont("Brand_25.Resources.Fonts.Moogalator.ttf");
            //MessageText.FontFamily = LoadFontFromFile(fontPath);
            //TitleText.FontFamily = LoadFontFromFile(fontPath);
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
                MessageText.Inlines.Add(new LineBreak());
                MessageText.Inlines.Add(new Run(revealPath)
                {
                    FontSize = 11,
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
        /*
        private void buttonMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }
        
        private void buttonMaximize_Click(object sender, RoutedEventArgs e)
        {
            //string path = System.IO.Path.GetDirectoryName(assembName);
            
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                //maximizeImage.Source = new BitmapImage(new Uri(path + "\\maximize_32.png"));
                maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                //maximizeImage.Source = new BitmapImage(new Uri(path + "\\restore_32.png"));
                maximizeImage.Source = LoadEmbeddedImage("restore_32.png");
            }
            
        }
        */
        private void buttonClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
        private void CloseDialog(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true; // Properly closes the dialog
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
                    new Warning("Not Found", $"Could not find:\n{_revealPath}", "Warning").ShowDialog();
            }
            catch (Exception ex)
            {
                new Warning("Could Not Open Folder", ex.Message, "Warning").ShowDialog();
            }
        }


        
    }
}
