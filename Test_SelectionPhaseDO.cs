using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace Brand_25
{
    // TEMPORARY: test harness for Selection_PhaseDO. Remove this command (and its
    // ribbon button in App.cs) once Win_AssignMark.cs is wired up for real.
    [Transaction(TransactionMode.ReadOnly)]
    public class Test_SelectionPhaseDO : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            // Real Phases from the model
            List<string> phases = doc.Phases
                .Cast<Phase>()
                .Select(p => p.Name)
                .ToList();

            // Real Design Options, formatted as "Option Set : Option Name" (matches
            // Revit's own project browser convention), plus "Main Model" for elements
            // with no DO assigned.
            List<string> designOptions = new List<string> { "Main Model" };
            designOptions.AddRange(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(DesignOption))
                    .Cast<DesignOption>()
                    .Select(d =>
                    {
                        ElementId setId = d.get_Parameter(BuiltInParameter.OPTION_SET_ID).AsElementId();
                        string setName = doc.GetElement(setId)?.Name ?? "Option Set";
                        return $"{setName} : {d.Name}";
                    })
            );

            var dlg = new Selection_PhaseDO(
                "Assign Mark - TEST",
                "Select Phase, Design Option, and naming rule:",
                phases,
                designOptions,
                "Brand_25 \u2013 Assign Mark");

            bool? result = dlg.ShowDialog();

            if (result == true)
            {
                TaskDialog.Show("Selection_PhaseDO Test Result",
                    $"Phase: {dlg.SelectedPhase}\n" +
                    $"Design Option: {dlg.SelectedDesignOption}\n" +
                    $"Tie-break: {dlg.SelectedTieBreak}");
            }
            else
            {
                TaskDialog.Show("Selection_PhaseDO Test Result", "Dialog was cancelled.");
            }

            return Result.Succeeded;
        }
    }
}
