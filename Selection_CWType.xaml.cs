using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows.Input;

namespace Brand_25
{
    public partial class Selection_CWType : Window
    {
        public List<WallType> SelectedWallTypes { get; private set; } = new List<WallType>();
        private string _credit;

        public Selection_CWType(List<WallType> wallTypes, string title, string credit = "CW_Select Default")
        {
            InitializeComponent();
            _credit = credit;

            TitleText.Text = title;
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            if (wallTypes.Count == 0)
            {
                new Warning("No Items", "There are no items to select.", _credit).ShowDialog();
                Close();
            }

            CurtainWallList.ItemsSource = wallTypes;
            CurtainWallList.DisplayMemberPath = "Name";
        }

        private void SelectWallTypes(object sender, RoutedEventArgs e)
        {
            SelectedWallTypes = CurtainWallList.SelectedItems.Cast<WallType>().ToList();

            if (SelectedWallTypes.Count == 0)
            {
                new Warning("No Selection", "You did not select any items.", _credit).ShowDialog();
                return;
            }

            DialogResult = true;
            Close();
        }

        // ... keep all existing helper methods (LoadEmbeddedImage, window controls etc) ...
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
        private void buttonClose_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
        private void CloseDialog(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true; // Properly closes the dialog
            this.Close();
        }
    }
}