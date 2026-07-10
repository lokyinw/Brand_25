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
                int skippedCenterOutsideRoom = 0;
                int roomsUsingLIC = 0;

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

                        // Get the bounding box of the room
                        BoundingBoxXYZ bbox = room.get_BoundingBox(null);
                        if (bbox == null)
                        {
                            skippedNoBoundingBox++;
                            continue;
                        }

                        // Compute the geometric center (X and Y only)
                        XYZ center = new XYZ((bbox.Min.X + bbox.Max.X) / 2, (bbox.Min.Y + bbox.Max.Y) / 2, locPoint.Point.Z);

                        // Check if the computed center is inside the room
                        if (!room.IsPointInRoom(center))
                        {
                            // If the center is outside the room, find the largest inscribed circle (LIC) center
                            XYZ licCenter = FindLargestInscribedCircleCenter(room);
                            if (licCenter != null)
                            {
                                center = licCenter; // Use the LIC center as the new insertion point
                                roomsUsingLIC++;
                            }
                            else
                            {
                                skippedCenterOutsideRoom++;
                                continue; // Skip if no valid LIC center is found
                            }
                        }

                        // Compare only the X and Y coordinates of the points
                        if (!IsAlmostEqualTo2D(locPoint.Point, center))
                        {
                            // Move the room's insertion point to the computed center (X and Y only)
                            locPoint.Point = new XYZ(center.X, center.Y, locPoint.Point.Z);
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
                //    $"{movedRooms} rooms were moved to their geometric centers.\n" +
                //    $"{roomsUsingLIC} rooms used the Largest Inscribed Circle (LIC) method.\n\n" +
                //    $"Skipped Rooms:\n" +
                //    $"- {skippedNoBoundingBox} rooms had no valid bounding box.\n" +
                //    $"- {skippedAlreadyCentered} rooms were already centered.\n" +
                //    $"- {skippedCenterOutsideRoom} rooms had centers outside the room and no valid LIC center.";

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

        // Helper method to find the largest inscribed circle (LIC) center
        private XYZ FindLargestInscribedCircleCenter(Room room)
        {
            // Get the room's boundary curves
            SpatialElementBoundaryOptions options = new SpatialElementBoundaryOptions();
            IList<IList<BoundarySegment>> boundaries = room.GetBoundarySegments(options);
            if (boundaries == null || boundaries.Count == 0) return null;

            // Flatten the boundary segments into a list of curves
            List<Curve> curves = new List<Curve>();
            foreach (IList<BoundarySegment> boundary in boundaries)
            {
                foreach (BoundarySegment segment in boundary)
                {
                    curves.Add(segment.GetCurve());
                }
            }

            // Generate a grid of points within the room's bounding box
            BoundingBoxXYZ bbox = room.get_BoundingBox(null);
            if (bbox == null) return null;

            double step = 1.0; // Step size for the grid (adjust as needed)
            double maxDistance = 0;
            XYZ licCenter = null;

            for (double x = bbox.Min.X; x <= bbox.Max.X; x += step)
            {
                for (double y = bbox.Min.Y; y <= bbox.Max.Y; y += step)
                {
                    XYZ point = new XYZ(x, y, bbox.Min.Z);

                    // Check if the point is inside the room
                    if (room.IsPointInRoom(point))
                    {
                        // Calculate the minimum distance to the boundary
                        double minDistance = curves.Min(curve => curve.Distance(point));

                        // Update the LIC center if this point is farther from the boundary
                        if (minDistance > maxDistance)
                        {
                            maxDistance = minDistance;
                            licCenter = point;
                        }
                    }
                }
            }

            return licCenter;
        }
    }
}