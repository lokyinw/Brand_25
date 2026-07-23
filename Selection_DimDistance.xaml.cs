using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    public partial class Selection_DimDistance : Window
    {
        private readonly string _credit;

        /// <summary>Distance 1 (closest ring) in millimetres — overall width / height.</summary>
        public double Distance1Mm { get; private set; }

        /// <summary>Distance 2 (mid ring) in millimetres — height tied to Level, used when curtain grids exist.</summary>
        public double Distance2Mm { get; private set; }

        /// <summary>Distance 3 (outer ring) in millimetres — curtain grid width dimension / door width tie.</summary>
        public double Distance3Mm { get; private set; }

        public Selection_DimDistance(string credit = "Selection_DimDistance Default")
        {
            _credit = credit;
            InitializeComponent();

            TitleText.Text = "Set Dimension Line Offset";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseMm(Dist1Box.Text, out double d1) ||
                !TryParseMm(Dist2Box.Text, out double d2) ||
                !TryParseMm(Dist3Box.Text, out double d3))
            {
                MessageBox.Show("Please enter a valid positive number (in millimetres) for all three distances.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Distance1Mm = d1;
            Distance2Mm = d2;
            Distance3Mm = d3;
            this.DialogResult = true;
        }

        private static bool TryParseMm(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void buttonMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void buttonMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                maximizeImage.Source = LoadEmbeddedImage("restore_32.png");
            }
        }

        private void buttonClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
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
    }
}
