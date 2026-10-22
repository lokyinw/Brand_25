using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    /// <summary>
    /// Collects the spacing between masses and the row length (both mm) for
    /// Luna_CreateMassFromExcel. No Cancel button - X closes (returns false).
    /// </summary>
    public partial class Input_MassLayout : Window
    {
        public double SpacingMm { get; private set; }
        public double RowLengthMm { get; private set; }

        public Input_MassLayout(double spacingMm, double rowLengthMm, string footerText = "Default")
        {
            InitializeComponent();

            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");
            FooterText.Text = footerText;

            SpacingBox.Text = spacingMm.ToString("0.##", CultureInfo.CurrentCulture);
            RowLengthBox.Text = rowLengthMm.ToString("0.##", CultureInfo.CurrentCulture);
        }

        private static bool TryParse(string text, out double value)
        {
            text = (text ?? "").Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void ButtonOk_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParse(SpacingBox.Text, out double spacing) || spacing < 0)
            {
                new Warning("Oops...", "Spacing must be a number in millimetres (0 or more).", FooterText.Text).ShowDialog();
                return;
            }
            if (!TryParse(RowLengthBox.Text, out double rowLength) || rowLength <= 0)
            {
                new Warning("Oops...", "Row length must be a number in millimetres, greater than 0.", FooterText.Text).ShowDialog();
                return;
            }

            SpacingMm = spacing;
            RowLengthMm = rowLength;
            DialogResult = true;
            Close();
        }

        private void buttonClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
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
