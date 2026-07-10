using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace Brand_25
{
    [Transaction(TransactionMode.Manual)]
    public class Grid_PropagateStatus : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uidoc = uiApp.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;
            string credit = "Last Modified by Lok on 2025-03-30. Beta 0.91";

            // Validate active view
            if (activeView.ViewType != ViewType.FloorPlan && activeView.ViewType != ViewType.CeilingPlan)
            {
                new Warning("Oops...", "Set the Active View to a Floor Plan or a Ceiling Plan before proceeding.", credit).ShowDialog();

                if (activeView.ViewType == ViewType.DrawingSheet)
                {
                    new Warning("...um...", "By the way, a sheet is not a plan view.", credit).ShowDialog();
                }
                return Result.Failed;
            }

            // Get eligible views
            List<View> eligibleViews = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan)
                .Where(v => v.CanHaveTypeAssigned())
                .OrderBy(v => v.Name)
                .ToList();

            if (eligibleViews.Count == 0)
            {
                new Warning("No Views", "No eligible plan views found in project.", credit).ShowDialog();
                return Result.Succeeded;
            }

            // Show selection dialog
            Selection_ViewPlan selector = new Selection_ViewPlan(
                eligibleViews,
                "Propagate Grid Status - Select Target View(s)",
                credit);

            if (selector.ShowDialog() != true)
                return Result.Cancelled;

            // Process selected views
            List<View> selectedViews = selector.SelectedViews;
            if (selectedViews.Count == 0)
                return Result.Succeeded;

            // Execute the grid propagation
            PropagateGridStatus(doc, activeView, selectedViews, credit);

            return Result.Succeeded;
        }

        private void PropagateGridStatus(Document doc, View sourceView, List<View> targetViews, string credit)
        {
            using (Transaction tx = new Transaction(doc, "LW_Propagate Grid Status"))
            {
                tx.Start();

                IEnumerable<Grid> grids = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Grids)
                    .WhereElementIsNotElementType()
                    .Cast<Grid>();

                foreach (View targetView in targetViews)
                {
                    foreach (Grid grid in grids)
                    {
                        // Visibility
                        if (grid.IsHidden(sourceView))
                            targetView.HideElements(new List<ElementId> { grid.Id });
                        else
                            targetView.UnhideElements(new List<ElementId> { grid.Id });

                        // Bubble visibility
                        foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                        {
                            if (grid.IsBubbleVisibleInView(end, sourceView))
                                grid.ShowBubbleInView(end, targetView);
                            else
                                grid.HideBubbleInView(end, targetView);
                        }

                        // Curve position
                        IList<Curve> sourceCurves = grid.GetCurvesInView(DatumExtentType.ViewSpecific, sourceView);
                        if (sourceCurves.Count > 0)
                        {
                            IList<Curve> targetCurves = grid.GetCurvesInView(DatumExtentType.ViewSpecific, targetView);
                            if (targetCurves.Count > 0)
                            {
                                Curve sourceCurve = sourceCurves[0];
                                double targetZ = targetCurves[0].GetEndPoint(0).Z;

                                XYZ newStart = new XYZ(sourceCurve.GetEndPoint(0).X, sourceCurve.GetEndPoint(0).Y, targetZ);
                                XYZ newEnd = new XYZ(sourceCurve.GetEndPoint(1).X, sourceCurve.GetEndPoint(1).Y, targetZ);

                                grid.SetCurveInView(DatumExtentType.ViewSpecific, targetView, Line.CreateBound(newStart, newEnd));
                            }
                        }
                    }
                }

                tx.Commit();
                new Warning("Success", $"Grid status propagated to {targetViews.Count} views.", credit).ShowDialog();
            }
        }
    }
}