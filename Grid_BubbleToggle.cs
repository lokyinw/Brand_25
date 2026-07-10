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
        private string credit = "Last Modified by Lok on 2025-04-30. Beta 0.90";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Get the Revit application and document
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // Ensure the active view is a plan view
            if (doc.ActiveView.ViewType != ViewType.FloorPlan &&
                doc.ActiveView.ViewType != ViewType.CeilingPlan &&
                doc.ActiveView.ViewType != ViewType.AreaPlan)
            {
                new Warning("...um...", "Did you forget to set the Active View to a Plan before we proceed?", credit).ShowDialog();
                return Result.Failed;
            }

            try
            {
                // Step 1: Select grids to modify
                new Warning("Toggle Grid Bubbles", "Select the grids.", credit).ShowDialog();
                List<Grid> selectedGrids = GetUserSelectedGrids(uiDoc);
                if (selectedGrids == null || selectedGrids.Count == 0)
                {
                    new Warning("Oops...", "No grids selected. We have to abort.", credit).ShowDialog();
                    return Result.Failed;
                }

                // Step 2: Select boundary line (detail line or grid)
                new Warning("Toggle Grid Bubbles", "Select a detail line or grid line that intersects your selected grids.\nBubbles closest to this line will be toggled.", credit).ShowDialog();
                Reference lineRef = uiDoc.Selection.PickObject(ObjectType.Element, new LineSelectionFilter(), "Select a detail line or grid line...");
                Element lineElement = doc.GetElement(lineRef);

                Curve boundaryCurve = null;
                if (lineElement is DetailLine detailLine)
                {
                    boundaryCurve = detailLine.GeometryCurve;
                }
                else if (lineElement is Grid grid)
                {
                    boundaryCurve = grid.GetCurvesInView(DatumExtentType.ViewSpecific, uiDoc.ActiveView).FirstOrDefault();
                }

                if (boundaryCurve == null)
                {
                    new Warning("...um...", "The selected element doesn't provide a valid curve.", credit).ShowDialog();
                    return Result.Failed;
                }

                // Flatten curves to 2D for intersection calculations
                Line boundaryLine2D = Line.CreateBound(
                    new XYZ(boundaryCurve.GetEndPoint(0).X, boundaryCurve.GetEndPoint(0).Y, 0),
                    new XYZ(boundaryCurve.GetEndPoint(1).X, boundaryCurve.GetEndPoint(1).Y, 0));

                Line boundaryLineUB = Line.CreateUnbound(boundaryLine2D.GetEndPoint(0), boundaryLine2D.Direction);

                // Start transaction
                using (Transaction trans = new Transaction(doc, "LW_Toggle Grid Bubbles"))
                {
                    trans.Start();

                    int bubblesToggled = 0;
                    foreach (Grid grid in selectedGrids)
                    {
                        // Get grid curve in current view
                        Curve gridCurve = grid.GetCurvesInView(DatumExtentType.ViewSpecific, uiDoc.ActiveView).FirstOrDefault();
                        if (gridCurve == null) continue;

                        // Flatten grid curve to 2D
                        Line gridLine2D = Line.CreateBound(
                            new XYZ(gridCurve.GetEndPoint(0).X, gridCurve.GetEndPoint(0).Y, 0),
                            new XYZ(gridCurve.GetEndPoint(1).X, gridCurve.GetEndPoint(1).Y, 0));

                        Line gridLineUB = Line.CreateUnbound(gridLine2D.GetEndPoint(0), gridLine2D.Direction);

                        // Check intersection
                        IntersectionResultArray intersectionResults;
                        SetComparisonResult result = gridLineUB.Intersect(boundaryLineUB, out intersectionResults);

                        if (result == SetComparisonResult.Overlap)
                        {
                            XYZ intersectionPoint = intersectionResults.get_Item(0).XYZPoint;

                            // Determine which bubble is closer to the intersection point
                            double distanceToStart = intersectionPoint.DistanceTo(gridLine2D.GetEndPoint(0));
                            double distanceToEnd = intersectionPoint.DistanceTo(gridLine2D.GetEndPoint(1));

                            // Toggle the closer bubble
                            if (distanceToStart < distanceToEnd)
                            {
                                // Start bubble is closer
                                ToggleGridBubble(grid, DatumEnds.End0, uiDoc.ActiveView);
                                bubblesToggled++;
                            }
                            else
                            {
                                // End bubble is closer
                                ToggleGridBubble(grid, DatumEnds.End1, uiDoc.ActiveView);
                                bubblesToggled++;
                            }
                        }
                    }

                    trans.Commit();

                    if (bubblesToggled > 0)
                    {
                        new Warning("Success", $"Successfully toggled {bubblesToggled} grid bubbles!", credit).ShowDialog();
                    }
                    else
                    {
                        new Warning("Hmm...", "No grid bubbles were toggled. Make sure your selected grids intersect with the boundary line.", credit).ShowDialog();
                    }
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User canceled the operation
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
            // Get current bubble visibility
            bool isVisible = grid.IsBubbleVisibleInView(end, view);

            // Set new visibility (toggle)
            if (isVisible)
            {
                grid.HideBubbleInView(end, view);
            }
            else
            {
                grid.ShowBubbleInView(end, view);
            }
        }

        private List<Grid> GetUserSelectedGrids(UIDocument uidoc)
        {
            try
            {
                Selection sel = uidoc.Selection;
                IList<Reference> pickedRefs = sel.PickObjects(ObjectType.Element, new GridSelectionFilter(), "Select grids to modify bubble visibility");

                List<Grid> grids = new List<Grid>();
                Document doc = uidoc.Document;

                foreach (Reference r in pickedRefs)
                {
                    Element elem = doc.GetElement(r);
                    if (elem is Grid grid)
                    {
                        grids.Add(grid);
                    }
                }

                return grids;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null; // User canceled selection
            }
        }

        public class GridSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Grid;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        public class LineSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is Grid) return true;
                if (elem is CurveElement curveElement && curveElement.Category.Id.Value == (long)BuiltInCategory.OST_Lines)
                    return true;
                return false;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}