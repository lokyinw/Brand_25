using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    /// <summary>
    /// Combined duplicate overview + survivor picker. Shows every candidate across
    /// every duplicate group in one table (shaded rows share a group), with a
    /// per-row Survivor radio button scoped to its own group via
    /// VM_TextNoteType.DuplicateGroupKey. Call ShowDialog() — if result is true,
    /// read SurvivorSelections.
    /// </summary>
    public partial class Consolidate_TextNoteType : Window
    {
        // Key = redundant TextNoteType, Value = the survivor to replace it with
        public Dictionary<TextNoteType, TextNoteType> SurvivorSelections { get; private set; }
            = new Dictionary<TextNoteType, TextNoteType>();

        private readonly List<List<VM_TextNoteType>> _groups;
        private readonly List<string> _selectedAttributes;
        private readonly Dictionary<string, List<VM_TextNoteType>> _groupsByKey =
            new Dictionary<string, List<VM_TextNoteType>>();

        public Consolidate_TextNoteType(List<List<VM_TextNoteType>> duplicateGroups,
                                        List<string> selectedAttributes,
                                        string credit = "Consolidate_TextNoteType Default")
        {
            _groups = duplicateGroups;
            _selectedAttributes = selectedAttributes;

            InitializeComponent();

            int typeCount = duplicateGroups.Sum(g => g.Count);
            int groupCount = duplicateGroups.Count;
            TitleText.Text = $"Duplicated Text Note Types — {typeCount} types across {groupCount} groups";
            SubtitleText.Text = "Select exactly one survivor per group — shaded rows share a group. " +
                                 "Grey columns are not selected for comparison, so those values may differ within a group.";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            TextNoteAttributeCatalog.BuildAllColumns(SurvivorGrid, _selectedAttributes);
            BuildRows();
        }

        private void BuildRows()
        {
            var rows = new List<VM_TextNoteType>();
            bool shade = false;
            int groupIndex = 0;

            foreach (var group in _groups.OrderBy(g => g.First().TextFont)
                                          .ThenBy(g => g.First().TextSize))
            {
                // Each duplicate group gets its own key so SurvivorRadio_Checked can
                // enforce "exactly one survivor" independently per group.
                string groupKey = $"SurvivorGroup_{groupIndex}";

                // Default to the type with the most instances, unless the user already
                // made a choice for this group (persists on the VM itself).
                if (!group.Any(vm => vm.IsSurvivor))
                {
                    var defaultChoice = group.OrderByDescending(vm => vm.InstanceCount).FirstOrDefault();
                    if (defaultChoice != null) defaultChoice.IsSurvivor = true;
                }

                foreach (var vm in group.OrderBy(v => v.TypeName))
                {
                    vm.GroupShade = shade;
                    vm.DuplicateGroupKey = groupKey;
                    rows.Add(vm);
                }

                _groupsByKey[groupKey] = group;

                shade = !shade;
                groupIndex++;
            }

            SurvivorGrid.ItemsSource = rows;
        }

        // Owns Survivor exclusivity explicitly, keyed off the underlying data rather
        // than WPF's GroupName mechanism — GroupName-based grouping across a single
        // virtualized DataGrid was found to desync when row containers got recycled
        // during scrolling, letting the wrong VM end up flagged as the survivor.
        private void SurvivorRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.RadioButton rb) ||
                !(rb.DataContext is VM_TextNoteType checkedVm))
                return;

            if (!_groupsByKey.TryGetValue(checkedVm.DuplicateGroupKey, out var members))
                return;

            foreach (var member in members)
                member.IsSurvivor = (member == checkedVm);
        }

        private void Consolidate_Click(object sender, RoutedEventArgs e)
        {
            foreach (var group in _groups)
            {
                var survivor = group.FirstOrDefault(vm => vm.IsSurvivor);
                if (survivor == null)
                {
                    new Warning("Missing Selection",
                        "Please select a survivor for every duplicate group before continuing.",
                        "Consolidate_TextNoteType").ShowDialog();
                    return;
                }

                foreach (var vm in group)
                {
                    if (vm.TextNoteType.Id != survivor.TextNoteType.Id)
                        SurvivorSelections[vm.TextNoteType] = survivor.TextNoteType;
                }
            }

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
