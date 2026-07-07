using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace Brand_25
{
    [Transaction(TransactionMode.Manual)]
    public class CW_DisallowJoin : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uidoc = uiApp.ActiveUIDocument;
            Document doc = uidoc.Document;
            string credit = "Last modified by Lok on 2025-03-30. Beta 0.91";

            // Get all curtain wall types
            List<WallType> curtainWallTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .Where(wt => wt.Kind == WallKind.Curtain)
                .ToList();

            if (curtainWallTypes.Count == 0)
            {
                new Warning("No Curtain Walls", "There are no curtain walls in this project.", credit).ShowDialog();
                return Result.Succeeded;
            }

            // Show selection dialog
            Selection_CWType selector = new Selection_CWType(
                curtainWallTypes,
                "Disallow Wall Joins - Select Curtain Wall Type(s)",
                credit);

            if (selector.ShowDialog() != true)
                return Result.Cancelled;

            // Process selected types
            List<ElementId> selectedTypeIds = selector.SelectedWallTypes.Select(wt => wt.Id).ToList();

            using (Transaction tx = new Transaction(doc, "Disallow Wall Joins for Curtain Walls"))
            {
                tx.Start();

                List<Wall> curtainWalls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>()
                    .Where(w => selectedTypeIds.Contains(w.WallType.Id))
                    .ToList();

                if (curtainWalls.Count == 0)
                {
                    new Warning("No Matching Walls", "No curtain walls match your selection.", credit).ShowDialog();
                    return Result.Succeeded;
                }

                foreach (Wall cw in curtainWalls)
                {
                    WallUtils.DisallowWallJoinAtEnd(cw, 0);
                    WallUtils.DisallowWallJoinAtEnd(cw, 1);
                }

                tx.Commit();
                new Warning("Break a leg!!!", $"Disallowed wall joins for {curtainWalls.Count} curtain walls.", credit).ShowDialog();
            }

            return Result.Succeeded;
        }
    }
}