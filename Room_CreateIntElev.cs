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
    [Regeneration(RegenerationOption.Manual)]
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

                // Step 5.5: Build a lookup of (Level, Phase) -> matching floor plan view ONCE,
                // instead of re-querying every ViewPlan in the document for every room.
                Dictionary<(ElementId levelId, ElementId phaseId), ViewPlan> viewLookup =
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewPlan))
                        .Cast<ViewPlan>()
                        .Where(v => v.ViewType == ViewType.FloorPlan && !v.IsTemplate && v.GenLevel != null
                                    && v.get_Parameter(BuiltInParameter.VIEW_PHASE) != null)
                        .GroupBy(v => (v.GenLevel.Id, v.get_Parameter(BuiltInParameter.VIEW_PHASE).AsElementId()))
                        .ToDictionary(g => g.Key, g => g.First());

                // Step 6: Create internal elevations for selected rooms.
                // Rooms are processed in batches, each in its own Transaction, instead of
                // one giant Transaction for the whole selection. A single long-running
                // transaction accumulates pending-change bookkeeping that made each later
                // room noticeably slower than the first (confirmed by profiling). Committing
                // periodically resets that cost. The TransactionGroup + Assimilate() keeps
                // all of it as a single "LW_Create Internal Elevations" entry in Revit's
                // Undo list, so the batching is invisible to the user.
                const int roomsPerBatch = 5;

                int totalElevationsCreated = 0;
                List<string> issues = new List<string>();

                // Views queued for crop-shape adjustment, done as a SEPARATE pass after
                // all elevations exist and their creating transactions have committed.
                // A control test confirmed GetCropShape -> modify -> SetCropShape works
                // fine on an already-existing, already-regenerated view — the repeated
                // failures earlier were from doing this immediately inside the same
                // transaction as the view's own creation, not from the geometry itself.
                List<(ViewSection view, double topZ, double floorZ)> cropAdjustQueue =
                    new List<(ViewSection, double, double)>();

                using (TransactionGroup transGroup = new TransactionGroup(doc, "LW_Create Internal Elevations"))
                {
                    transGroup.Start();

                    // Room bounding boxes only reflect a room's real 3D extent (capped by
                    // whatever ceiling/roof/floor bounds it above) when Volume Computations
                    // is turned on. Snapshot the current setting, force it on for this
                    // command's duration, and restore it afterward so we don't leave a
                    // side effect on the project.
                    AreaVolumeSettings areaVolumeSettings = AreaVolumeSettings.GetAreaVolumeSettings(doc);
                    bool originalComputeVolumes = areaVolumeSettings.ComputeVolumes;
                    log.AppendLine($"Volume Computations was originally: {(originalComputeVolumes ? "ON" : "OFF")}");

                    if (!originalComputeVolumes)
                    {
                        using (Transaction volTrans = new Transaction(doc, "LW_Enable Volume Computations"))
                        {
                            volTrans.Start();
                            areaVolumeSettings.ComputeVolumes = true;
                            volTrans.Commit();
                        }
                        // Belt-and-suspenders: Transaction.Commit() already forces a full
                        // regeneration, but this guarantees room bounding boxes are current
                        // before we start reading them below.
                        doc.Regenerate();
                        log.AppendLine("Volume Computations temporarily enabled.");
                    }

                    List<Room> selectedRooms = roomWindow.SelectedRooms.ToList();
                    for (int batchStart = 0; batchStart < selectedRooms.Count; batchStart += roomsPerBatch)
                    {
                        List<Room> batch = selectedRooms.Skip(batchStart).Take(roomsPerBatch).ToList();

                        using (Transaction trans = new Transaction(doc, "LW_Create Internal Elevations"))
                        {
                            trans.Start();

                            foreach (Room room in batch)
                            {
                                log.AppendLine($"Processing Room: {room.Name} (ID: {room.Id})");

                                int elevationsCreated = CreateElevationsForRoom(doc, room, selectedElevType, viewLookup, log, issues, cropAdjustQueue);
                                totalElevationsCreated += elevationsCreated;

                                log.AppendLine($"Created {elevationsCreated} elevations for Room: {room.Name} (ID: {room.Id})");
                                log.AppendLine();
                            }

                            trans.Commit();
                        }
                    }

                    // Restore Volume Computations to whatever it was before this command ran.
                    if (!originalComputeVolumes)
                    {
                        using (Transaction volRevertTrans = new Transaction(doc, "LW_Restore Volume Computations"))
                        {
                            volRevertTrans.Start();
                            areaVolumeSettings.ComputeVolumes = originalComputeVolumes;
                            volRevertTrans.Commit();
                        }
                        doc.Regenerate();
                        log.AppendLine($"Volume Computations restored to: {(originalComputeVolumes ? "ON" : "OFF")}");
                    }

                    // Second pass: adjust crop shapes now that every elevation view in the
                    // queue already exists and its creating transaction has committed.
                    // Batched the same way as room processing, for the same reason (avoid
                    // one giant transaction's cumulative slowdown).
                    const int viewsPerBatch = 20; // lighter-weight than element creation, so a larger batch is fine
                    log.AppendLine();
                    log.AppendLine($"=== Adjusting crop shapes for {cropAdjustQueue.Count} view(s) ===");
                    for (int batchStart = 0; batchStart < cropAdjustQueue.Count; batchStart += viewsPerBatch)
                    {
                        var batch = cropAdjustQueue.Skip(batchStart).Take(viewsPerBatch).ToList();

                        using (Transaction trans = new Transaction(doc, "LW_Adjust Elevation Crop"))
                        {
                            trans.Start();
                            foreach (var (view, topZ, floorZ) in batch)
                            {
                                SetElevationCropShape(view, topZ, floorZ, log);
                            }
                            trans.Commit();
                        }
                    }

                    transGroup.Assimilate();
                }

                // Show success message, plus a count of any issues instead of
                // having popped up a blocking dialog for each one mid-loop.
                string summary = $"{totalElevationsCreated} internal elevations created.";
                if (issues.Count > 0)
                {
                    summary += $"\n\n{issues.Count} issue(s) were encountered — see the log for details.";
                }
                new Warning("Success", summary, credit).ShowDialog();
                log.AppendLine($"{totalElevationsCreated} internal elevations created.");
                if (issues.Count > 0)
                {
                    log.AppendLine();
                    log.AppendLine($"=== {issues.Count} Issue(s) ===");
                    foreach (string issue in issues) log.AppendLine(issue);
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

        private int CreateElevationsForRoom(Document doc, Room room, ViewFamilyType elevationType,
            Dictionary<(ElementId levelId, ElementId phaseId), ViewPlan> viewLookup, StringBuilder log, List<string> issues,
            List<(ViewSection view, double topZ, double floorZ)> cropAdjustQueue)
        {
            int createdElevations = 0;

            // Step 1: Find matching plan view for the room (O(1) dictionary lookup instead
            // of a fresh document-wide FilteredElementCollector query per room).
            ViewPlan matchingView = FindMatchingView(doc, room, viewLookup, log);
            if (matchingView == null)
            {
                string issue = $"Room {room.Name} (ID: {room.Id}): No matching plan view found for the room.";
                log.AppendLine($"Error: {issue}");
                issues.Add(issue);
                return 0;
            }

            // Step 2: Get room boundary segments and filter out arcs
            IList<IList<BoundarySegment>> boundarySegments = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (boundarySegments == null || boundarySegments.Count == 0)
            {
                string issue = $"Room {room.Name} (ID: {room.Id}): No boundary segments found for the room.";
                log.AppendLine($"Error: {issue}");
                issues.Add(issue);
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
                string issue = $"Room {room.Name} (ID: {room.Id}): No valid boundary segments (non-arc) found for the room.";
                log.AppendLine($"Error: {issue}");
                issues.Add(issue);
                return 0;
            }

            // Step 3: Get room insertion point and level elevation
            LocationPoint location = room.Location as LocationPoint;
            if (location == null)
            {
                string issue = $"Room {room.Name} (ID: {room.Id}): Room does not have a valid location point.";
                log.AppendLine($"Error: {issue}");
                issues.Add(issue);
                return 0;
            }

            Level roomLevel = doc.GetElement(room.LevelId) as Level;
            if (roomLevel == null)
            {
                string issue = $"Room {room.Name} (ID: {room.Id}): Room does not have a valid level.";
                log.AppendLine($"Error: {issue}");
                issues.Add(issue);
                return 0;
            }

            XYZ roomCenter = new XYZ(location.Point.X, location.Point.Y, roomLevel.Elevation);

            // With Volume Computations on, the room's bounding box top reflects its real
            // 3D extent — capped by whatever ceiling/roof/floor actually bounds it above —
            // rather than just the room's Upper Limit + Offset parameters. This is what
            // we align each elevation's crop top to.
            //
            // The floor (bottom) target is derived from this SAME bounding box (Min.Z),
            // not from roomLevel.Elevation — testing showed those two are in different
            // coordinate frames in this project (Level.Elevation reflects a survey/site
            // datum offset, ~183 ft, while the room bounding box and the crop shape's own
            // points both use a different, smaller-scale frame). Deriving both top and
            // bottom from the same BoundingBoxXYZ guarantees they're consistent with each
            // other and with what the crop shape itself expects.
            BoundingBoxXYZ roomBoundingBox = room.get_BoundingBox(null);
            double roomTopElevation = roomBoundingBox != null
                ? roomBoundingBox.Max.Z
                : roomLevel.Elevation + 10.0; // Fallback if the room has no bounding box for some reason
            double roomFloorElevation = roomBoundingBox != null
                ? roomBoundingBox.Min.Z
                : roomLevel.Elevation - 1.0; // Fallback if the room has no bounding box for some reason

            // Step 4: Group walls by orthogonal directions
            List<List<XYZ>> wallGroups = GroupWallsByOrthogonalDirections(filteredBoundarySegments.Cast<IList<BoundarySegment>>().ToList(), roomCenter, log, issues);

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
                    string issue = $"Room {room.Name} (ID: {room.Id}): A wall group has more than 4 directions, which is not supported.";
                    log.AppendLine($"Error: {issue}");
                    issues.Add(issue);
                    continue;
                }

                // Create elevation marker at room center
                ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, elevationType.Id, roomCenter, 50);
                if (marker == null)
                {
                    string issue = $"Room {room.Name} (ID: {room.Id}): Failed to create elevation marker.";
                    log.AppendLine($"Error: {issue}");
                    issues.Add(issue);
                    continue;
                }

                // Step 6: Create a placeholder elevation at index 2 BEFORE rotating.
                // A brand-new ElevationMarker apparently needs to already host at least
                // one view for a subsequent rotation to actually take effect on views
                // created afterward — rotating an empty marker doesn't stick. This view
                // gets replaced with a correctly-oriented one once we've rotated.
                ViewSection dummyElevation = marker.CreateElevation(doc, matchingView.Id, 2);
                if (dummyElevation == null)
                {
                    string issue = $"Room {room.Name} (ID: {room.Id}): Failed to create dummy elevation.";
                    log.AppendLine($"Error: {issue}");
                    issues.Add(issue);
                    continue;
                }

                // Step 7: Rotate the marker to align with the first wall direction
                XYZ firstWallDirection = group[0];
                RotateMarker(doc, marker, firstWallDirection, roomCenter, log);

                // With RegenerationOption.Manual, the rotation above isn't reflected in the
                // model's geometry until we regenerate explicitly. Every CreateElevation call
                // below depends on the marker's rotated orientation.
                doc.Regenerate();

                // Step 8: Create elevations for every OTHER wall direction in the group first
                // (firstWallDirection/index 2 is still occupied by the pre-rotation dummy for now).
                // Creating these first means that, when it's time to delete the stale dummy,
                // the marker already has at least one correctly-oriented view hosted — so the
                // marker is never left with zero views mid-transaction, and we only need a
                // second temporary dummy for the one edge case where the group has no "other"
                // directions at all.
                int createdElevationsInGroup = 0;
                foreach (XYZ wallDirection in group.Skip(1))
                {
                    int elevationIndex = GetElevationIndex(wallDirection, firstWallDirection, log);

                    try
                    {
                        ViewSection elevationView = marker.CreateElevation(doc, matchingView.Id, elevationIndex);
                        if (elevationView != null)
                        {
                            createdElevationsInGroup++;
                            createdElevations++; // Increment the total counter

                            RenameElevation(elevationView, room, elevationNumber, issues);
                            cropAdjustQueue.Add((elevationView, roomTopElevation, roomFloorElevation));
                        }
                    }
                    catch (Exception ex)
                    {
                        string issue = $"Room {room.Name} (ID: {room.Id}): Failed to create elevation for index {elevationIndex}: {ex.Message}";
                        log.AppendLine($"Error: {issue}");
                        issues.Add(issue);
                    }
                    finally
                    {
                        elevationNumber++;
                    }
                }

                // Step 9: Replace the pre-rotation dummy at index 2 with the real,
                // correctly-oriented view for firstWallDirection.
                if (createdElevationsInGroup > 0)
                {
                    // At least one other view already exists on the marker, so it's safe
                    // to delete the dummy and recreate index 2 directly.
                    doc.Delete(dummyElevation.Id);

                    ViewSection realElevation = marker.CreateElevation(doc, matchingView.Id, 2);
                    if (realElevation != null)
                    {
                        createdElevations++; // Increment the total counter
                        RenameElevation(realElevation, room, elevationNumber, issues);
                        cropAdjustQueue.Add((realElevation, roomTopElevation, roomFloorElevation));
                        elevationNumber++;
                    }
                }
                else
                {
                    // No other directions in this group — deleting the dummy right now
                    // would leave the marker with zero hosted views. Create a second,
                    // temporary dummy at a different index first to keep the marker
                    // "alive" through the swap, then clean it up afterward.
                    ViewSection dummyElevation2 = marker.CreateElevation(doc, matchingView.Id, 1);
                    if (dummyElevation2 == null)
                    {
                        string issue = $"Room {room.Name} (ID: {room.Id}): Failed to create second dummy elevation.";
                        log.AppendLine($"Error: {issue}");
                        issues.Add(issue);
                        continue;
                    }

                    doc.Delete(dummyElevation.Id);

                    ViewSection realElevation = marker.CreateElevation(doc, matchingView.Id, 2);
                    if (realElevation != null)
                    {
                        createdElevations++; // Increment the total counter
                        RenameElevation(realElevation, room, elevationNumber, issues);
                        cropAdjustQueue.Add((realElevation, roomTopElevation, roomFloorElevation));
                        elevationNumber++;
                    }

                    doc.Delete(dummyElevation2.Id);
                }

                // Step 10: Move secondary markers by 1 meter in the index 0 direction
                if (markerCount > 0)
                {
                    XYZ offsetDirection = GetMarkerDirection(0); // Marker's index 0 direction (east)
                    XYZ offset = offsetDirection * markerOffsetDistance;
                    ElementTransformUtils.MoveElement(doc, marker.Id, offset);
                }

                markerCount++;
            }

            return createdElevations;
        }

        private void RotateMarker(Document doc, ElevationMarker marker, XYZ wallDirection, XYZ roomCenter, StringBuilder log)
        {
            // Calculate the rotation angle using Atan2
            double angle = Math.Atan2(wallDirection.Y, wallDirection.X);

            // Rotate around the Z-axis (vertical), through the room's center point
            XYZ rotationAxisDirection = XYZ.BasisZ;
            Line rotationAxis = Line.CreateBound(roomCenter, roomCenter + rotationAxisDirection);

            ElementTransformUtils.RotateElement(doc, marker.Id, rotationAxis, angle);
        }

        private int GetElevationIndex(XYZ wallDirection, XYZ firstWallDirection, StringBuilder log)
        {
            // Map the angle between the first wall direction and this one to the
            // closest predefined marker direction (0=West, 1=North, 2=East, 3=South).
            double signedAngle = GetSignedAngle(firstWallDirection, wallDirection);

            if (signedAngle >= -Math.PI / 4 && signedAngle < Math.PI / 4) // -45° to 45°
            {
                return 2; // East
            }
            else if (signedAngle >= Math.PI / 4 && signedAngle < 3 * Math.PI / 4) // 45° to 135°
            {
                return 1; // North
            }
            else if (signedAngle >= 3 * Math.PI / 4 || signedAngle < -3 * Math.PI / 4) // 135° to 180° or -180° to -135°
            {
                return 0; // West
            }
            else // -135° to -45°
            {
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

        private List<List<XYZ>> GroupWallsByOrthogonalDirections(IList<IList<BoundarySegment>> boundarySegments, XYZ roomCenter, StringBuilder log, List<string> issues)
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
                    string issue = "A wall group has more than 4 directions, which is not supported.";
                    log.AppendLine($"Error: {issue}");
                    issues.Add(issue);
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

        private void RenameElevation(ViewSection elevationView, Room room, int elevationNumber, List<string> issues)
        {
            string newName = $"{room.Number} {room.Name} - {elevationNumber}";
            try
            {
                elevationView.Name = newName;
            }
            catch (Exception ex)
            {
                // Handle naming conflicts (e.g., duplicate names)
                issues.Add($"Failed to rename elevation view to \"{newName}\": {ex.Message}");
            }
        }

        // Raises the crop shape's top to the room's ceiling elevation and lowers its
        // bottom to the room's floor elevation, while preserving the room-fitted outline
        // Revit auto-generates when an ElevationMarker is created inside a room.
        //
        // This is called from a SEPARATE pass, after every elevation in the batch has
        // already been created and its creating transaction committed — a standalone
        // control test confirmed GetCropShape -> modify -> SetCropShape works fine on an
        // already-existing, already-regenerated view. The repeated "plane parallel to
        // view plane" / "self-intersection" rejections seen earlier came from attempting
        // this immediately inside the same transaction as the view's own creation — a
        // timing issue, not a problem with the geometry or coordinate space. In fact,
        // GetCropShape()/SetCropShape() both use WORLD coordinates directly — no
        // transform to local space needed at all, confirmed by that same test.
        private void SetElevationCropShape(ViewSection elevationView, double topElevationWorldZ, double floorElevationWorldZ, StringBuilder log)
        {
            try
            {
                ViewCropRegionShapeManager cropShapeManager = elevationView.GetCropRegionShapeManager();
                IList<CurveLoop> cropShapes = cropShapeManager.GetCropShape();

                if (cropShapes == null || cropShapes.Count == 0)
                {
                    log.AppendLine($"  View '{elevationView.Name}': GetCropShape() returned nothing; skipping.");
                    return;
                }

                // Edit the first (outer) loop's curves in place — the common case for a
                // room-fitted crop. Multiple crop loops (split regions) aren't handled here.
                CurveLoop originalLoop = cropShapes[0];

                double oldTopZ = double.MinValue;
                double oldBottomZ = double.MaxValue;
                foreach (Curve curve in originalLoop)
                {
                    foreach (XYZ p in new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) })
                    {
                        oldTopZ = Math.Max(oldTopZ, p.Z);
                        oldBottomZ = Math.Min(oldBottomZ, p.Z);
                    }
                }

                const double tolerance = 0.01; // feet (~1/8")
                CurveLoop newLoop = new CurveLoop();
                foreach (Curve curve in originalLoop)
                {
                    XYZ p0 = curve.GetEndPoint(0);
                    XYZ p1 = curve.GetEndPoint(1);

                    XYZ newP0 = p0;
                    if (Math.Abs(p0.Z - oldTopZ) < tolerance) newP0 = new XYZ(p0.X, p0.Y, topElevationWorldZ);
                    else if (Math.Abs(p0.Z - oldBottomZ) < tolerance) newP0 = new XYZ(p0.X, p0.Y, floorElevationWorldZ);

                    XYZ newP1 = p1;
                    if (Math.Abs(p1.Z - oldTopZ) < tolerance) newP1 = new XYZ(p1.X, p1.Y, topElevationWorldZ);
                    else if (Math.Abs(p1.Z - oldBottomZ) < tolerance) newP1 = new XYZ(p1.X, p1.Y, floorElevationWorldZ);

                    newLoop.Append(Line.CreateBound(newP0, newP1));
                }

                cropShapeManager.SetCropShape(newLoop);
                elevationView.CropBoxActive = true;

                log.AppendLine($"  View '{elevationView.Name}': crop top {oldTopZ:F3} -> {topElevationWorldZ:F3}, " +
                    $"bottom {oldBottomZ:F3} -> {floorElevationWorldZ:F3} (room-fitted outline preserved).");
            }
            catch (Exception ex)
            {
                log.AppendLine($"  Warning: Failed to adjust crop shape for view '{elevationView.Name}': {ex.Message}");
            }
        }


        private ViewPlan FindMatchingView(Document doc, Room room, Dictionary<(ElementId levelId, ElementId phaseId), ViewPlan> viewLookup, StringBuilder log)
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

            // Find the matching view via the pre-built lookup (O(1)) instead of
            // re-scanning every ViewPlan in the document for each room.
            viewLookup.TryGetValue((roomLevel.Id, phaseId), out ViewPlan matchingView);

            if (matchingView == null)
            {
                log.AppendLine($"Error: No matching view found for Room {room.Name} (ID: {room.Id}).");
            }

            return matchingView;
        }
    }
}