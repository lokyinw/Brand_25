using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    public partial class Selection_PlanAlign : Window
    {
        private readonly string _credit;
        private readonly List<VM_PlanViewport> _allPlans;
        private readonly List<VM_PlanSheetGroup> _sheetGroups;

        public VM_PlanViewport SelectedSource { get; private set; }
        public List<VM_PlanViewport> SelectedTargets { get; private set; } = new List<VM_PlanViewport>();

        // allPlans: every plan viewport in the project. preSelectSource: the plan to
        // pre-select as source (null for none). activeViewIsSheet: false adds a red
        // warning before the source label.
        public Selection_PlanAlign(List<VM_PlanViewport> allPlans, VM_PlanViewport preSelectSource,
                                    string credit = "Selection_PlanAlign Default", bool activeViewIsSheet = true)
        {
            _credit = credit;
            _allPlans = allPlans;

            InitializeComponent();

            TitleText.Text = "Align Plans on Sheets";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            SourceLabel.Inlines.Clear();
            if (!activeViewIsSheet)
                SourceLabel.Inlines.Add(new Run("CURRENT VIEW IS NOT A SHEET. ") { Foreground = Brushes.Red, FontWeight = FontWeights.Bold });
            SourceLabel.Inlines.Add(new Run("Align plans to the setting-out of:"));

            // Every plan is a possible source (e.g. a key plan), largest first within each sheet.
            SourceCombo.ItemsSource = _allPlans
                .OrderBy(p => p.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(p => p.PaperAreaSqFt)
                .ToList();
            SourceCombo.DisplayMemberPath = "ComboLabel";
            SourceCombo.SelectedItem = preSelectSource;
            SourceCombo.SelectionChanged += SourceCombo_SelectionChanged;

            _sheetGroups = _allPlans
                .GroupBy(p => p.Sheet.Id)
                .Select(g => new VM_PlanSheetGroup(
                    g.First().Sheet,
                    g.OrderBy(p => p.ViewName, StringComparer.OrdinalIgnoreCase).ToList()))
                .OrderBy(g => g.Sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            TargetTree.ItemsSource = _sheetGroups;

            UpdateAvailability();
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAvailability();
        }

        // Greys out (and unticks) the source itself and every plan whose scale or
        // rotation differs from the source, and collapses sheets with nothing left to
        // pick; re-run whenever the source changes.
        private void UpdateAvailability()
        {
            VM_PlanViewport source = SourceCombo.SelectedItem as VM_PlanViewport;
            foreach (VM_PlanViewport plan in _allPlans)
            {
                bool isSource = source != null && plan.Viewport.Id == source.Viewport.Id;
                plan.IsSourceExcluded = isSource;
                plan.IncompatibleReason = (source == null || isSource) ? null : plan.GetIncompatibility(source);
            }

            foreach (VM_PlanSheetGroup group in _sheetGroups)
                group.RefreshExpansion();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (VM_PlanViewport plan in _allPlans)
                if (plan.CanSelect) plan.IsSelected = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (VM_PlanViewport plan in _allPlans)
                plan.IsSelected = false;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            SelectedSource = SourceCombo.SelectedItem as VM_PlanViewport;
            if (SelectedSource == null)
            {
                new Warning("Oops...", "Please select a source plan.", _credit).ShowDialog();
                return;
            }

            SelectedTargets = _allPlans.Where(p => p.IsSelected).ToList();
            if (SelectedTargets.Count == 0)
            {
                new Warning("Oops...", "Please select at least one target plan to align.", _credit).ShowDialog();
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
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
