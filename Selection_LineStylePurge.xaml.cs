using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    /// <summary>
    /// Shown once at the start of Line_ConsolidateStyles, before any duplicate
    /// detection runs, whenever at least one line style is currently unused.
    /// Lists EVERY line style (used and unused) so the user has full context, but
    /// only unused, non-built-in styles (VM_LineStyle.CanBeDeleted) can actually
    /// be selected — everything else is greyed out and disabled. Pre-selects all
    /// deletable styles by default; the user can uncheck any of them before
    /// clicking Continue. Call ShowDialog() — if the result is true, read
    /// SelectedForDeletion.
    /// </summary>
    public partial class Selection_LineStylePurge : Window
    {
        private readonly List<VM_LineStyle> _allStyles;

        public List<VM_LineStyle> SelectedForDeletion =>
            _allStyles.Where(vm => vm.IsSelectedForDeletion).ToList();

        public Selection_LineStylePurge(List<VM_LineStyle> allStyles, string credit = "Selection_LineStylePurge Default")
        {
            _allStyles = allStyles;

            InitializeComponent();

            int deletableCount = allStyles.Count(vm => vm.CanBeDeleted);
            TitleText.Text = $"Purge Unused Line Styles — {deletableCount} of {allStyles.Count} style(s) deletable";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            // Pre-select every deletable style (unused, non-built-in) by default —
            // matches the tool's previous "delete unused for me" default, just now
            // adjustable per-style rather than all-or-nothing.
            foreach (VM_LineStyle vm in _allStyles)
                vm.IsSelectedForDeletion = vm.CanBeDeleted;

            StyleGrid.ItemsSource = _allStyles.OrderBy(v => v.Name).ToList();
        }

        private void DeleteCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is VM_LineStyle vm)
                vm.IsSelectedForDeletion = cb.IsChecked == true;
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
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

        private void buttonClose_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

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
}
