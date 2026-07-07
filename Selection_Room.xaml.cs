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
using Autodesk.Revit.DB.Architecture;

namespace Brand_25
{
    public partial class Selection_Room : Window
    {
        private string _credit;
        public List<Room> SelectedRooms { get; private set; }

        public Selection_Room(List<Room> rooms, Phase currentPhase, DesignOption currentDesignOption, string credit = "Selection_Room Default")
        {
            _credit = credit;
            InitializeComponent();

            TitleText.Text = "Select Rooms to Re-center the Room Tag";
            FooterText.Text = credit;

            List<VM_Room> roomViewModels = rooms.Select(room => new VM_Room(room)).ToList();

            foreach (VM_Room viewModel in roomViewModels)
            {
                if (!viewModel.ShouldBeGrayedOut &&
                    viewModel.PhaseName != null && currentPhase != null &&
                    viewModel.PhaseName == currentPhase.Name &&
                    viewModel.DesignOptionName == (currentDesignOption?.Name ?? "Main Model"))
                {
                    viewModel.IsSelected = true;
                }
            }

            RoomsDataGrid.ItemsSource = roomViewModels;
            RoomsDataGrid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Number", System.ComponentModel.ListSortDirection.Ascending));

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");
        }

        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (RoomsDataGrid.SelectedItems.Count > 1 && sender is CheckBox clickedCheckBox)
            {
                bool newValue = clickedCheckBox.IsChecked == true;
                foreach (Object item in RoomsDataGrid.SelectedItems)
                {
                    if (item is VM_Room roomItem)
                    {
                        roomItem.IsSelected = newValue;
                    }
                }
                // Force refresh of checkboxes
                RoomsDataGrid.Items.Refresh();
            }
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            SelectedRooms = RoomsDataGrid.ItemsSource
                .Cast<VM_Room>()
                .Where(vm => vm.IsSelected)
                .Select(vm => vm.Room)
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
            foreach (VM_Room viewModel in RoomsDataGrid.ItemsSource.Cast<VM_Room>())
            {
                viewModel.IsSelected = false;
            }
            RoomsDataGrid.Items.Refresh();
        }

        //private void SelectNone_Click(object sender, RoutedEventArgs e)
        //{
        //    RoomsDataGrid.UnselectAll();
        //}

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