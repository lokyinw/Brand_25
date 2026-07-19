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

        private List<CheckBox> _tieBreakCheckBoxes = new List<CheckBox>();

        public Selection_PhaseDO(
                string title,
                string instruction,
                List<string> phases,
                List<string> designOptions,
                string credit = "Default Warning",
                string preSelectedPhase = null,
                string preSelectedDesignOption = null,
                MarkTieBreak preSelectedTieBreak = MarkTieBreak.SmallerArea)
        {
            InitializeComponent();
            TitleText.Text = title;
            InstructionText.Text = instruction;
            FooterText.Text = credit;

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

            // Build tie-break bullets (single-selection, same pattern as BulletPointSelector)
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

            // Fall back to the first option if nothing matched the pre-selection
            if (!_tieBreakCheckBoxes.Any(cb => cb.IsChecked == true))
                _tieBreakCheckBoxes[0].IsChecked = true;
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

            CheckBox selected = _tieBreakCheckBoxes.FirstOrDefault(cb => cb.IsChecked == true);
            int index = selected != null ? _tieBreakCheckBoxes.IndexOf(selected) : 0;
            SelectedTieBreak = TieBreakOptions[index].Value;

            DialogResult = true;
            Close();
        }
    }
}
