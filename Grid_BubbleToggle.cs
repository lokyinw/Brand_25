using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Brand_25
{
    [Transaction(TransactionMode.Manual)]
    public class Grid_BubbleToggle : IExternalCommand
    {
        private string credit = "Last Modified by Lok on 2026-08-12. Beta 0.91";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            if (doc.ActiveView.ViewType != ViewType.FloorPlan &&
                doc.ActiveView.ViewType != ViewType.CeilingPlan &&
                doc.ActiveView.ViewType != ViewType.AreaPlan)
            {
                new Warning("...um...", "Did you forget to set the Active View to a Plan before we proceed?", credit).ShowDialog();
                return Result.Failed;
            }

            try
            {
                List<Grid> selectedGrids = GetPreSelectedGrids(uiDoc);

                if (selectedGrids.Count == 0)
                {
                    new Warning("Toggle Grid Bubbles", "Select the grids.", credit).ShowDialog();
                    selectedGrids = GetUserSelectedGrids(uiDoc);
                }

                if (selectedGrids == null || selectedGrids.Count == 0)
                {
                    new Warning("Oops...", "No grids selected. We have to abort.", credit).ShowDialog();
                    return Result.Failed;
                }

                List<XYZ> fencePointsRaw = new List<XYZ>();

                Transaction sketchTrans = new Transaction(doc, "LW_Sketch Fence Line (temporary)");
                sketchTrans.Start();
                try
                {
                    while (true)
                    {
                        XYZ pt;
                        try
                        {
                            string prompt = fencePointsRaw.Count == 0
                                ? "Click the first fence point (Esc to cancel)"
                                : "Click next fence point, or press Esc to finish";
                            // No snapping — ObjectSnapTypes.None — so the fence lands
                            // exactly where clicked, not pulled onto nearby geometry.
                            pt = uiDoc.Selection.PickPoint(ObjectSnapTypes.None, prompt);
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            break;
                        }

                        if (fencePointsRaw.Count > 0)
                        {
                            XYZ prev = fencePointsRaw[fencePointsRaw.Count - 1];
                            if (prev.DistanceTo(pt) < 1e-9)
                            {
                                continue;
                            }

                            try
                            {
                                doc.Create.NewDetailCurve(doc.ActiveView, Line.CreateBound(prev, pt));
                                doc.Regenerate();
                            }
                            catch
                            {
                                // Purely cosmetic — keep collecting points regardless.
                            }
                        }

                        fencePointsRaw.Add(pt);
                    }
                }
                finally
                {
                    sketchTrans.RollBack();
                }

                if (fencePointsRaw.Count < 2)
                {
                    new Warning("Oops...", "Need at least two points to sketch a fence line. We have to abort.", credit).ShowDialog();
                    return Result.Failed;
                }

                List<Line> fenceSegments = new List<Line>();
                for (int i = 0; i < fencePointsRaw.Count - 1; i++)
                {
                    XYZ p0 = new XYZ(fencePointsRaw[i].X, fencePointsRaw[i].Y, 0);
                    XYZ p1 = new XYZ(fencePointsRaw[i + 1].X, fencePointsRaw[i + 1].Y, 0);
                    if (p0.DistanceTo(p1) < 1e-9) continue;
                    fenceSegments.Add(Line.CreateBound(p0, p1));
                }

                if (fenceSegments.Count == 0)
                {
                    new Warning("Oops...", "The fence line has no usable segments. We have to abort.", credit).ShowDialog();
                    return Result.Failed;
                }

                using (Transaction trans = new Transaction(doc, "LW_Toggle Grid Bubbles"))
                {
                    trans.Start();

                    int bubblesToggled = 0;
                    foreach (Grid grid in selectedGrids)
                    {
                        Curve gridCurve = grid.GetCurvesInView(DatumExtentType.ViewSpecific, uiDoc.ActiveView).FirstOrDefault();
                        if (gridCurve == null) continue;

                        XYZ gridStart2D = new XYZ(gridCurve.GetEndPoint(0).X, gridCurve.GetEndPoint(0).Y, 0);
                        XYZ gridEnd2D = new XYZ(gridCurve.GetEndPoint(1).X, gridCurve.GetEndPoint(1).Y, 0);
                        if (gridStart2D.DistanceTo(gridEnd2D) < 1e-9) continue;
                        Line gridLine2D = Line.CreateBound(gridStart2D, gridEnd2D);

                        HashSet<DatumEnds> endsToToggle = new HashSet<DatumEnds>();

                        foreach (Line fenceSegment in fenceSegments)
                        {
                            IntersectionResultArray intersectionResults;
                            SetComparisonResult result = gridLine2D.Intersect(fenceSegment, out intersectionResults);

                            if (result != SetComparisonResult.Overlap || intersectionResults == null) continue;

                            foreach (IntersectionResult ir in intersectionResults)
                            {
                                XYZ intersectionPoint = ir.XYZPoint;
                                double distanceToStart = intersectionPoint.DistanceTo(gridStart2D);
                                double distanceToEnd = intersectionPoint.DistanceTo(gridEnd2D);
                                endsToToggle.Add(distanceToStart < distanceToEnd ? DatumEnds.End0 : DatumEnds.End1);
                            }
                        }

                        foreach (DatumEnds end in endsToToggle)
                        {
                            ToggleGridBubble(grid, end, uiDoc.ActiveView);
                            bubblesToggled++;
                        }
                    }

                    trans.Commit();

                    if (bubblesToggled > 0)
                    {
                        new Warning("Success", $"Successfully toggled {bubblesToggled} grid bubbles!", credit).ShowDialog();
                    }
                    else
                    {
                        new Warning("Hmm...", "No grid bubbles were toggled. Make sure your fence line actually crosses your selected grids.", credit).ShowDialog();
                    }
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                new Warning("Error", $"An error occurred: {ex.Message}", credit).ShowDialog();
                return Result.Failed;
            }
        }

        private void ToggleGridBubble(Grid grid, DatumEnds end, View view)
        {
            bool isVisible = grid.IsBubbleVisibleInView(end, view);
            if (isVisible)
                grid.HideBubbleInView(end, view);
            else
                grid.ShowBubbleInView(end, view);
        }

        private List<Grid> GetPreSelectedGrids(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            return uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .OfType<Grid>()
                .ToList();
        }

        private List<Grid> GetUserSelectedGrids(UIDocument uidoc)
        {
            try
            {
                Selection sel = uidoc.Selection;
                IList<Element> pickedElements = sel.PickElementsByRectangle(new GridSelectionFilter(), "Select grids to modify bubble visibility");
                return pickedElements.OfType<Grid>().ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        public class GridSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Grid;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}