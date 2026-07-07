using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;

namespace Brand_25
{
    public partial class Selection_Sheet : Window
    {
        private string _credit;
        public List<ViewSheet> SelectedSheets { get; private set; }

        public Selection_Sheet(List<ViewSheet> sheets, string credit = "Selection_Sheet Default")
        {
            _credit = credit;
            InitializeComponent();

            TitleText.Text = "Select Sheets";
            FooterText.Text = credit;

            List<VM_Sheet> sheetViewModels = sheets.Select(sheet => new VM_Sheet(sheet)).ToList();
            SheetsDataGrid.ItemsSource = sheetViewModels;
            SheetsDataGrid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("SheetNumber", System.ComponentModel.ListSortDirection.Ascending));

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");
        }

        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (SheetsDataGrid.SelectedItems.Count > 1 && sender is CheckBox clickedCheckBox)
            {
                bool newValue = clickedCheckBox.IsChecked == true;
                foreach (Object item in SheetsDataGrid.SelectedItems)
                {
                    if (item is VM_Sheet sheetItem)
                    {
                        sheetItem.IsSelected = newValue;
                    }
                }
                // Force refresh of checkboxes
                SheetsDataGrid.Items.Refresh();
            }
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            SelectedSheets = SheetsDataGrid.ItemsSource
                .Cast<VM_Sheet>()
                .Where(vm => vm.IsSelected)
                .Select(vm => vm.Sheet)
                .ToList();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (VM_Sheet viewModel in SheetsDataGrid.ItemsSource.Cast<VM_Sheet>())
            {
                viewModel.IsSelected = false;
            }
            SheetsDataGrid.Items.Refresh();
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

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
        private void buttonMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void buttonMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            maximizeImage.Source = LoadEmbeddedImage(
                WindowState == WindowState.Maximized ? "restore_32.png" : "maximize_32.png");
        }

        private void buttonClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}