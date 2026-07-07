using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System;

namespace Brand_25
{
    public partial class Selection_ViewPlan : Window
    {
        public List<View> SelectedViews { get; private set; } = new List<View>();
        private string _credit;

        public Selection_ViewPlan(List<View> views, string title, string credit = "Selection_ViewPlan Default")
        {
            InitializeComponent();
            _credit = credit;

            TitleText.Text = title;
            FooterText.Text = credit;
            ApplyButton.Content = "Select Views"; // Generic button text

            // Populate the list
            viewSelector.ItemsSource = views;
            viewSelector.DisplayMemberPath = "Name";

            // Load images
            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedViews = viewSelector.SelectedItems.Cast<View>().ToList();

            if (SelectedViews.Count == 0)
            {
                new Warning("Oops...", "Please select at least one view.", _credit).ShowDialog();
                return;
            }

            DialogResult = true;
            Close();
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
            this.Close();
        }
        private void CloseDialog(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true; // Properly closes the dialog
            this.Close();
        }
    }
}