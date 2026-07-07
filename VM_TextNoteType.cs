using Autodesk.Revit.DB;

namespace Brand_25
{
    public class VM_TextNoteType
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

        // Comparison key — built from whichever attributes were selected
        public string ComparisonKey { get; set; }

        // Instance counts — populated by the command before showing Dialog 2
        public int InstanceCount { get; set; }
        public int InstancesInGroups { get; set; }

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