using Autodesk.Revit.DB;
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
    /// Shows one duplicate group at a time and asks the user to pick the survivor.
    /// Call ShowDialog() — if result is true, read SurvivorSelections.
    /// </summary>
    public partial class Consolidate_TextNoteType : Window
    {
        // Key = redundant TextNoteType, Value = the survivor to replace it with
        public Dictionary<TextNoteType, TextNoteType> SurvivorSelections { get; private set; }
            = new Dictionary<TextNoteType, TextNoteType>();

        private readonly List<List<VM_TextNoteType>> _groups;
        private int _currentIndex = 0;
        private List<RadioButton> _radioButtons = new List<RadioButton>();

        public Consolidate_TextNoteType(List<List<VM_TextNoteType>> duplicateGroups,
                                        string credit = "Consolidate_TextNoteType Default")
        {
            _groups = duplicateGroups;

            InitializeComponent();
            FooterText.Text = credit;

            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            ShowGroup(0);
        }

        private void ShowGroup(int index)
        {
            _currentIndex = index;
            var group = _groups[index];

            TitleText.Text = $"Select Survivor — Group {index + 1} of {_groups.Count}";
            SubtitleText.Text = $"These {group.Count} types share identical compared attributes. Select the one to KEEP.";

            // Rebuild radio buttons for this group
            SurvivorPanel.Children.Clear();
            _radioButtons.Clear();

            foreach (var vm in group.OrderBy(v => v.TypeName))
            {
                string label = $"{vm.TypeName}    [{vm.TextFont}, {vm.TextSize}]" +
                               $"    Instances: {vm.InstanceCount}" +
                               (vm.InstancesInGroups > 0 ? $"  ⚠ {vm.InstancesInGroups} in groups" : "");

                var rb = new RadioButton
                {
                    Content = label,
                    Tag = vm,
                    FontFamily = new System.Windows.Media.FontFamily("Calibri"),
                    FontSize = 15,
                    Margin = new Thickness(5, 4, 5, 4),
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x27, 0x27, 0x27))
                };

                _radioButtons.Add(rb);
                SurvivorPanel.Children.Add(rb);
            }

            // Default: select the type with the most instances
            var defaultChoice = _radioButtons
                .OrderByDescending(rb => ((VM_TextNoteType)rb.Tag).InstanceCount)
                .FirstOrDefault();
            if (defaultChoice != null) defaultChoice.IsChecked = true;

            // Update navigation buttons
            BackButton.IsEnabled = index > 0;
            NextButton.Content = index == _groups.Count - 1 ? "Confirm" : "Next ›";
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (!RecordSelection()) return;

            if (_currentIndex == _groups.Count - 1)
            {
                // Last group — done
                DialogResult = true;
                Close();
            }
            else
            {
                ShowGroup(_currentIndex + 1);
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            // Record current selection before going back so it persists
            RecordSelection();
            ShowGroup(_currentIndex - 1);
        }

        private bool RecordSelection()
        {
            var selectedRb = _radioButtons.FirstOrDefault(rb => rb.IsChecked == true);
            if (selectedRb == null)
            {
                new Warning("No Selection", "Please select a type to keep before continuing.", "Consolidate_TextNoteType")
                    .ShowDialog();
                return false;
            }

            var survivor = (VM_TextNoteType)selectedRb.Tag;
            var group = _groups[_currentIndex];

            // Map every non-survivor in this group → survivor
            foreach (var vm in group)
            {
                if (vm.TextNoteType.Id != survivor.TextNoteType.Id)
                    SurvivorSelections[vm.TextNoteType] = survivor.TextNoteType;
            }

            return true;
        }

        private void CancelAll_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
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