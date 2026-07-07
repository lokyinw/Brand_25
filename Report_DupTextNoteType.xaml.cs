using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Brand_25
{
    public partial class Report_DupTextNoteType : Window
    {
        // True = user clicked Consolidate; false = Close
        public bool ProceedWithConsolidation { get; private set; } = false;

        private readonly List<List<VM_TextNoteType>> _duplicateGroups;
        private readonly List<string> _extraAttributes;

        public Report_DupTextNoteType(
            List<List<VM_TextNoteType>> duplicateGroups,
            List<string> extraAttributes,
            string credit = "Report_DupTextNoteType Default")
        {
            _duplicateGroups = duplicateGroups;
            _extraAttributes = extraAttributes;

            InitializeComponent();

            int typeCount = duplicateGroups.Sum(g => g.Count);
            int groupCount = duplicateGroups.Count;
            TitleText.Text = $"Duplicate Text Note Types — {typeCount} types across {groupCount} keynote groups";
            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            BuildColumns();
            BuildRows();
        }

        // ── Column builder ────────────────────────────────────────────────────

        private void BuildColumns()
        {
            // Always-present columns
            AddTextColumn("Type Name", nameof(VM_TextNoteType.TypeName), "3*");
            AddTextColumn("Text Font", nameof(VM_TextNoteType.TextFont), "2*");
            AddTextColumn("Text Size", nameof(VM_TextNoteType.TextSize), "Auto");

            // One column per extra attribute the user selected in Dialog 1
            foreach (string attr in _extraAttributes)
            {
                string binding = AttributeNameToProperty(attr);
                if (binding != null)
                    AddTextColumn(attr, binding, "Auto");
            }

            // Always-present instance count columns — key for the user's decisions
            AddTextColumn("Instances", nameof(VM_TextNoteType.InstanceCount), "Auto");
            AddTextColumn("In Groups", nameof(VM_TextNoteType.InstancesInGroups), "Auto");
        }

        private void AddTextColumn(string header, string bindingPath, string width)
        {
            var col = new DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(bindingPath),
                Width = ParseWidth(width)
            };
            TypesDataGrid.Columns.Add(col);
        }

        private static DataGridLength ParseWidth(string width)
        {
            if (width == "Auto") return DataGridLength.Auto;
            if (width.EndsWith("*"))
            {
                if (double.TryParse(width.TrimEnd('*'), out double stars))
                    return new DataGridLength(stars, DataGridLengthUnitType.Star);
                return new DataGridLength(1, DataGridLengthUnitType.Star);
            }
            if (double.TryParse(width, out double px))
                return new DataGridLength(px);
            return DataGridLength.Auto;
        }

        // Maps the display name shown in BulletPointSelector to the VM property name
        private static string AttributeNameToProperty(string displayName)
        {
            switch (displayName)
            {
                case "Bold": return nameof(VM_TextNoteType.Bold);
                case "Italic": return nameof(VM_TextNoteType.Italic);
                case "Underline": return nameof(VM_TextNoteType.Underline);
                case "Width Factor": return nameof(VM_TextNoteType.WidthFactor);
                case "Background": return nameof(VM_TextNoteType.Background);
                case "Show Border": return nameof(VM_TextNoteType.ShowBorder);
                case "Line Weight": return nameof(VM_TextNoteType.LineWeight);
                case "Leader Arrowhead": return nameof(VM_TextNoteType.LeaderArrowhead);
                case "Leader Offset": return nameof(VM_TextNoteType.LeaderOffset);
                case "Tab Size": return nameof(VM_TextNoteType.TabSize);
                case "Colour": return nameof(VM_TextNoteType.Colour);
                default: return null;
            }
        }

        // ── Row builder ───────────────────────────────────────────────────────

        private void BuildRows()
        {
            var rows = new List<VM_TextNoteType>();
            bool shade = false;

            foreach (var group in _duplicateGroups.OrderBy(g => g.First().TextFont)
                                                  .ThenBy(g => g.First().TextSize))
            {
                foreach (var vm in group.OrderBy(v => v.TypeName))
                {
                    vm.GroupShade = shade;
                    rows.Add(vm);
                }
                shade = !shade;
            }

            TypesDataGrid.ItemsSource = rows;
        }

        // ── Button handlers ───────────────────────────────────────────────────

        private void Consolidate_Click(object sender, RoutedEventArgs e)
        {
            ProceedWithConsolidation = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
        private void buttonMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void buttonClose_Click(object sender, RoutedEventArgs e) => Close();

        private void buttonMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            maximizeImage.Source = LoadEmbeddedImage(
                WindowState == WindowState.Maximized ? "restore_32.png" : "maximize_32.png");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

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