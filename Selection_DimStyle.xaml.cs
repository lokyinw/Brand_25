using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Controls;

namespace Brand_25
{
    public partial class Selection_DimStyle : Window
    {
        private readonly string _credit;

        public DimensionType SelectedDimensionType { get; private set; }

        public Selection_DimStyle(List<DimensionType> dimTypes, string credit = "Selection_DimStyle Default")
        {
            _credit = credit;
            InitializeComponent();

            TitleText.Text = "Select Dimension Style";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            DimStyleListBox.ItemsSource = dimTypes;

            DimensionType standard = dimTypes.FirstOrDefault(dt => dt.Name == "Standard");
            DimStyleListBox.SelectedItem = standard;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (!(DimStyleListBox.SelectedItem is DimensionType selected))
            {
                MessageBox.Show("Please select a dimension style.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SelectedDimensionType = selected;
            this.DialogResult = true;
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
                    throw new System.Exception($"Embedded resource not found: {resourcePath}");

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
