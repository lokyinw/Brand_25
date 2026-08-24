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
    public partial class Selection_PlanAlign : Window
    {
        private readonly string _credit;
        private readonly List<VM_PlanViewport> _allPlans;
        private readonly List<VM_PlanSheetGroup> _sheetGroups;

        public VM_PlanViewport SelectedSource { get; private set; }
        public List<VM_PlanViewport> SelectedTargets { get; private set; } = new List<VM_PlanViewport>();

        // allPlans: every qualifying plan viewport in the project (one VM_PlanViewport
        // per placed plan viewport, regardless of how many sit on the same sheet).
        // preSelectSource: the (sheet, view)-largest VM_PlanViewport to pre-select in
        // the source combo, or null if the active view isn't a qualifying sheet.
        public Selection_PlanAlign(List<VM_PlanViewport> allPlans, VM_PlanViewport preSelectSource,
                                    string credit = "Selection_PlanAlign Default")
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

            // Source combo: one entry per sheet, showing only that sheet's LARGEST
            // (paper-space) plan viewport — matches the spec's "if more than one plan
            // on a sheet, pre-select the largest one" rule, applied here to which entry
            // represents that sheet in the combo at all (there's only one entry per
            // sheet either way, so "which one" and "which one is pre-selected for that
            // sheet" are the same choice).
            List<VM_PlanViewport> largestPerSheet = _allPlans
                .GroupBy(p => p.Sheet.Id)
                .Select(g => g.OrderByDescending(p => p.PaperAreaSqFt).First())
                .OrderBy(p => p.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            SourceCombo.ItemsSource = largestPerSheet;
            SourceCombo.DisplayMemberPath = "ComboLabel";
            SourceCombo.SelectedItem = preSelectSource != null
                ? largestPerSheet.FirstOrDefault(p => p.Sheet.Id == preSelectSource.Sheet.Id)
                : null;
            SourceCombo.SelectionChanged += SourceCombo_SelectionChanged;

            // Target tree: EVERY plan viewport, grouped by sheet — the full picture,
            // not just each sheet's largest plan (unlike the source combo above).
            _sheetGroups = _allPlans
                .GroupBy(p => p.Sheet.Id)
                .Select(g => new VM_PlanSheetGroup(
                    g.First().Sheet,
                    g.OrderBy(p => p.ViewName, StringComparer.OrdinalIgnoreCase).ToList()))
                .OrderBy(g => g.Sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            TargetTree.ItemsSource = _sheetGroups;

            UpdateSourceExclusion();
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSourceExclusion();
        }

        // Disables (and clears) the checkbox for whichever viewport is currently
        // selected as the source — a plan can't be aligned to itself.
        private void UpdateSourceExclusion()
        {
            VM_PlanViewport source = SourceCombo.SelectedItem as VM_PlanViewport;
            foreach (VM_PlanViewport plan in _allPlans)
            {
                plan.IsSourceExcluded = source != null && plan.Viewport.Id == source.Viewport.Id;
            }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (VM_PlanViewport plan in _allPlans)
                if (!plan.IsSourceExcluded) plan.IsSelected = true;
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
