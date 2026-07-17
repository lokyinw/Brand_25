using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Brand_25
{
    /// <summary>
    /// Single source of truth for every TextNoteType attribute the tool knows how to
    /// display and compare. Previously this list was hand-duplicated in three places
    /// (BulletPointSelector's bullet list, Report_DupTextNoteType's column mapping,
    /// and Text_ConsolidateTypes' comparison-key switch) — add/rename an attribute in
    /// one place and the others would silently drift out of sync. Everything now reads
    /// from this catalog instead.
    /// </summary>
    public class AttributeDefinition
    {
        public string DisplayName { get; }
        public string PropertyName { get; }          // VM_TextNoteType property, for DataGrid bindings
        public Func<VM_TextNoteType, string> Getter { get; }
        public bool DefaultSelected { get; }

        public AttributeDefinition(string displayName, string propertyName,
                                    Func<VM_TextNoteType, string> getter, bool defaultSelected)
        {
            DisplayName = displayName;
            PropertyName = propertyName;
            Getter = getter;
            DefaultSelected = defaultSelected;
        }
    }

    public static class TextNoteAttributeCatalog
    {
        // Order here is the order attributes appear in the selector and as columns.
        // DefaultSelected = true → pre-checked in the attribute picker.
        public static readonly List<AttributeDefinition> All = new List<AttributeDefinition>
        {
            new AttributeDefinition("Text Font",        nameof(VM_TextNoteType.TextFont),        vm => vm.TextFont, true),
            new AttributeDefinition("Text Size",         nameof(VM_TextNoteType.TextSize),         vm => RoundedSize(vm.TextSize), true),
            new AttributeDefinition("Bold",              nameof(VM_TextNoteType.Bold),              vm => vm.Bold, true),
            new AttributeDefinition("Italic",            nameof(VM_TextNoteType.Italic),            vm => vm.Italic, true),
            new AttributeDefinition("Underline",         nameof(VM_TextNoteType.Underline),         vm => vm.Underline, false),
            new AttributeDefinition("Width Factor",      nameof(VM_TextNoteType.WidthFactor),      vm => vm.WidthFactor, false),
            new AttributeDefinition("Background",        nameof(VM_TextNoteType.Background),        vm => vm.Background, true),
            new AttributeDefinition("Show Border",       nameof(VM_TextNoteType.ShowBorder),       vm => vm.ShowBorder, true),
            new AttributeDefinition("Line Weight",       nameof(VM_TextNoteType.LineWeight),       vm => vm.LineWeight, false),
            new AttributeDefinition("Leader Arrowhead",  nameof(VM_TextNoteType.LeaderArrowhead),  vm => vm.LeaderArrowhead, false),
            new AttributeDefinition("Leader Offset",     nameof(VM_TextNoteType.LeaderOffset),     vm => vm.LeaderOffset, false),
            new AttributeDefinition("Tab Size",          nameof(VM_TextNoteType.TabSize),           vm => vm.TabSize, false),
            new AttributeDefinition("Colour",            nameof(VM_TextNoteType.Colour),            vm => vm.Colour, true),
        };

        public static List<string> DisplayNames => All.Select(a => a.DisplayName).ToList();

        public static List<string> DefaultSelectedNames =>
            All.Where(a => a.DefaultSelected).Select(a => a.DisplayName).ToList();

        public static AttributeDefinition Find(string displayName) =>
            All.FirstOrDefault(a => a.DisplayName == displayName);

        // Strip the " mm" suffix and round to 2dp so floating-point noise doesn't
        // produce false non-matches between types with the same nominal size.
        private static string RoundedSize(string textSize)
        {
            string numeric = textSize.Replace(" mm", "").Trim();
            return double.TryParse(numeric, out double val) ? val.ToString("F2") : textSize;
        }

        /// <summary>
        /// Builds "Type Name" + one column per selected attribute + "Instances" / "In Groups"
        /// on the given DataGrid. Shared by Report_DupTextNoteType and Consolidate_TextNoteType
        /// so both windows always show the exact same table.
        /// </summary>
        public static void BuildStandardColumns(DataGrid grid, List<string> selectedAttributes)
        {
            AddTextColumn(grid, "Type Name", nameof(VM_TextNoteType.TypeName), "3*");

            foreach (string attrName in selectedAttributes)
            {
                var def = Find(attrName);
                if (def != null)
                    AddTextColumn(grid, def.DisplayName, def.PropertyName, "Auto");
            }

            AddTextColumn(grid, "Instances", nameof(VM_TextNoteType.InstanceCount), "Auto");
            AddTextColumn(grid, "In Groups", nameof(VM_TextNoteType.InstancesInGroups), "Auto");
        }

        /// <summary>
        /// Builds "Type Name" + EVERY catalog attribute + "Instances" / "In Groups".
        /// Columns for attributes the user didn't select to compare are still shown,
        /// but styled grey — those values are informational only and may legitimately
        /// differ within a duplicate group. Used by the merged overview/survivor-picker
        /// dialog so the user gets the full picture, not just what they checked.
        /// </summary>
        public static void BuildAllColumns(DataGrid grid, List<string> selectedAttributes)
        {
            AddTextColumn(grid, "Type Name", nameof(VM_TextNoteType.TypeName), "3*", isHighlighted: true);

            foreach (var def in All)
            {
                bool isSelected = selectedAttributes.Contains(def.DisplayName);
                AddTextColumn(grid, def.DisplayName, def.PropertyName, "Auto", isHighlighted: isSelected);
            }

            AddTextColumn(grid, "Instances", nameof(VM_TextNoteType.InstanceCount), "Auto", isHighlighted: true);
            AddTextColumn(grid, "In Groups", nameof(VM_TextNoteType.InstancesInGroups), "Auto", isHighlighted: true);
        }

        // ── Cell text styling ──────────────────────────────────────────────────
        // Edit these two lines to change how the "Duplicate Text Note Types" table
        // looks — GreyTextColor is the colour used for attribute columns that
        // weren't part of the comparison; TextAlignment controls whether cell text
        // is left/center/right aligned.
        private static readonly Color GreyTextColor = Color.FromRgb(0x9E, 0x9E, 0x9E); // lower the numbers to lighten
        private const TextAlignment CellTextAlignment = TextAlignment.Left;             // change to Center or Right

        private static void AddTextColumn(DataGrid grid, string header, string bindingPath, string width,
                                           bool isHighlighted = true)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(bindingPath),
                Width = ParseWidth(width),
                ElementStyle = BuildCellTextStyle(isHighlighted)
            });
        }

        private static Style BuildCellTextStyle(bool isHighlighted)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                new SolidColorBrush(isHighlighted
                    ? Color.FromRgb(0x27, 0x27, 0x27)   // matches the app's normal text colour
                    : GreyTextColor)));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, CellTextAlignment));
            return style;
        }

        private static DataGridLength ParseWidth(string width)
        {
            if (width == "Auto") return DataGridLength.Auto;
            if (width.EndsWith("*"))
            {
                return double.TryParse(width.TrimEnd('*'), out double stars)
                    ? new DataGridLength(stars, DataGridLengthUnitType.Star)
                    : new DataGridLength(1, DataGridLengthUnitType.Star);
            }
            return double.TryParse(width, out double px) ? new DataGridLength(px) : DataGridLength.Auto;
        }
    }
}
