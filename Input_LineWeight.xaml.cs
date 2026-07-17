using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    /// <summary>
    /// Interaction logic for Input_LineWeight.xaml
    /// Collects the elevation view type and outline line weight (1-16) to override,
    /// following the same embedded-image / dark title bar / brand footer conventions
    /// as Warning.xaml.
    /// </summary>
    public partial class Input_LineWeight : Window
    {
        // Exposed to the calling command after ShowDialog() returns true.
        public ViewFamilyType SelectedViewFamilyType { get; private set; }
        public int LineWeight { get; private set; }

        // True if the user clicked Restore (clear overrides) rather than OK (apply
        // the selected line weight). The command should branch on this after
        // ShowDialog() returns true.
        public bool IsRestore { get; private set; }

        public Input_LineWeight(List<ViewFamilyType> elevationTypes, string footerText = "Default")
        {
            InitializeComponent();

            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");
            FooterText.Text = footerText;

            ViewTypeCombo.ItemsSource = elevationTypes;
            if (elevationTypes.Count > 0)
            {
                ViewTypeCombo.SelectedIndex = 0;
            }

            // Revit line weights run 1-16; ComboBox default (index 0) is 1.
            LineWeightCombo.ItemsSource = Enumerable.Range(1, 16).ToList();
            LineWeightCombo.SelectedIndex = 0;
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

        private void buttonClose_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void ButtonOk_Click(object sender, RoutedEventArgs e)
        {
            if (ViewTypeCombo.SelectedItem == null)
            {
                new Warning("Oops...", "Please select an elevation view type.", FooterText.Text).ShowDialog();
                return;
            }
            if (LineWeightCombo.SelectedItem == null)
            {
                new Warning("Oops...", "Please select a line weight.", FooterText.Text).ShowDialog();
                return;
            }

            SelectedViewFamilyType = (ViewFamilyType)ViewTypeCombo.SelectedItem;
            LineWeight = (int)LineWeightCombo.SelectedItem;
            IsRestore = false;

            this.DialogResult = true;
            this.Close();
        }

        private void ButtonRestore_Click(object sender, RoutedEventArgs e)
        {
            // Restoring only needs the view type — no line weight to apply.
            if (ViewTypeCombo.SelectedItem == null)
            {
                new Warning("Oops...", "Please select an elevation view type.", FooterText.Text).ShowDialog();
                return;
            }

            SelectedViewFamilyType = (ViewFamilyType)ViewTypeCombo.SelectedItem;
            IsRestore = true;

            this.DialogResult = true;
            this.Close();
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
