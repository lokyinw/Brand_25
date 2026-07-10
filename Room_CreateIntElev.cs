using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Brand_25
{
    [Transaction(TransactionMode.Manual)]
    public class Room_CreateIntElev : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2025-03-30. Beta 0.70";

            // Create a StringBuilder to log details
            StringBuilder log = new StringBuilder();

            try
            {
                // Step 1: Collect all elevation view family types
                List<ViewFamilyType> elevViewTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .Where(vft => vft.ViewFamily == ViewFamily.Elevation)
                    .ToList();

                if (elevViewTypes.Count == 0)
                {
                    TaskDialog.Show("Error", "No elevation view types found in the document.");
                    return Result.Cancelled;
                }

                // Step 2: Show elevation type selection window
                Selection_ElevType elevTypeWindow = new Selection_ElevType(elevViewTypes, credit);
                if (elevTypeWindow.ShowDialog() != true || elevTypeWindow.SelectedElevType == null)
                {
                    return Result.Cancelled;
                }
                ViewFamilyType selectedElevType = elevTypeWindow.SelectedElevType;

                // Step 3: Collect all rooms in the project
                List<Room> rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .ToList();

                if (rooms.Count == 0)
                {
                    new Warning("Oops...", "No rooms found in the project.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 4: Get the active design option
                ElementId activeDesignOptionId = DesignOption.GetActiveDesignOptionId(doc);
                DesignOption currentDesignOption = null;

                if (activeDesignOptionId != ElementId.InvalidElementId)
                {
                    currentDesignOption = doc.GetElement(activeDesignOptionId) as DesignOption;
                }

                // Step 5: Show the room selection window
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

                Selection_Room roomWindow = new Selection_Room(rooms, activePhase, currentDesignOption, credit);
                if (roomWindow.ShowDialog() != true || roomWindow.SelectedRooms == null || !roomWindow.SelectedRooms.Any())
                {
                    return Result.Cancelled;
                }

                // Step 6: Create internal elevations for selected rooms
                using (Transaction trans = new Transaction(doc, "LW_Create Internal Elevations"))
                {
                    trans.Start();

                    int totalElevationsCreated = 0; // Renamed variable
                    foreach (Room room in roomWindow.SelectedRooms)
                    {
                        log.AppendLine($"Processing Room: {room.Name} (ID: {room.Id})");

                        int elevationsCreated = CreateElevationsForRoom(doc, room, selectedElevType, log);
                        totalElevationsCreated += elevationsCreated;

                        log.AppendLine($"Created {elevationsCreated} elevations for Room: {room.Name} (ID: {room.Id})");
                        log.AppendLine();
                    }

                    trans.Commit();

                    // Show success message
                    new Warning("Success", $"{totalElevationsCreated} internal elevations created.", credit).ShowDialog();
                    log.AppendLine($"{totalElevationsCreated} internal elevations created.");
                }

                // Save the log to a text file
                string logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RoomElevationLog.txt");
                try
                {
                    File.WriteAllText(logFilePath, log.ToString());
                    TaskDialog.Show("Success", $"Log saved to: {logFilePath}");
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Error", $"Failed to save log file: {ex.Message}");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}\n\nDebug Log:\n{log}");
                return Result.Failed;
            }
        }

        private int CreateElevationsForRoom(Document doc, Room room, ViewFamilyType elevationType, StringBuilder log)
        {
            int createdElevations = 0;

            // Step 1: Find matching plan view for the room
            ViewPlan matchingView = FindMatchingView(doc, room, log);
            if (matchingView == null)
            {
                log.AppendLine("Error: No matching plan view found for the room.");
                TaskDialog.Show("Error", "No matching plan view found for the room.");
                return 0;
            }

            // Step 2: Get room boundary segments and filter out arcs
            IList<IList<BoundarySegment>> boundarySegments = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (boundarySegments == null || boundarySegments.Count == 0)
            {
                log.AppendLine("Error: No boundary segments found for the room.");
                TaskDialog.Show("Error", "No boundary segments found for the room.");
                return 0;
            }

            // Filter out arcs (curved walls)
            List<List<BoundarySegment>> filteredBoundarySegments = boundarySegments
                .Select(segmentList => segmentList
                    .Where(segment => !(segment.GetCurve() is Arc)) // Remove arcs
                    .ToList())
                .Where(segmentList => segmentList.Any()) // Remove empty segment lists
                .ToList();

            if (filteredBoundarySegments.Count == 0)
            {
                log.AppendLine("Error: No valid boundary segments (non-arc) found for the room.");
                TaskDialog.Show("Error", "No valid boundary segments (non-arc) found for the room.");
                return 0;
            }

            // Log boundary segments
            log.AppendLine($"Boundary Segments for Room {room.Name}:");
            foreach (List<BoundarySegment> segmentList in filteredBoundarySegments)
            {
                foreach (BoundarySegment segment in segmentList)
                {
                    Curve curve = segment.GetCurve();
                    log.AppendLine($"  Segment: {curve.GetEndPoint(0)} to {curve.GetEndPoint(1)}");
                }
            }

            // Step 3: Get room insertion point and level elevation
            LocationPoint location = room.Location as LocationPoint;
            if (location == null)
            {
                log.AppendLine("Error: Room does not have a valid location point.");
                TaskDialog.Show("Error", "Room does not have a valid location point.");
                return 0;
            }

            Level roomLevel = doc.GetElement(room.LevelId) as Level;
            if (roomLevel == null)
            {
                log.AppendLine("Error: Room does not have a valid level.");
                TaskDialog.Show("Error", "Room does not have a valid level.");
                return 0;
            }

            XYZ roomCenter = new XYZ(location.Point.X, location.Point.Y, roomLevel.Elevation);
            log.AppendLine($"Room Center: {roomCenter}");

            // Step 4: Group walls by orthogonal directions
            List<List<XYZ>> wallGroups = GroupWallsByOrthogonalDirections(filteredBoundarySegments.Cast<IList<BoundarySegment>>().ToList(), roomCenter, log);

            // Step 5: Create elevation markers for each group
            double markerOffsetDistance = 3.0; // Offset distance in meters
            int markerCount = 0;

            // Counter for naming elevations (shared across all groups for the same room)
            int elevationNumber = 1;

            foreach (List<XYZ> group in wallGroups)
            {
                // Ensure the group has at most 4 directions
                if (group.Count > 4)
                {
                    log.AppendLine("Error: A group has more than 4 directions. This is not supported.");
                    TaskDialog.Show("Error", "A group has more than 4 directions. This is not supported.");
                    continue;
                }

                // Create elevation marker at room center
                ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, elevationType.Id, roomCenter, 50);
                if (marker == null)
                {
                    log.AppendLine("Error: Failed to create elevation marker.");
                    TaskDialog.Show("Error", "Failed to create elevation marker.");
                    continue;
                }

                log.AppendLine($"Created Elevation Marker: {marker.Id}");

                // Step 6: Create a dummy elevation at index 0
                ViewSection dummyElevation = marker.CreateElevation(doc, matchingView.Id, 2);
                if (dummyElevation == null)
                {
                    log.AppendLine("Error: Failed to create dummy elevation.");
                    TaskDialog.Show("Error", "Failed to create dummy elevation.");
                    continue;
                }
                log.AppendLine($"Created Dummy Elevation: {dummyElevation.Name} (ID: {dummyElevation.Id})");

                // Step 7: Rotate the marker to align with the first wall direction
                XYZ firstWallDirection = group[0];
                RotateMarker(doc, marker, firstWallDirection, roomCenter, log);
                log.AppendLine($"Rotated Marker {marker.Id} to align with wall direction: {firstWallDirection}");

                // Query and log the ViewDirection of the dummy elevation
                XYZ dummyElevationViewDirection = dummyElevation.ViewDirection;
                log.AppendLine($"Dummy Elevation ViewDirection: {dummyElevationViewDirection}");

                // Step 8: Create elevations for each wall in the group, except the firstWallDirection
                int createdElevationsInGroup = 0;
                for (int i = 0; i < group.Count; i++)
                {
                    if (i == 0) continue; // Skip the first wall direction (already handled by the dummy elevation)

                    XYZ wallDirection = group[i];
                    int elevationIndex = GetElevationIndex(wallDirection, firstWallDirection, log);
                    log.AppendLine($"Creating elevation for wall direction: {wallDirection} (Index: {elevationIndex})");

                    try
                    {
                        // Create the elevation view
                        ViewSection elevationView = marker.CreateElevation(doc, matchingView.Id, elevationIndex);
                        if (elevationView != null)
                        {
                            createdElevationsInGroup++;
                            createdElevations++; // Increment the total counter

                            // Rename the elevation view
                            RenameElevation(elevationView, room, elevationNumber);
                            log.AppendLine($"Created Elevation View: {elevationView.Name} (ID: {elevationView.Id}) towards {elevationView.ViewDirection}");
                        }
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine($"Error creating elevation for index {elevationIndex}: {ex.Message}");
                        TaskDialog.Show("Error", $"Failed to create elevation for index {elevationIndex}: {ex.Message}");
                    }
                    finally
                    {
                        // Increment the elevation number regardless of success or failure
                        elevationNumber++;
                    }
                }

                // Step 9: Handle the dummy elevation based on the number of elevations created
                if (createdElevationsInGroup > 0)
                {
                    // Multiple elevations in this group: Delete the dummy elevation and re-create the real elevation at index 0
                    Element dummy = dummyElevation as Element;
                    doc.Delete(dummy.Id);
                    //log.AppendLine($"Deleted Dummy Elevation: {dummyElevation.Name} (ID: {dummyElevation.Id})");

                    // Re-create the real elevation at index 2
                    ViewSection realElevation = marker.CreateElevation(doc, matchingView.Id, 2);
                    if (realElevation != null)
                    {
                        createdElevations++; // Increment the total counter
                        RenameElevation(realElevation, room, elevationNumber);
                        log.AppendLine($"Created Real Elevation at Index 0: {realElevation.Name} (ID: {realElevation.Id}) towards {realElevation.ViewDirection}");
                        elevationNumber++;
                    }
                }
                else
                {
                    // Only one elevation in this group: Create another dummy elevation at index 1
                    ViewSection dummyElevation2 = marker.CreateElevation(doc, matchingView.Id, 1);
                    if (dummyElevation2 == null)
                    {
                        log.AppendLine("Error: Failed to create second dummy elevation.");
                        TaskDialog.Show("Error", "Failed to create second dummy elevation.");
                        continue;
                    }
                    log.AppendLine($"Created Second Dummy Elevation: {dummyElevation2.Name} (ID: {dummyElevation2.Id})");

                    // Delete the dummy elevation at index 2
                    Element dummy0 = dummyElevation as Element;
                    doc.Delete(dummy0.Id);
                    //log.AppendLine($"Deleted Dummy Elevation: {dummyElevation.Name} (ID: {dummyElevation.Id})");

                    // Re-create the real elevation at index 2
                    ViewSection realElevation = marker.CreateElevation(doc, matchingView.Id, 2);
                    if (realElevation != null)
                    {
                        createdElevations++; // Increment the total counter
                        RenameElevation(realElevation, room, elevationNumber);
                        log.AppendLine($"Created Real Elevation at Index 0: {realElevation.Name} (ID: {realElevation.Id})");
                        elevationNumber++;
                    }

                    // Delete the second dummy elevation at index 1
                    Element dummy1 = dummyElevation2 as Element;
                    doc.Delete(dummy1.Id);
                    //log.AppendLine($"Deleted Second Dummy Elevation: {dummyElevation2.Name} (ID: {dummyElevation2.Id})");
                }

                // Step 10: Move secondary markers by 1 meter in the index 0 direction
                if (markerCount > 0)
                {
                    XYZ offsetDirection = GetMarkerDirection(0); // Marker's index 0 direction (east)
                    XYZ offset = offsetDirection * markerOffsetDistance;
                    ElementTransformUtils.MoveElement(doc, marker.Id, offset);
                    log.AppendLine($"Moved Marker {marker.Id} by {offset}");
                }

                markerCount++;
            }

            log.AppendLine($"Total Elevations Created for Room {room.Name}: {createdElevations}");
            return createdElevations;
        }

        private void RotateMarker(Document doc, ElevationMarker marker, XYZ wallDirection, XYZ roomCenter, StringBuilder log)
        {
            // Step 1: Calculate the rotation angle
            //XYZ defaultDirection = XYZ.BasisX; // Default marker direction (east)
            //double angle = defaultDirection.AngleTo(wallDirection);

            // Step 1: Calculate the rotation angle using Atan2
            double angle = Math.Atan2(wallDirection.Y, wallDirection.X);

            // Log the calculated angle in degrees
            double angleInDegrees = angle * 180 / Math.PI;
            log.AppendLine($"Rotation Angle: {angleInDegrees}°");

            // Step 2: Define the rotation axis (Z-axis for 2D rotation)
            XYZ rotationAxisDirection = XYZ.BasisZ; // Rotate around the Z-axis
            Line rotationAxis = Line.CreateBound(roomCenter, roomCenter + rotationAxisDirection);

            // Step 3: Rotate the marker
            ElementTransformUtils.RotateElement(doc, marker.Id, rotationAxis, angle);
        }

        private int GetElevationIndex(XYZ wallDirection, XYZ firstWallDirection, StringBuilder log)
        {
            // Calculate the signed angle between the first wall direction and the current wall direction
            double signedAngle = GetSignedAngle(firstWallDirection, wallDirection);
            double degreeAngle = Math.Round(signedAngle * 180 / Math.PI, 6);
            log.AppendLine($"Signed Angle: {degreeAngle}°");

            // Map the angle to the closest predefined direction (0, 1, 2, 3)
            if (signedAngle >= -Math.PI / 4 && signedAngle < Math.PI / 4) // -45° to 45°
            {
                log.AppendLine($"Direction: {wallDirection} → Index: 2(0) (East)");
                return 2; // East
            }
            else if (signedAngle >= Math.PI / 4 && signedAngle < 3 * Math.PI / 4) // 45° to 135°
            {
                log.AppendLine($"Direction: {wallDirection} → Index: 1 (North)");
                return 1; // North
            }
            else if (signedAngle >= 3 * Math.PI / 4 || signedAngle < -3 * Math.PI / 4) // 135° to 180° or -180° to -135°
            {
                log.AppendLine($"Direction: {wallDirection} → Index: 0(2) (West)");
                return 0; // West
            }
            else // -135° to -45°
            {
                log.AppendLine($"Direction: {wallDirection} → Index: 3 (South)");
                return 3; // South
            }
        }

        private double GetSignedAngle(XYZ firstWallDirection, XYZ wallDirection)
        {
            // Calculate the dot product to get the cosine of the angle
            double dotProduct = firstWallDirection.DotProduct(wallDirection);

            // Calculate the cross product to get the sine of the angle
            XYZ crossProduct = firstWallDirection.CrossProduct(wallDirection);

            // Calculate the magnitude of the angle using Atan2
            double angle = Math.Atan2(crossProduct.Z, dotProduct);

            // Round the angle to a precision of 1e-6
            //angle = Math.Round(angle, 6);

            // Ensure the angle is between -π and π
            return angle;
        }

        private XYZ GetMarkerDirection(int elevationIndex)
        {
            // Return the direction corresponding to the elevation index
            switch (elevationIndex)
            {
                case 0: return XYZ.BasisX; // East
                case 1: return XYZ.BasisY; // North
                case 2: return -XYZ.BasisX; // West
                case 3: return -XYZ.BasisY; // South
                default: return XYZ.BasisX; // Default to East
            }
        }

        private List<List<XYZ>> GroupWallsByOrthogonalDirections(IList<IList<BoundarySegment>> boundarySegments, XYZ roomCenter, StringBuilder log)
        {
            List<XYZ> wallDirections = new List<XYZ>();
            foreach (IList<BoundarySegment> segmentList in boundarySegments)
            {
                foreach (BoundarySegment segment in segmentList)
                {
                    Curve curve = segment.GetCurve();

                    // Convert the curve to an unbounded line for projection (ignore Z-coordinate)
                    XYZ startPoint = new XYZ(curve.GetEndPoint(0).X, curve.GetEndPoint(0).Y, 0); // Flatten to Z=0
                    XYZ endPoint = new XYZ(curve.GetEndPoint(1).X, curve.GetEndPoint(1).Y, 0);   // Flatten to Z=0
                    Line unboundedLine = Line.CreateUnbound(startPoint, (endPoint - startPoint).Normalize());

                    // Project room center onto the unbounded line (ignore Z-coordinate)
                    XYZ flattenedRoomCenter = new XYZ(roomCenter.X, roomCenter.Y, 0); // Flatten to Z=0
                    XYZ projectionPoint = unboundedLine.Project(flattenedRoomCenter).XYZPoint;

                    // Calculate the projection vector (ignore Z-coordinate)
                    XYZ projectionVector = (projectionPoint - flattenedRoomCenter).Normalize();

                    // Check if the direction already exists in the list
                    if (!wallDirections.Any(dir => dir.IsAlmostEqualTo(projectionVector)))
                    {
                        wallDirections.Add(projectionVector);
                        log.AppendLine($"Wall Direction: {projectionVector} (Segment: {curve.GetEndPoint(0)} to {curve.GetEndPoint(1)})");
                    }
                }
            }

            // Group walls by orthogonal directions
            List<List<XYZ>> groups = new List<List<XYZ>>();
            while (wallDirections.Count > 0)
            {
                List<XYZ> group = new List<XYZ> { wallDirections[0] };
                wallDirections.RemoveAt(0);

                for (int i = wallDirections.Count - 1; i >= 0; i--)
                {
                    if (IsOrthogonal(group[0], wallDirections[i]))
                    {
                        group.Add(wallDirections[i]);
                        wallDirections.RemoveAt(i);
                    }
                }

                // Ensure the group has at most 4 directions
                if (group.Count > 4)
                {
                    log.AppendLine("Error: A group has more than 4 directions. This is not supported.");
                    TaskDialog.Show("Error", "A group has more than 4 directions. This is not supported.");
                    continue;
                }

                groups.Add(group);
            }

            return groups;
        }

        private bool IsOrthogonal(XYZ dir1, XYZ dir2)
        {
            double dotProduct = dir1.DotProduct(dir2);
            return Math.Abs(dotProduct) < 1e-6 || Math.Abs(dotProduct + 1) < 1e-6; // Check if vectors are orthogonal or 180°
        }

        private void RenameElevation(ViewSection elevationView, Room room, int elevationNumber)
        {
            string newName = $"{room.Number} {room.Name} - {elevationNumber}";
            try
            {
                elevationView.Name = newName;
            }
            catch (Exception ex)
            {
                // Handle naming conflicts (e.g., duplicate names)
                TaskDialog.Show("Warning", $"Failed to rename elevation view: {ex.Message}\n{room.Number} {room.Name} - {elevationNumber}");
            }
        }


        private ViewPlan FindMatchingView(Document doc, Room room, StringBuilder log)
        {
            ElementId phaseId = room.get_Parameter(BuiltInParameter.ROOM_PHASE).AsElementId();
            Phase roomPhase = doc.GetElement(phaseId) as Phase;
            if (roomPhase == null)
            {
                log.AppendLine("Error: Room does not have a valid phase.");
                return null;
            }

            Level roomLevel = doc.GetElement(room.LevelId) as Level;
            if (roomLevel == null)
            {
                log.AppendLine("Error: Room does not have a valid level.");
                return null;
            }

            // Find the matching view
            ViewPlan matchingView = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v =>
                    v.ViewType == ViewType.FloorPlan &&
                    !v.IsTemplate &&
                    v.GenLevel != null &&
                    v.GenLevel.Id == roomLevel.Id &&
                    v.get_Parameter(BuiltInParameter.VIEW_PHASE).AsElementId() == phaseId)
                .FirstOrDefault();

            if (matchingView != null)
            {
                log.AppendLine($"Matching View for Room {room.Name} (ID: {room.Id}): {matchingView.Name} (ID: {matchingView.Id})");
            }
            else
            {
                log.AppendLine($"Error: No matching view found for Room {room.Name} (ID: {room.Id}).");
            }

            return matchingView;
        }
    }
}