using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    public enum MarkTieBreak
    {
        SmallerArea,
        LargerArea,
        SmallerRoomNumber,
        LargerRoomNumber
    }

    public partial class Selection_PhaseDO : Window
    {
        // Display labels for the tie-break bullets, in the order they appear in the dialog.
        // Kept in one place so the label <-> enum mapping can't drift out of sync.
        private static readonly (string Label, MarkTieBreak Value)[] TieBreakOptions = new[]
        {
            ("Smaller Room (Area)", MarkTieBreak.SmallerArea),
            ("Larger Room (Area)", MarkTieBreak.LargerArea),
            ("Smaller Room Number", MarkTieBreak.SmallerRoomNumber),
            ("Larger Room Number", MarkTieBreak.LargerRoomNumber),
        };

        public string SelectedPhase { get; private set; }
        public string SelectedDesignOption { get; private set; }
        public MarkTieBreak SelectedTieBreak { get; private set; }

        // Only meaningful when this dialog was constructed with showNumericFields:true
        // (i.e. the elevation-creation command). Left at their pre-filled defaults if
        // the numeric fields were never shown.
        public double SelectedSideExtentMm { get; private set; }
        public double SelectedTopExtentMm { get; private set; }
        public double SelectedBottomExtentMm { get; private set; }
        public double SelectedFarClipOffsetMm { get; private set; }
        public double SelectedMarkerOffsetMm { get; private set; }
        public double SelectedMarkerRadius { get; private set; }

        private readonly bool _showNumericFields;
        private List<CheckBox> _tieBreakCheckBoxes = new List<CheckBox>();

        public Selection_PhaseDO(
                string title,
                string instruction,
                List<string> phases,
                List<string> designOptions,
                string credit = "Default Warning",
                string preSelectedPhase = null,
                string preSelectedDesignOption = null,
                MarkTieBreak preSelectedTieBreak = MarkTieBreak.SmallerArea,
                bool showNumericFields = false,
                double sideExtentMm = 300.0,
                double topExtentMm = 300.0,
                double bottomExtentMm = -500.0,
                double farClipOffsetMm = 1500.0,
                double markerOffsetMm = -1000.0,
                double markerRadius = 50.0)
        {
            InitializeComponent();
            TitleText.Text = title;
            InstructionText.Text = instruction;
            FooterText.Text = credit;
            _showNumericFields = showNumericFields;

            // Load images
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            // Populate Phase / Design Option combo boxes
            foreach (string phase in phases)
                PhaseComboBox.Items.Add(phase);
            PhaseComboBox.SelectedItem = preSelectedPhase != null && phases.Contains(preSelectedPhase)
                ? preSelectedPhase
                : phases.FirstOrDefault();

            foreach (string designOption in designOptions)
                DesignOptionComboBox.Items.Add(designOption);
            DesignOptionComboBox.SelectedItem = preSelectedDesignOption != null && designOptions.Contains(preSelectedDesignOption)
                ? preSelectedDesignOption
                : designOptions.FirstOrDefault();

            if (_showNumericFields)
            {
                // Elevation-creation mode: numeric crop/marker fields instead of tie-break bullets.
                // Crop visibility and isolate-in-view are NOT asked here — Win_CreateElevations
                // always wants the crop line hidden and the element isolated, so there's
                // nothing for the user to decide.
                TieBreakContainer.Visibility = Visibility.Collapsed;
                NumericFieldsContainer.Visibility = Visibility.Visible;

                SideExtentBox.Text = sideExtentMm.ToString(CultureInfo.InvariantCulture);
                TopExtentBox.Text = topExtentMm.ToString(CultureInfo.InvariantCulture);
                BottomExtentBox.Text = bottomExtentMm.ToString(CultureInfo.InvariantCulture);
                FarClipBox.Text = farClipOffsetMm.ToString(CultureInfo.InvariantCulture);
                MarkerOffsetBox.Text = markerOffsetMm.ToString(CultureInfo.InvariantCulture);
                MarkerRadiusBox.Text = markerRadius.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                // Tie-break mode (Win_AssignMark): build the bullet selector, same as before.
                foreach (var option in TieBreakOptions)
                {
                    CheckBox checkBox = new CheckBox
                    {
                        Content = option.Label,
                        Style = (Style)FindResource("CheckBoxStyle"),
                        IsChecked = option.Value == preSelectedTieBreak
                    };
                    checkBox.Checked += OnTieBreakChecked;
                    _tieBreakCheckBoxes.Add(checkBox);
                    TieBreakPanel.Children.Add(checkBox);
                }

                if (!_tieBreakCheckBoxes.Any(cb => cb.IsChecked == true))
                    _tieBreakCheckBoxes[0].IsChecked = true;
            }
        }

        private void OnTieBreakChecked(object sender, RoutedEventArgs e)
        {
            CheckBox currentCheckBox = (CheckBox)sender;
            foreach (CheckBox cb in _tieBreakCheckBoxes)
            {
                if (cb != currentCheckBox)
                    cb.IsChecked = false;
            }
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

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void buttonClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CloseDialog(object sender, RoutedEventArgs e)
        {
            SelectedPhase = PhaseComboBox.SelectedItem as string;
            SelectedDesignOption = DesignOptionComboBox.SelectedItem as string;

            if (_showNumericFields)
            {
                if (!TryParseNumericFields())
                    return; // validation failed — a message box was already shown, keep the dialog open
            }
            else
            {
                CheckBox selected = _tieBreakCheckBoxes.FirstOrDefault(cb => cb.IsChecked == true);
                int index = selected != null ? _tieBreakCheckBoxes.IndexOf(selected) : 0;
                SelectedTieBreak = TieBreakOptions[index].Value;
            }

            DialogResult = true;
            Close();
        }

        private bool TryParseNumericFields()
        {
            (string label, TextBox box, Action<double> assign)[] fields =
            {
                ("Side Extent", SideExtentBox, v => SelectedSideExtentMm = v),
                ("Top Extent", TopExtentBox, v => SelectedTopExtentMm = v),
                ("Bottom Extent", BottomExtentBox, v => SelectedBottomExtentMm = v),
                ("Far Clip Offset", FarClipBox, v => SelectedFarClipOffsetMm = v),
                ("Marker Offset", MarkerOffsetBox, v => SelectedMarkerOffsetMm = v),
                ("Marker Circle Scale", MarkerRadiusBox, v => SelectedMarkerRadius = v),
            };

            foreach (var field in fields)
            {
                if (!double.TryParse(field.box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    MessageBox.Show($"\"{field.label}\" must be a number.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
                field.assign(value);
            }

            return true;
        }
    }
}
