using Autodesk.Revit.DB;

namespace Brand_25
{
    public class VM_DupMaterial
    {
        public Material Material { get; }
        public string Name => Material.Name;
        public string Keynote => GetParameterValue("Keynote");
        public string Comments => GetParameterValue("Comments");
        public string Description => GetParameterValue("Description");
        public string Colour => FormatColour(Material.Color);

        public VM_DupMaterial(Material material)
        {
            Material = material;
        }

        private string GetParameterValue(string paramName, string defaultValue = "—")
        {
            Parameter param = Material.LookupParameter(paramName);
            return (param != null && param.HasValue) ? param.AsValueString() ?? defaultValue : defaultValue;
        }

        private static string FormatColour(Color color)
        {
            if (color == null || !color.IsValid) return "—";
            return $"R:{color.Red} G:{color.Green} B:{color.Blue}";
        }
    }
}