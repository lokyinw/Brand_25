using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;

namespace Brand_25
{
    public partial class Selection_SheetLayout : Window
    {
        public string SheetNumber { get; private set; }
        public ViewFamilyType SelectedViewFamilyType { get; private set; }
        public Phase SelectedPhase { get; private set; }
        public double LeftMarginMm { get; private set; }
        public double XSpacingMm { get; private set; }
        public double YSpacingMm { get; private set; }
        public double ReturnWidthMm { get; private set; }
        public double LowerMarginMm { get; private set; }

        public Selection_SheetLayout(List<ViewFamilyType> elevationTypes, List<Phase> phases, string credit = "Selection_SheetLayout Default")
        {
            InitializeComponent();
            TitleText.Text = "Place Internal Elevations on Sheet";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            // Pre-fill defaults matching the original Dynamo script's hardcoded values.
            SheetNumberBox.Text = "A160";
            LeftMarginBox.Text = "50";
            XSpacingBox.Text = "-40";
            YSpacingBox.Text = "20";
            ReturnWidthBox.Text = "720";
            LowerMarginBox.Text = "20";

            List<ViewFamilyType> orderedTypes = elevationTypes.OrderBy(t => t.Name).ToList();
            ViewTypeCombo.ItemsSource = orderedTypes;
            ViewTypeCombo.SelectedItem = orderedTypes.FirstOrDefault(t => t.Name.IndexOf("Internal Elevation", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? orderedTypes.FirstOrDefault();

            List<Phase> orderedPhases = phases.ToList(); // keep project phase sequence order
            PhaseCombo.ItemsSource = orderedPhases;
            PhaseCombo.SelectedItem = orderedPhases.FirstOrDefault(p => p.Name.IndexOf("New Construction", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? orderedPhases.LastOrDefault();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            List<string> problems = new List<string>();

            if (string.IsNullOrWhiteSpace(SheetNumberBox.Text))
                problems.Add("Enter a starting sheet number.");

            if (ViewTypeCombo.SelectedItem == null)
                problems.Add("Select an elevation view type.");

            if (PhaseCombo.SelectedItem == null)
                problems.Add("Select a phase.");

            bool ParseNumber(System.Windows.Controls.TextBox box, string label, out double value)
            {
                if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    problems.Add($"{label} must be a number.");
                    return false;
                }
                return true;
            }

            ParseNumber(LeftMarginBox, "Left Margin", out double leftMargin);
            ParseNumber(XSpacingBox, "X Spacing", out double xSpacing);
            ParseNumber(YSpacingBox, "Y Spacing", out double ySpacing);
            ParseNumber(ReturnWidthBox, "Return Width", out double returnWidth);
            ParseNumber(LowerMarginBox, "Lower Margin", out double lowerMargin);

            if (problems.Count > 0)
            {
                ErrorText.Text = string.Join(" ", problems);
                ErrorText.Visibility = System.Windows.Visibility.Visible;
                return;
            }

            SheetNumber = SheetNumberBox.Text.Trim();
            SelectedViewFamilyType = ViewTypeCombo.SelectedItem as ViewFamilyType;
            SelectedPhase = PhaseCombo.SelectedItem as Phase;
            LeftMarginMm = leftMargin;
            XSpacingMm = xSpacing;
            YSpacingMm = ySpacing;
            ReturnWidthMm = returnWidth;
            LowerMarginMm = lowerMargin;

            this.DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void buttonMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void buttonMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            }
            else
            {
                this.WindowState = WindowState.Maximized;
                maximizeImage.Source = LoadEmbeddedImage("restore_32.png");
            }
        }

        private void buttonClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
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
