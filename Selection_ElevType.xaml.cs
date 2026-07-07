using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;

namespace Brand_25
{
    public partial class Selection_ElevType : Window
    {
        private string _credit;

        public ViewFamilyType SelectedElevType { get; private set; }

        public Selection_ElevType(List<ViewFamilyType> elevTypes, string credit = "Selection_ElevType Default")
        {
            _credit = credit;

            InitializeComponent();
            TitleText.Text = "Select Elevation Type";
            FooterText.Text = credit;  // Set footer text dynamically

            // Load elevation types into the ListBox
            IOrderedEnumerable<VM_ElevType> elevTypeViewModels = elevTypes.Select(et => new VM_ElevType(et)).ToList()
                .OrderBy(v => v.Name); // Sorting alphabetically;
            ElevTypesListBox.ItemsSource = elevTypeViewModels;

            // Load images from embedded resources
            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            VM_ElevType selectedVM = ElevTypesListBox.SelectedItem as VM_ElevType;
            if (selectedVM != null)
            {
                SelectedElevType = selectedVM.ElevType;
                this.DialogResult = true;
            }
            else
            {
                MessageBox.Show("Please select an elevation type.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

    //public class VM_ElevType
    //{
    //    public ViewFamilyType ElevType { get; }
    //    public string Name => ElevType.Name;
    //    //public string FamilyName => ElevType.FamilyName;

    //    public VM_ElevType(ViewFamilyType elevType)
    //    {
    //        ElevType = elevType;
    //    }
    //}
}