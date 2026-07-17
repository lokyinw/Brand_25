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
    public partial class BulletPointSelector : Window
    {
        public List<string> SelectedItems { get; private set; } = new List<string>();
        private List<CheckBox> _checkBoxes = new List<CheckBox>();
        private bool _singleSelectionMode;
        private CheckBox _customCheckBox;
        private TextBox _customTextBox;

        // Below this many total items (bullet points + custom-input row, if any),
        // the checkbox grid stays a single column — a short either/or-style list
        // (e.g. 2 items) reads far better stacked than side-by-side, and short
        // lists don't need the two-column layout that longer ones (e.g. the 11
        // attribute checkboxes in Text_ConsolidateTypes) need to avoid scrolling.
        private const int TwoColumnThreshold = 5;

        public BulletPointSelector(
                string title,
                string instruction,
                List<string> bulletPoints,
                string credit = "Default Warning",
                bool showCustomInput = false,
                bool singleSelectionMode = false,
                List<string> preSelectedItems = null)
        {
            InitializeComponent();
            _singleSelectionMode = singleSelectionMode;
            TitleText.Text = title;
            InstructionText.Text = instruction;
            FooterText.Text = credit;

            // Load images
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            // Create dynamic checkboxes
            foreach (string item in bulletPoints)
            {
                bool isPreSelected = preSelectedItems != null && preSelectedItems.Contains(item);
                AddCheckBox(item, isPreSelected);
            }

            // Add custom input as last item if requested
            if (showCustomInput)
            {
                StackPanel customPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };

                _customCheckBox = new CheckBox
                {
                    Content = "Custom:",
                    Style = (Style)FindResource("CheckBoxStyle"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                _customTextBox = new TextBox
                {
                    Width = 200,
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                customPanel.Children.Add(_customCheckBox);
                customPanel.Children.Add(_customTextBox);

                CheckBoxPanel.Children.Add(customPanel);

                if (_singleSelectionMode)
                {
                    _customCheckBox.Checked += OnSingleSelectionChecked;
                }
            }

            // Two columns only once there are enough items to benefit from it —
            // short lists (e.g. a 2-item either/or choice) stay single-column.
            int totalItems = bulletPoints.Count + (showCustomInput ? 1 : 0);
            CheckBoxPanel.Columns = totalItems >= TwoColumnThreshold ? 2 : 1;
        }

        private void AddCheckBox(string content, bool isChecked = false)
        {
            CheckBox checkBox = new CheckBox
            {
                Content = content,
                Style = (Style)FindResource("CheckBoxStyle"),
                IsChecked = isChecked
            };

            if (_singleSelectionMode)
            {
                checkBox.Checked += OnSingleSelectionChecked;
            }

            _checkBoxes.Add(checkBox);
            CheckBoxPanel.Children.Add(checkBox);
        }

        private void OnSingleSelectionChecked(object sender, RoutedEventArgs e)
        {
            if (!_singleSelectionMode) return;

            CheckBox currentCheckBox = (CheckBox)sender;

            // Uncheck all other checkboxes
            foreach (CheckBox cb in _checkBoxes)
            {
                if (cb != currentCheckBox)
                {
                    cb.IsChecked = false;
                }
            }

            // Uncheck custom checkbox if it's not the current one
            if (_customCheckBox != null && _customCheckBox != currentCheckBox)
            {
                _customCheckBox.IsChecked = false;
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
            SelectedItems.Clear();

            if (_singleSelectionMode)
            {
                // In single selection mode, only one item can be selected
                var selected = _checkBoxes.FirstOrDefault(cb => cb.IsChecked == true);
                if (selected != null)
                {
                    SelectedItems.Add(selected.Content.ToString());
                }
                else if (_customCheckBox != null && _customCheckBox.IsChecked == true &&
                         !string.IsNullOrWhiteSpace(_customTextBox.Text))
                {
                    SelectedItems.Add(_customTextBox.Text.Trim());
                }
            }
            else
            {
                // Original multi-selection behavior
                SelectedItems.AddRange(
                    _checkBoxes.Where(cb => cb.IsChecked == true)
                               .Select(cb => cb.Content.ToString())
                );

                // Add custom text if checked and not empty
                if (_customCheckBox != null &&
                    _customCheckBox.IsChecked == true &&
                    !string.IsNullOrWhiteSpace(_customTextBox.Text))
                {
                    SelectedItems.Add(_customTextBox.Text.Trim());
                }
            }

            DialogResult = true;
            Close();
        }
    }
}
