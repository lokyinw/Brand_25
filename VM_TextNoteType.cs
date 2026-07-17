using Autodesk.Revit.DB;
using System.ComponentModel;

namespace Brand_25
{
    public class VM_TextNoteType : INotifyPropertyChanged
    {
        public TextNoteType TextNoteType { get; }

        // Always-shown columns
        public string TypeName => TextNoteType.Name;
        public string TextFont => GetParamString("Text Font");
        public string TextSize => FormatMm("Text Size");

        // Optional columns — populated always, shown conditionally
        public string Bold => GetParamString("Bold");
        public string Italic => GetParamString("Italic");
        public string Underline => GetParamString("Underline");
        public string WidthFactor => FormatDouble("Width Factor");
        public string Background => GetParamString("Background");
        public string ShowBorder => GetParamString("Show Border");
        public string LineWeight => GetParamInt("Line Weight");
        public string LeaderArrowhead => GetParamString("Leader Arrowhead");
        public string LeaderOffset => FormatMm("Leader/Border Offset");
        public string TabSize => FormatMm("Tab Size");
        public string Colour => FormatColour();

        // For duplicate-group row shading
        public bool GroupShade { get; set; }

        // Scopes the Survivor radio button's mutual exclusivity to just this row's
        // duplicate group, now that all groups are shown together in one grid.
        public string DuplicateGroupKey { get; set; }

        // Comparison key — built from whichever attributes were selected
        public string ComparisonKey { get; set; }

        // Set when the user picks this type as the survivor for its group in the
        // Consolidate_TextNoteType dialog. Backed by a field (not auto-property) and
        // raises PropertyChanged so that setting this from code — e.g. when the user
        // picks a different survivor and we need to un-check the rest of the group —
        // reliably updates the bound RadioButton even if its row container has been
        // recycled by DataGrid virtualization.
        private bool _isSurvivor;
        public bool IsSurvivor
        {
            get => _isSurvivor;
            set
            {
                if (_isSurvivor == value) return;
                _isSurvivor = value;
                OnPropertyChanged(nameof(IsSurvivor));
            }
        }

        // Instance counts — populated by the command before showing Dialog 2
        public int InstanceCount { get; set; }
        public int InstancesInGroups { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public VM_TextNoteType(TextNoteType tnt)
        {
            TextNoteType = tnt;
        }

        private string GetParamString(string paramName, string defaultValue = "—")
        {
            Parameter p = TextNoteType.LookupParameter(paramName);
            return (p != null && p.HasValue) ? p.AsValueString() ?? defaultValue : defaultValue;
        }

        private string GetParamInt(string paramName, string defaultValue = "—")
        {
            Parameter p = TextNoteType.LookupParameter(paramName);
            return (p != null && p.HasValue) ? p.AsInteger().ToString() : defaultValue;
        }

        private string FormatDouble(string paramName, string defaultValue = "—")
        {
            Parameter p = TextNoteType.LookupParameter(paramName);
            return (p != null && p.HasValue) ? p.AsDouble().ToString("F4") : defaultValue;
        }

        private string FormatMm(string paramName, string defaultValue = "—")
        {
            Parameter p = TextNoteType.LookupParameter(paramName);
            if (p == null || !p.HasValue) return defaultValue;
            double mm = p.AsDouble() * 304.8;
            return mm.ToString("F2") + " mm";
        }

        private string FormatColour()
        {
            Parameter p = TextNoteType.LookupParameter("Color");
            if (p == null || !p.HasValue) return "—";
            int raw = p.AsInteger();
            int r = raw & 0xFF;
            int g = (raw >> 8) & 0xFF;
            int b = (raw >> 16) & 0xFF;
            return $"R:{r} G:{g} B:{b}";
        }
    }
}