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
    /// Duplicate-line-style overview + survivor picker. Shows every candidate across
    /// every duplicate group in one table (shaded rows share a group), with a
    /// per-row Survivor radio button scoped to its own group via
    /// VM_LineStyle.DuplicateGroupKey, plus a per-row Retain Instances checkbox.
    /// The Survivor radio is never disabled — any style, including built-in ones,
    /// can be picked. Retain Instances (independent of Survivor) means "leave this
    /// style's instances alone"; everything else not retained gets reassigned to
    /// the survivor. Call ShowDialog() — if the result is true, read
    /// SurvivorSelections (styles to reassign+delete) and RetainedStyles (styles
    /// explicitly left untouched, for logging).
    /// </summary>
    public partial class Consolidate_LineStyle : Window
    {
        // Key = redundant line-style VM, Value = the survivor VM to replace it with.
        // Never contains a VM that had Retain Instances checked.
        public Dictionary<VM_LineStyle, VM_LineStyle> SurvivorSelections { get; private set; }
            = new Dictionary<VM_LineStyle, VM_LineStyle>();

        // Every non-survivor VM that had Retain Instances checked — for logging;
        // these are deliberately excluded from SurvivorSelections above.
        public List<VM_LineStyle> RetainedStyles { get; private set; } = new List<VM_LineStyle>();

        private readonly List<List<VM_LineStyle>> _groups;
        private readonly Dictionary<string, List<VM_LineStyle>> _groupsByKey =
            new Dictionary<string, List<VM_LineStyle>>();

        public Consolidate_LineStyle(List<List<VM_LineStyle>> duplicateGroups, string credit = "Consolidate_LineStyle Default")
        {
            _groups = duplicateGroups;

            InitializeComponent();

            int styleCount = duplicateGroups.Sum(g => g.Count);
            int groupCount = duplicateGroups.Count;
            TitleText.Text = $"Duplicate Line Styles — {styleCount} styles across {groupCount} groups";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            BuildRows();
        }

        private void BuildRows()
        {
            var rows = new List<VM_LineStyle>();
            bool shade = false;
            int groupIndex = 0;

            foreach (var group in _groups.OrderBy(g => g.First().Name))
            {
                string groupKey = $"SurvivorGroup_{groupIndex}";

                // Default to the style with the most instances overall — the
                // Survivor radio is never disabled, so any member (including a
                // built-in one) is a valid default; the user is always free to
                // change it.
                if (!group.Any(vm => vm.IsSurvivor))
                {
                    var defaultChoice = group.OrderByDescending(vm => vm.InstanceCount).FirstOrDefault();
                    if (defaultChoice != null) defaultChoice.IsSurvivor = true;
                }

                foreach (var vm in group.OrderBy(v => v.Name))
                {
                    vm.GroupShade = shade;
                    vm.DuplicateGroupKey = groupKey;
                    rows.Add(vm);
                }

                _groupsByKey[groupKey] = group;

                shade = !shade;
                groupIndex++;
            }

            LineStyleGrid.ItemsSource = rows;
        }

        // Owns Survivor exclusivity explicitly, keyed off the underlying data rather
        // than WPF's GroupName mechanism — see Consolidate_TextNoteType.xaml.cs for
        // why (GroupName-based grouping desynced under DataGrid row recycling).
        private void SurvivorRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb) || !(rb.DataContext is VM_LineStyle checkedVm))
                return;

            if (!_groupsByKey.TryGetValue(checkedVm.DuplicateGroupKey, out var members))
                return;

            foreach (var member in members)
                member.IsSurvivor = (member == checkedVm);
        }

        // Sets IsRetainInstances directly from the CheckBox's own state, rather
        // than relying solely on the XAML TwoWay binding to have committed by the
        // time Consolidate_Click reads it. A style with Retain Instances checked
        // was observed still being reassigned in practice — this makes the write
        // explicit and immediate on every toggle, the same reliable pattern
        // already used for the Survivor radio above, instead of trusting binding
        // timing alone.
        private void RetainInstancesCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb) || !(cb.DataContext is VM_LineStyle vm))
                return;

            vm.IsRetainInstances = cb.IsChecked == true;
        }

        private void Consolidate_Click(object sender, RoutedEventArgs e)
        {
            // Force any pending cell/row edit state in the grid to flush to the
            // bound VMs before reading Survivor/Retain state below — extra
            // insurance on top of the explicit event handlers above.
            LineStyleGrid.CommitEdit(DataGridEditingUnit.Row, true);
            LineStyleGrid.CommitEdit(DataGridEditingUnit.Cell, true);

            foreach (var group in _groups)
            {
                var survivor = group.FirstOrDefault(vm => vm.IsSurvivor);
                if (survivor == null)
                {
                    new Warning("Missing Selection",
                        "Please select a survivor for every duplicate group before continuing.",
                        "Consolidate_LineStyle").ShowDialog();
                    return;
                }

                foreach (var vm in group)
                {
                    if (vm.Category.Id == survivor.Category.Id) continue;

                    if (vm.IsRetainInstances)
                        RetainedStyles.Add(vm);
                    else
                        SurvivorSelections[vm] = survivor;
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
