using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    // What the user asked for when the dialog closed. The command loops on
    // PickRooms (close -> PickObjects -> reopen) and proceeds on Create.
    public enum FloorFinishAction
    {
        None,
        PickRooms,
        Create
    }

    /// <summary>
    /// Modal dialog for Room_CreateFloorFinish. Deliberately modal: room picking
    /// happens between dialog rounds in the command, so no ExternalEvent is needed.
    /// All state lives in VM_FloorFinish so each round can rebuild this window.
    /// </summary>
    public partial class Selection_FloorFinish : Window
    {
        private readonly VM_FloorFinish _vm;
        private readonly ICollectionView _floorTypeView;

        public FloorFinishAction Action { get; private set; } = FloorFinishAction.None;

        public Selection_FloorFinish(VM_FloorFinish vm, string credit, IntPtr ownerHandle)
        {
            InitializeComponent();

            _vm = vm;
            DataContext = vm;

            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");
            FooterText.Text = credit;

            // Pin to Revit's main window (no TopMost).
            if (ownerHandle != IntPtr.Zero)
                new WindowInteropHelper(this).Owner = ownerHandle;

            // Reopen where the user left it on the previous round.
            if (!double.IsNaN(vm.WindowLeft) && !double.IsNaN(vm.WindowTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = vm.WindowLeft;
                Top = vm.WindowTop;
            }

            _floorTypeView = CollectionViewSource.GetDefaultView(vm.FloorTypes);
            _floorTypeView.Filter = FloorTypeFilter;
            FilterBox.Text = vm.FilterText ?? "";   // triggers TextChanged -> Refresh

            Loaded += (s, e) =>
            {
                if (FloorTypeListBox.SelectedItem != null)
                    FloorTypeListBox.ScrollIntoView(FloorTypeListBox.SelectedItem);
            };
            Closing += (s, e) =>
            {
                _vm.WindowLeft = Left;
                _vm.WindowTop = Top;
                _vm.FilterText = FilterBox.Text;
            };
        }

        private bool FloorTypeFilter(object item)
        {
            string filter = FilterBox?.Text;
            if (string.IsNullOrWhiteSpace(filter)) return true;
            return item is VM_FloorTypeItem ft &&
                   ft.Name.IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _floorTypeView?.Refresh();
        }

        private void SelectRooms_Click(object sender, RoutedEventArgs e)
        {
            Action = FloorFinishAction.PickRooms;
            DialogResult = true;   // closes the dialog; the command runs PickObjects
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedRoomIds.Count == 0)
            {
                new Warning("Oops...", "Please select at least one room.", FooterText.Text).ShowDialog();
                return;
            }
            if (_vm.SelectedFloorType == null)
            {
                new Warning("Oops...", "Please select a floor type.", FooterText.Text).ShowDialog();
                return;
            }
            if (!TryParseMillimetres(_vm.OffsetText, out double offsetMillimetres))
            {
                new Warning("Oops...", "The raise amount must be a number in millimetres, e.g. 0, 20 or -15.", FooterText.Text).ShowDialog();
                return;
            }

            _vm.OffsetMillimetres = offsetMillimetres;
            Action = FloorFinishAction.Create;
            DialogResult = true;
        }

        private static bool TryParseMillimetres(string text, out double value)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) { value = 0.0; return true; }   // blank = zero
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void buttonClose_Click(object sender, RoutedEventArgs e)
        {
            Action = FloorFinishAction.None;
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
