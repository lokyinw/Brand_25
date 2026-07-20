using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;

namespace Brand_25
{
    public partial class Selection_TagTypes : Window
    {
        public FamilySymbol SelectedWindowTagType { get; private set; }
        public FamilySymbol SelectedDoorTagType { get; private set; }

        // Defaults match WinEle4_Tag_Windows__R4__Py3.dyn's own saved selections —
        // used here only as a starting suggestion; falls back to the first available
        // type of the relevant tag category if not found in this project.
        private const string DefaultWindowTagFamily = "TAG_Wall#asWindow#";
        private const string DefaultWindowTagType = "2mm Window";
        private const string DefaultDoorTagFamily = "TAG_Door";
        private const string DefaultDoorTagType = "Standard";

        public Selection_TagTypes(List<FamilySymbol> wallTagTypes, List<FamilySymbol> doorTagTypes, string credit = "Selection_TagTypes Default")
        {
            InitializeComponent();
            FooterText.Text = credit;

            List<VM_TagType> windowItems = wallTagTypes.Select(t => new VM_TagType(t)).OrderBy(v => v.Name).ToList();
            WindowTagCombo.ItemsSource = windowItems;
            WindowTagCombo.SelectedItem = windowItems.FirstOrDefault(v =>
                    v.Symbol.Family.Name == DefaultWindowTagFamily && v.Symbol.Name == DefaultWindowTagType)
                ?? windowItems.FirstOrDefault();

            List<VM_TagType> doorItems = doorTagTypes.Select(t => new VM_TagType(t)).OrderBy(v => v.Name).ToList();
            DoorTagCombo.ItemsSource = doorItems;
            DoorTagCombo.SelectedItem = doorItems.FirstOrDefault(v =>
                    v.Symbol.Family.Name == DefaultDoorTagFamily && v.Symbol.Name == DefaultDoorTagType)
                ?? doorItems.FirstOrDefault();

            icon.Source = LoadEmbeddedImage("B_icon_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            VM_TagType windowVm = WindowTagCombo.SelectedItem as VM_TagType;
            VM_TagType doorVm = DoorTagCombo.SelectedItem as VM_TagType;

            if (windowVm == null && WindowTagCombo.Items.Count > 0)
            {
                MessageBox.Show("Please select a window tag type.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (doorVm == null && DoorTagCombo.Items.Count > 0)
            {
                MessageBox.Show("Please select a door tag type.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SelectedWindowTagType = windowVm?.Symbol;
            SelectedDoorTagType = doorVm?.Symbol;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void buttonClose_Click(object sender, RoutedEventArgs e) => Close();
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        private BitmapImage LoadEmbeddedImage(string imageName)
        {
            string resourcePath = $"Brand_25.Resources.Images.{imageName}";
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath);
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
