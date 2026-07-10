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
    public class Grid_TrimGrid : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Get the Revit application and document
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2025-03-30. Beta 0.91";

            // Ensure the active view is a plan view
            if (doc.ActiveView.ViewType != ViewType.FloorPlan && doc.ActiveView.ViewType != ViewType.CeilingPlan)
            {
                new Warning("...um...", "Did you forget to set the Active View to a Floor Plan or a Ceiling Plan before we proceed?", credit).ShowDialog();
                return Result.Failed;
            }

            try
            {
                // Ask the user to select a detail line
                new Warning("Trim/Extend Grids", "Select the new boundary.\nIt has to be a straight detail line.", credit).ShowDialog();
                Reference detailLineRef = uiDoc.Selection.PickObject(ObjectType.Element, new DetailLineSelectionFilter(), "Select a detail line...");
                DetailLine detailLine = doc.GetElement(detailLineRef) as DetailLine;
                if (detailLine == null)
                {
                    new Warning("...um...", "Have you selected a straight detail line?", credit).ShowDialog();
                    //message = "The selected element is not a detail line.";
                    return Result.Failed;
                }

                // Get the geometry curve of the detail line
                Line detailCurve0 = detailLine.GeometryCurve as Line;
                Line detailCurve = Line.CreateBound(new XYZ(detailCurve0.GetEndPoint(0).X, detailCurve0.GetEndPoint(0).Y, 0), new XYZ(detailCurve0.GetEndPoint(1).X, detailCurve0.GetEndPoint(1).Y, 0));

                // Ask the user to select grid lines using a rectangle
                new Warning("Trim/Extend Grids", "Which grid do you want to modify?\nRemember, only the 2D extent of this view will be modified.", credit).ShowDialog();
                List<Grid> gridLines = GetUserSelectedGrids(uiDoc);
                if (gridLines == null || gridLines.Count == 0)
                {
                    new Warning("Oops...", "No grids selected. We have to abort.", credit).ShowDialog();
                    return Result.Failed;
                }

                // Start a transaction to modify the document
                using (Transaction trans = new Transaction(doc, "LW_Trim/Extend Grid Lines"))
                {
                    trans.Start();

                    foreach (Grid gridLine in gridLines)
                    {
                        // Get the 2D extent of the grid line in the current view
                        Curve gridCurve0 = gridLine.GetCurvesInView(DatumExtentType.ViewSpecific, uiDoc.ActiveView).FirstOrDefault();// as Line;

                        if (!(gridCurve0 is Line))
                        {
                            new Warning("...um...", "There is an arc in the grids you selected. But I am not clever enough to process arc. You may wait for the next version or talk to Lok.", credit).ShowDialog();
                            continue;
                        }
                        Line gridCurve = Line.CreateBound(new XYZ(gridCurve0.GetEndPoint(0).X, gridCurve0.GetEndPoint(0).Y, 0), new XYZ(gridCurve0.GetEndPoint(1).X, gridCurve0.GetEndPoint(1).Y, 0));
                        double originalZ = gridCurve0.GetEndPoint(0).Z;
                        Line gridCurveUB = Line.CreateUnbound(gridCurve.GetEndPoint(0), gridCurve.Direction);

                        // Check if the grid line intersects with the detail line
                        IntersectionResultArray intersectionResults;
                        SetComparisonResult result = gridCurveUB.Intersect(detailCurve, out intersectionResults);

                        if (result == SetComparisonResult.Overlap)
                        {
                            // Trim or extend the grid line
                            XYZ intersectionPoint = intersectionResults.get_Item(0).XYZPoint;
                            Curve newGridCurve = AdjustCurve(gridCurve, detailCurve, intersectionPoint, uiDoc.Selection);
                            Curve newGridCurve0 = Line.CreateBound(new XYZ(newGridCurve.GetEndPoint(0).X, newGridCurve.GetEndPoint(0).Y, originalZ), new XYZ(newGridCurve.GetEndPoint(1).X, newGridCurve.GetEndPoint(1).Y, originalZ)) as Curve;

                            // Update the grid line's 2D extent
                            gridLine.SetCurveInView(DatumExtentType.ViewSpecific, uiDoc.ActiveView, newGridCurve0);
                        }
                    }

                    trans.Commit();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private Curve AdjustCurve(Curve gridCurve, Curve detailCurve, XYZ intersectionPoint, Selection selection)
        {
            // Get the start and end points of the grid line
            XYZ gridStart = gridCurve.GetEndPoint(0);
            XYZ gridEnd = gridCurve.GetEndPoint(1);

            // Determine which side of the detail line the selection is on
            XYZ selectionCenter = gridCurve.Evaluate(0.5, true);
            bool isSelectionOnStartSide = IsPointOnSideOfCurve(selectionCenter, detailCurve, gridStart);

            // Check if the grid line intersects the detail line (trim scenario)
            bool isStartAndEndOnSameSide = IsPointOnSideOfCurve(gridStart, detailCurve, gridEnd);

            if (!isStartAndEndOnSameSide)
            {
                // Trim scenario: end points are on different sides of the detail line
                if (isSelectionOnStartSide)
                {
                    // Retain the start side
                    return Line.CreateBound(gridStart, intersectionPoint);
                }
                else
                {
                    // Retain the end side
                    return Line.CreateBound(intersectionPoint, gridEnd);
                }
            }
            else
            {
                // Extend scenario: end points are on the same side of the detail line
                double distanceStart = detailCurve.Distance(gridStart);
                double distanceEnd = detailCurve.Distance(gridEnd);

                if (distanceStart > distanceEnd)
                {
                    // Extend from the start point
                    return Line.CreateBound(gridStart, intersectionPoint);
                }
                else
                {
                    // Extend from the end point
                    return Line.CreateBound(intersectionPoint, gridEnd);
                }
            }
        }

        private bool IsPointOnSideOfCurve(XYZ point, Curve curve, XYZ referencePoint)
        {
            // Determine which side of the curve the point lies on
            XYZ curveStart = curve.GetEndPoint(0);
            XYZ curveEnd = curve.GetEndPoint(1);
            XYZ curveDirection = (curveEnd - curveStart).Normalize();
            XYZ normal = XYZ.BasisZ.CrossProduct(curveDirection);

            XYZ vectorToPoint = (point - curveStart).Normalize();
            XYZ vectorToReference = (referencePoint - curveStart).Normalize();

            double dotPoint = vectorToPoint.DotProduct(normal);
            double dotReference = vectorToReference.DotProduct(normal);

            return Math.Sign(dotPoint) == Math.Sign(dotReference);
        }

        private List<Grid> GetUserSelectedGrids(UIDocument uidoc)
        {
            try
            {
                Selection sel = uidoc.Selection;
                IList<Reference> pickedRefs = sel.PickObjects(ObjectType.Element, new GridSelectionFilter(), "Select grids that you want to modify the extent.");

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

        public class DetailLineSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is CurveElement curveElement && curveElement.Category.Id.Value == (long)BuiltInCategory.OST_Lines;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        public class GridSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Grid;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}