using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Brand_25
{
    [Transaction(TransactionMode.Manual)]
    public class Room_Recentre : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            string credit = "Last Modified by Lok on 2025-03-30. Nightly 0.91";

            try
            {
                // Collect all rooms in the project
                FilteredElementCollector collector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType();

                List<Room> rooms = collector.Cast<Room>().ToList();

                if (rooms.Count == 0)
                {
                    new Warning("Oops...", "No rooms found in the project.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Get the current phase
                View activeView = doc.ActiveView;
                Phase activePhase = null;

                Parameter phaseParam = activeView.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (phaseParam != null && phaseParam.HasValue)
                {
                    ElementId phaseId = phaseParam.AsElementId();
                    activePhase = doc.GetElement(phaseId) as Phase;
                }

                if (activePhase == null)
                {
                    // Assuming the latest phase is the one with the highest sequence number
                    activePhase = new FilteredElementCollector(doc)
                        .OfClass(typeof(Phase))
                        .Cast<Phase>()
                        .OrderByDescending(p => p.get_Parameter(BuiltInParameter.PHASE_SEQUENCE_NUMBER).AsInteger())
                        .FirstOrDefault();
                }

                // Get the active design option ID using the static method
                ElementId activeDesignOptionId = DesignOption.GetActiveDesignOptionId(doc);
                DesignOption currentDesignOption = null;

                // Retrieve the active design option if the ID is valid
                if (activeDesignOptionId != ElementId.InvalidElementId)
                {
                    currentDesignOption = doc.GetElement(activeDesignOptionId) as DesignOption;
                }

                // Show the room selection window
                Selection_Room roomSelectionWindow = new Selection_Room(rooms, activePhase, currentDesignOption, credit);
                bool? result = roomSelectionWindow.ShowDialog();

                if (result != true || roomSelectionWindow.SelectedRooms == null || roomSelectionWindow.SelectedRooms.Count == 0)
                {
                    return Result.Cancelled; // User canceled or didn't select any rooms
                }

                int movedRooms = 0;
                int skippedNoBoundingBox = 0;
                int skippedAlreadyCentered = 0;

                using (Transaction trans = new Transaction(doc, "LW_Move Rooms to Center"))
                {
                    trans.Start();

                    foreach (Room room in roomSelectionWindow.SelectedRooms)
                    {
                        // Skip rooms without a valid location point
                        if (!(room.Location is LocationPoint locPoint))
                        {
                            skippedNoBoundingBox++;
                            continue;
                        }

                        // Find the best insertion point: the center of the room's largest
                        // inscribed circle, i.e. the point farthest from every wall.
                        // For a simple rectangular room this converges to the same result
                        // as a plain bounding-box center, but it also handles L-shaped,
                        // U-shaped, or notched rooms correctly in the same pass.
                        XYZ centerXY = FindBestRoomCenter(room);
                        if (centerXY == null)
                        {
                            skippedNoBoundingBox++;
                            continue;
                        }

                        XYZ center = new XYZ(centerXY.X, centerXY.Y, locPoint.Point.Z);

                        // Compare only the X and Y coordinates of the points
                        if (!IsAlmostEqualTo2D(locPoint.Point, center))
                        {
                            // Move the room's insertion point to the computed center (X and Y only)
                            locPoint.Point = center;
                            movedRooms++;
                        }
                        else
                        {
                            skippedAlreadyCentered++;
                        }
                    }

                    trans.Commit();
                }

                // Compose the message for the Warning window
                //string messageText =
                //    $"{movedRooms} rooms were moved to their geometric centers.\n\n" +
                //    $"Skipped Rooms:\n" +
                //    $"- {skippedNoBoundingBox} rooms had no valid boundary or location point.\n" +
                //    $"- {skippedAlreadyCentered} rooms were already centered.";

                string messageText = $"{movedRooms} rooms were re-centered";

                // Show the Warning window with the composed message
                new Warning("Re-center Room Tags", messageText, credit).ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Show the TaskDialog for errors
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }

        // Helper method to compare only the X and Y coordinates of two points
        private bool IsAlmostEqualTo2D(XYZ point1, XYZ point2)
        {
            double tolerance = 1e-9; // Tolerance for floating-point comparison
            return Math.Abs(point1.X - point2.X) < tolerance && Math.Abs(point1.Y - point2.Y) < tolerance;
        }

        // Finds the point in the room that is farthest from every boundary wall —
        // i.e. the center of the room's "largest inscribed circle" (LIC).
        //
        // This uses an adaptive search (the same strategy as Mapbox's "polylabel"
        // algorithm): instead of testing every point on a uniform grid, it starts
        // with a coarse set of candidate cells, always expands the most promising
        // one first, and discards branches that can't possibly beat the best
        // point found so far. This finds the same answer as a brute-force grid
        // scan while evaluating far fewer candidate points, and it naturally
        // handles simple rectangular rooms just as well as L-shaped or notched
        // ones — no separate "is the geometric center good enough" check needed.
        private XYZ FindBestRoomCenter(Room room, double precision = 0.05)
        {
            // Get the room's boundary curves
            SpatialElementBoundaryOptions options = new SpatialElementBoundaryOptions();
            IList<IList<BoundarySegment>> boundaries = room.GetBoundarySegments(options);
            if (boundaries == null || boundaries.Count == 0) return null;

            List<Curve> curves = new List<Curve>();
            foreach (IList<BoundarySegment> boundary in boundaries)
            {
                foreach (BoundarySegment segment in boundary)
                {
                    curves.Add(segment.GetCurve());
                }
            }
            if (curves.Count == 0) return null;

            BoundingBoxXYZ bbox = room.get_BoundingBox(null);
            if (bbox == null) return null;

            double z = bbox.Min.Z;
            double minX = bbox.Min.X, minY = bbox.Min.Y;
            double width = bbox.Max.X - minX;
            double height = bbox.Max.Y - minY;
            if (width <= 0 || height <= 0) return null;

            // Signed distance to the boundary: positive inside the room, negative outside.
            // (Distance-to-boundary alone doesn't say which side of the wall you're on,
            // so IsPointInRoom supplies the sign.)
            double SignedDistance(double x, double y)
            {
                XYZ pt = new XYZ(x, y, z);
                double dist = curves.Min(c => c.Distance(pt));
                return room.IsPointInRoom(pt) ? dist : -dist;
            }

            double cellSize = Math.Min(width, height);
            double h = cellSize / 2.0;

            // Best point found so far, seeded with the bounding-box center. For a
            // simple rectangular room this is already the optimal answer, so the
            // search below will confirm it and stop almost immediately.
            double bestX = minX + width / 2.0;
            double bestY = minY + height / 2.0;
            double bestD = SignedDistance(bestX, bestY);

            // Max-heap of candidate cells, ordered by the best distance any point
            // inside that cell could possibly achieve (cell.Distance + cell.HalfSize * sqrt(2)).
            var queue = new PriorityQueue<(double x, double y, double half, double d), double>();

            void Enqueue(double x, double y, double half)
            {
                double d = SignedDistance(x, y);
                double potential = d + half * Math.Sqrt(2);
                queue.Enqueue((x, y, half, d), -potential); // negate: PriorityQueue is min-heap by default
                if (d > bestD)
                {
                    bestX = x;
                    bestY = y;
                    bestD = d;
                }
            }

            // Seed an initial coarse grid of cells covering the whole bounding box
            for (double x = minX; x < minX + width; x += cellSize)
            {
                for (double y = minY; y < minY + height; y += cellSize)
                {
                    Enqueue(x + h, y + h, h);
                }
            }

            while (queue.Count > 0)
            {
                queue.TryDequeue(out var cell, out double negPotential);
                double potential = -negPotential;

                // No remaining cell (this one included) can beat the current best
                // by more than the requested precision — nothing left worth exploring.
                if (potential - bestD <= precision) break;

                // Split into 4 quadrant cells and keep searching
                double half = cell.half / 2.0;
                Enqueue(cell.x - half, cell.y - half, half);
                Enqueue(cell.x + half, cell.y - half, half);
                Enqueue(cell.x - half, cell.y + half, half);
                Enqueue(cell.x + half, cell.y + half, half);
            }

            // bestD > 0 confirms the point actually lands inside the room, not just
            // "closest among a bad set" for a degenerate/self-intersecting boundary.
            return bestD > 0 ? new XYZ(bestX, bestY, z) : null;
        }
    }
}