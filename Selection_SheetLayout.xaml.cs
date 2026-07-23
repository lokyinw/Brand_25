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
    // Unit convention used across this dialog and Elev_PlaceOnSheets.cs:
    //   - Properties suffixed "Mm"  hold raw human-entered millimeters (paper space).
    //   - Properties suffixed "Ft"  (computed elsewhere) hold that same paper-space
    //     value converted to Revit's internal feet via MmToFeet.
    //   - Properties with NO suffix are already in model-space
    //     Revit internal feet, exactly as the Revit API returns them — no conversion
    //     is applied. If a value has no suffix, it's model space; if it does, it's
    //     paper space, in whichever of the two paper units the suffix names.
    public partial class Selection_SheetLayout : Window
    {
        public string SheetNumber { get; private set; }
        public ViewFamilyType SelectedViewFamilyType { get; private set; }
        public Phase SelectedPhase { get; private set; }

        // Paper size.
        public double PaperWidthMm { get; private set; }
        public double PaperHeightMm { get; private set; }

        // Frame margins: distance from the paper edge to the title frame.
        public double FrameMarginTopMm { get; private set; }
        public double FrameMarginBottomMm { get; private set; }
        public double FrameMarginLeftMm { get; private set; }
        public double FrameMarginRightMm { get; private set; }

        // Content margins: distance from the title frame to the usable drawing area.
        // ContentMarginRightMm doubles as the "Drawing Information Area" width, since
        // on this title block that info block is what actually bounds the drawing
        // area on the right (it's wider than the frame's own right margin).
        public double ContentMarginTopMm { get; private set; }
        public double ContentMarginBottomMm { get; private set; }
        public double ContentMarginLeftMm { get; private set; }
        public double ContentMarginRightMm { get; private set; }

        // Viewport layout.
        public double XSpacingMm { get; private set; }
        public double YSpacingMm { get; private set; }

        // Paper-space length: how much of each level line remains visible beyond the
        // crop edge, after trimming. Consumed by Elev_PlaceOnSheets by computing each
        // line's actual natural overhang past the crop (measured per-view, since it
        // varies) and repositioning the endpoint so exactly this much remains — not a
        // relative trim amount, so this has a literal, testable meaning on paper.
        public double LevelExtensionMm { get; private set; }

        // dialogTitle / preferredTypeNameContains let this same dialog be reused by
        // both Elev_PlaceOnSheets (internal/room elevations) and Win_PlaceElevOnSheets
        // (window/door elevations) without either one seeing the other's wording or
        // default-selection bias. Defaults match the original, Elev_PlaceOnSheets-only
        // behavior exactly, so that command needed no changes at its call site.
        public Selection_SheetLayout(List<ViewFamilyType> elevationTypes, List<Phase> phases, string credit = "Selection_SheetLayout Default",
            string dialogTitle = "Place Internal Elevations on Sheet", string preferredTypeNameContains = "Internal Elevation")
        {
            InitializeComponent();
            TitleText.Text = dialogTitle;
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            // Pre-fill defaults measured from the firm's standard A1 title block.
            SheetNumberBox.Text = "A160";

            PaperWidthBox.Text = "841";
            PaperHeightBox.Text = "594";

            FrameMarginTopBox.Text = "21";
            FrameMarginBottomBox.Text = "21";
            FrameMarginLeftBox.Text = "30";
            FrameMarginRightBox.Text = "21";

            ContentMarginTopBox.Text = "20";
            ContentMarginBottomBox.Text = "10";
            ContentMarginLeftBox.Text = "25";
            ContentMarginRightBox.Text = "100"; // Drawing Information Area width: 841 - 720 - 21

            XSpacingBox.Text = "20";
            YSpacingBox.Text = "35";

            LevelExtensionBox.Text = "7.5";

            List<ViewFamilyType> orderedTypes = elevationTypes.OrderBy(t => t.Name).ToList();
            ViewTypeCombo.ItemsSource = orderedTypes;
            ViewTypeCombo.SelectedItem = (!string.IsNullOrEmpty(preferredTypeNameContains)
                    ? orderedTypes.FirstOrDefault(t => t.Name.IndexOf(preferredTypeNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    : null)
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

            ParseNumber(PaperWidthBox, "Paper Width", out double paperWidth);
            ParseNumber(PaperHeightBox, "Paper Height", out double paperHeight);

            ParseNumber(FrameMarginTopBox, "Frame Margin Top", out double frameTop);
            ParseNumber(FrameMarginBottomBox, "Frame Margin Bottom", out double frameBottom);
            ParseNumber(FrameMarginLeftBox, "Frame Margin Left", out double frameLeft);
            ParseNumber(FrameMarginRightBox, "Frame Margin Right", out double frameRight);

            ParseNumber(ContentMarginTopBox, "Content Margin Top", out double contentTop);
            ParseNumber(ContentMarginBottomBox, "Content Margin Bottom", out double contentBottom);
            ParseNumber(ContentMarginLeftBox, "Content Margin Left", out double contentLeft);
            ParseNumber(ContentMarginRightBox, "Drawing Info Area Width", out double contentRight);

            ParseNumber(XSpacingBox, "X Spacing", out double xSpacing);
            ParseNumber(YSpacingBox, "Y Spacing", out double ySpacing);

            ParseNumber(LevelExtensionBox, "Level Extension", out double levelExtension);

            if (problems.Count > 0)
            {
                ErrorText.Text = string.Join(" ", problems);
                ErrorText.Visibility = System.Windows.Visibility.Visible;
                return;
            }

            SheetNumber = SheetNumberBox.Text.Trim();
            SelectedViewFamilyType = ViewTypeCombo.SelectedItem as ViewFamilyType;
            SelectedPhase = PhaseCombo.SelectedItem as Phase;

            PaperWidthMm = paperWidth;
            PaperHeightMm = paperHeight;

            FrameMarginTopMm = frameTop;
            FrameMarginBottomMm = frameBottom;
            FrameMarginLeftMm = frameLeft;
            FrameMarginRightMm = frameRight;

            ContentMarginTopMm = contentTop;
            ContentMarginBottomMm = contentBottom;
            ContentMarginLeftMm = contentLeft;
            ContentMarginRightMm = contentRight;

            XSpacingMm = xSpacing;
            YSpacingMm = ySpacing;

            LevelExtensionMm = levelExtension;

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
