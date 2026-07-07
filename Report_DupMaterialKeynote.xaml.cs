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
    public partial class Report_DupMaterialKeynote : Window
    {
        public Report_DupMaterialKeynote(List<List<Material>> duplicateGroups, string credit = "Report_DupMaterialKeynote Default")
        {
            InitializeComponent();

            TitleText.Text = $"Duplicate Material Keynotes — {duplicateGroups.Sum(g => g.Count)} materials across {duplicateGroups.Count} keynotes";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            // Flatten groups into rows, alternating GroupShade per group
            // so duplicate sets are visually distinct in the table
            var rows = new List<VM_DupMaterialRow>();
            bool shade = false;
            foreach (List<Material> group in duplicateGroups.OrderBy(g => g.First().Name))
            {
                foreach (Material mat in group.OrderBy(m => m.Name))
                    rows.Add(new VM_DupMaterialRow(mat, shade));
                shade = !shade;
            }

            MaterialsDataGrid.ItemsSource = rows;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
        private void buttonMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void buttonClose_Click(object sender, RoutedEventArgs e) => Close();

        private void buttonMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            maximizeImage.Source = LoadEmbeddedImage(
                WindowState == WindowState.Maximized ? "restore_32.png" : "maximize_32.png");
        }

        private BitmapImage LoadEmbeddedImage(string imageName)
        {
            string resourcePath = $"Brand_25.Resources.Images.{imageName}";
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath))
            {
                if (stream == null) throw new Exception($"Embedded resource not found: {resourcePath}");
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                return image;
            }
        }
    }

    // Thin wrapper adding GroupShade to VM_DupMaterial for row colouring
    public class VM_DupMaterialRow : VM_DupMaterial
    {
        public bool GroupShade { get; }

        public VM_DupMaterialRow(Material material, bool groupShade) : base(material)
        {
            GroupShade = groupShade;
        }
    }
}