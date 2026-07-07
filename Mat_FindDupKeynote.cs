using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace Brand_25
{
    [Transaction(TransactionMode.ReadOnly)]
    public class Mat_FindDupKeynote : IExternalCommand
    {
        private const string credit = "Last Modified by Lok on 2026-03-31. Beta 0.90";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            // 1. Collect all materials, sorted by name
            List<Material> materials = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Materials)
                .WhereElementIsNotElementType()
                .Cast<Material>()
                .OrderBy(m => m.Name)
                .ToList();

            if (!materials.Any())
            {
                new Warning("No Materials", "No materials found in the project.", credit).ShowDialog();
                return Result.Cancelled;
            }

            // 2. Group by keynote value, keep only groups with more than one material
            List<List<Material>> duplicateGroups = materials
                .GroupBy(m => GetParameterValue(m, "Keynote"))
                .Where(g => g.Key != null && g.Key != "" && g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();

            if (!duplicateGroups.Any())
            {
                new Warning("No Duplicates", "No materials with duplicate keynotes were found.", credit).ShowDialog();
                return Result.Succeeded;
            }

            // 3. Show report dialog
            new Report_DupMaterialKeynote(duplicateGroups, credit).ShowDialog();

            return Result.Succeeded;
        }

        private static string GetParameterValue(Material material, string paramName)
        {
            Parameter param = material.LookupParameter(paramName);
            return (param != null && param.HasValue) ? param.AsValueString() ?? "" : "";
        }
    }
}