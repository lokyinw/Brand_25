using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Brand_25
{
    // Re-names internal elevation views from the room they sit in.
    //
    // Room_CreateIntElev names each elevation "{room number} {room name} - {n}". As the design
    // progresses the room number / name change, so the elevation names go stale. This command
    // finds, for every elevation, the room at the elevation marker's location and rebuilds the
    // name as "{room number} {room name} - {n}", keeping the existing " - {n}" suffix.
    //
    // Only elevations whose current name ends in " - {digits}" are touched, so elevations
    // made some other way (window elevations, exterior elevations, ...) are left alone.
    [Transaction(TransactionMode.Manual)]
    public class Elev_RenameFromRoom : IExternalCommand
    {
        // A room with the data needed for the point test, read once.
        private class RoomInfo
        {
            public Room Room;
            public BoundingBoxXYZ Box;
            public ElementId PhaseId;
        }

        private class RenamePlan
        {
            public ViewSection View;
            public string OldName;
            public string NewName;
        }

        // Matches the trailing " - 3" of "101 Office - 3".
        private static readonly Regex SuffixPattern = new Regex(@"\s-\s(\d+)\s*$", RegexOptions.Compiled);

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2026-10-22. Beta 0.1";

            List<string> log = new List<string>
            {
                $"Elev_RenameFromRoom log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                ""
            };
            int issueCount = 0;
            int renamed = 0;
            int unchanged = 0;
            int considered = 0;
            string fatalError = null;

            try
            {
                // Placed, bounded rooms only (Area 0 = not placed or not enclosed).
                List<RoomInfo> rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .OfType<Room>()
                    .Where(r => r.Area > 0)
                    .Select(r => new RoomInfo
                    {
                        Room = r,
                        Box = r.get_BoundingBox(null),
                        PhaseId = r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? ElementId.InvalidElementId
                    })
                    .Where(ri => ri.Box != null)
                    .ToList();

                List<RenamePlan> plans = new List<RenamePlan>();

                IEnumerable<ElevationMarker> markers = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElevationMarker))
                    .Cast<ElevationMarker>()
                    .Where(m => !m.IsReference);

                foreach (ElevationMarker marker in markers)
                {
                    // The views this marker hosts (up to 4, one per index).
                    List<ViewSection> hosted = new List<ViewSection>();
                    for (int index = 0; index < marker.MaximumViewCount; index++)
                    {
                        try
                        {
                            if (marker.IsAvailableIndex(index)) continue;   // no view at this index
                            ViewSection v = doc.GetElement(marker.GetViewId(index)) as ViewSection;
                            if (v != null) hosted.Add(v);
                        }
                        catch (Exception)
                        {
                            // index with nothing usable on it - ignore
                        }
                    }

                    // Only elevations that carry the " - n" suffix are ours to rename.
                    List<ViewSection> candidates = hosted.Where(v => SuffixPattern.IsMatch(v.Name)).ToList();
                    if (candidates.Count == 0) continue;

                    // Marker position and level (the level of the plan the marker sits in).
                    ViewPlan ownerPlan = doc.GetElement(marker.OwnerViewId) as ViewPlan;
                    Level level = ownerPlan?.GenLevel;
                    XYZ point = GetMarkerPoint(marker, ownerPlan);

                    foreach (ViewSection view in candidates)
                    {
                        considered++;

                        if (level == null || point == null)
                        {
                            issueCount++;
                            log.Add($"[Issue] '{view.Name}' (Id {view.Id.Value}): cannot work out the marker's location or level - not renamed");
                            continue;
                        }

                        // Rooms are matched on the elevation's own phase, falling back to the plan's.
                        ElementId phaseId =
                            view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ??
                            ownerPlan?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ??
                            ElementId.InvalidElementId;

                        Room room = FindRoomAtPoint(doc, rooms, point, level, phaseId, view, out string problem);
                        if (room == null)
                        {
                            issueCount++;
                            log.Add($"[Issue] '{view.Name}' (Id {view.Id.Value}): {problem} - not renamed");
                            continue;
                        }

                        string suffix = SuffixPattern.Match(view.Name).Groups[1].Value;
                        string newName = BuildName(room, suffix);

                        if (newName == view.Name)
                        {
                            unchanged++;
                            continue;
                        }

                        plans.Add(new RenamePlan { View = view, OldName = view.Name, NewName = newName });
                    }
                }

                if (plans.Count > 0)
                {
                    using (Transaction tx = new Transaction(doc, "LW_Rename Elevations"))
                    {
                        tx.Start();

                        // Pass 1: park every view on a temporary unique name. View names must be
                        // unique, so without this, two rooms swapping numbers (101 <-> 102) would
                        // collide halfway through.
                        foreach (RenamePlan plan in plans)
                        {
                            try
                            {
                                plan.View.Name = $"__LW_tmp_{plan.View.Id.Value}";
                            }
                            catch (Exception ex)
                            {
                                issueCount++;
                                log.Add($"[Issue] '{plan.OldName}': could not be renamed ({ex.Message})");
                                plan.NewName = null;
                            }
                        }

                        // Pass 2: the real names.
                        foreach (RenamePlan plan in plans.Where(p => p.NewName != null))
                        {
                            try
                            {
                                plan.View.Name = plan.NewName;
                                renamed++;
                                log.Add($"[Renamed] '{plan.OldName}' -> '{plan.NewName}'");
                            }
                            catch (Exception ex)
                            {
                                // Typically a name clash with an unrelated view, or a character
                                // Revit does not allow in view names.
                                issueCount++;
                                log.Add($"[Issue] '{plan.OldName}': could not be renamed to '{plan.NewName}' ({ex.Message})");
                                plan.NewName = null;
                            }
                        }

                        // Put back the original name on anything that failed pass 2.
                        foreach (RenamePlan plan in plans.Where(p => p.NewName == null))
                        {
                            try
                            {
                                if (plan.View.Name != plan.OldName) plan.View.Name = plan.OldName;
                            }
                            catch (Exception ex)
                            {
                                issueCount++;
                                log.Add($"[Issue] '{plan.OldName}': original name could not be restored; left as '{plan.View.Name}' ({ex.Message})");
                            }
                        }

                        TransactionStatus status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                        {
                            issueCount++;
                            renamed = 0;
                            log.Add($"[Issue] Transaction did not commit (status: {status}) - nothing was renamed");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                fatalError = ex.Message;
                issueCount++;
                log.Add($"[Fatal] {ex.GetType().Name}: {ex.Message}");
                log.Add("[Fatal] The transaction was rolled back - nothing was renamed.");
            }

            log.Add("");
            log.Add($"[Summary] Elevations checked: {considered}, renamed: {renamed}, already up to date: {unchanged}, issues: {issueCount}");

            string logPath = Path.Combine(Path.GetTempPath(), $"Elev_RenameFromRoom_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            string logWriteError = null;
            try
            {
                File.WriteAllLines(logPath, log);
            }
            catch (Exception ex)
            {
                logWriteError = ex.Message;
            }

            bool hasIssues = issueCount > 0;
            bool showLogButton = logWriteError == null && hasIssues;

            string summary;
            if (fatalError != null)
                summary = "The run failed and was rolled back. Nothing was renamed.\n";
            else if (considered == 0)
                summary = "No internal elevation views found.\n(Names must end in \" - 1\", \" - 2\", ...)\n";
            else
                summary = $"{renamed} elevations renamed\n{unchanged} already up to date\n";

            summary += (hasIssues ? $"{issueCount} issues founded - see log\n" : "") +
                       (logWriteError != null ? $"Log FAILED to write: {logWriteError}" : "");

            new Warning(fatalError != null ? "Error" : "Rename Elevations", summary, credit,
                revealPath: showLogButton ? logPath : null).ShowDialog();

            if (fatalError != null)
            {
                message = fatalError;
                return Result.Failed;
            }
            return Result.Succeeded;
        }

        // "{room number} {room name} - {n}", the same pattern Room_CreateIntElev uses.
        private static string BuildName(Room room, string suffix)
        {
            Parameter nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME);
            string roomName = nameParam != null && nameParam.HasValue ? nameParam.AsString() : "No Room Name";
            return $"{room.Number} {roomName} - {suffix}";
        }

        // The marker's centre: its location point, or the centre of its bounding box in the
        // plan it sits in when no location point is available.
        private static XYZ GetMarkerPoint(ElevationMarker marker, ViewPlan ownerPlan)
        {
            if (marker.Location is LocationPoint lp) return lp.Point;

            BoundingBoxXYZ box = ownerPlan != null ? marker.get_BoundingBox(ownerPlan) : null;
            return box != null ? (box.Min + box.Max) * 0.5 : null;
        }

        // The room that contains the point, on the given level and phase. When the same spot
        // holds rooms from more than one design option, the ones in options the elevation does
        // not show are dropped, and an option room wins over a main-model room.
        private static Room FindRoomAtPoint(Document doc, List<RoomInfo> rooms, XYZ point, Level level,
                                            ElementId phaseId, View elevationView, out string problem)
        {
            problem = null;
            const double tolerance = 0.01;   // feet

            List<Room> found = new List<Room>();
            foreach (RoomInfo info in rooms)
            {
                if (info.Room.LevelId != level.Id) continue;
                if (phaseId != ElementId.InvalidElementId && info.PhaseId != phaseId) continue;

                BoundingBoxXYZ b = info.Box;
                if (point.X < b.Min.X - tolerance || point.X > b.Max.X + tolerance ||
                    point.Y < b.Min.Y - tolerance || point.Y > b.Max.Y + tolerance) continue;

                // Tested at the room's own floor (+ a little) so the level/room datum
                // difference in this project cannot put the point outside the room.
                if (info.Room.IsPointInRoom(new XYZ(point.X, point.Y, b.Min.Z + 0.05)))
                    found.Add(info.Room);
            }

            if (found.Count == 0)
            {
                problem = "no room found at the elevation marker's location";
                return null;
            }

            // Design options only come into play if one of the rooms sits in an option.
            if (found.Any(r => r.DesignOption != null))
            {
                HashSet<ElementId> visibleOptions = Room_Recentre.GetVisibleDesignOptionIds(doc, elevationView);

                found = found
                    .Where(r => r.DesignOption == null || visibleOptions.Contains(r.DesignOption.Id))
                    .ToList();

                if (found.Count == 0)
                {
                    problem = "the room at the marker's location is in a design option this elevation does not show";
                    return null;
                }

                if (found.Count > 1 && found.Any(r => r.DesignOption != null))
                    found = found.Where(r => r.DesignOption != null).ToList();
            }

            if (found.Count > 1)
            {
                problem = "more than one room found at the elevation marker's location (" +
                          string.Join(", ", found.Select(r => r.Number)) + ")";
                return null;
            }

            return found[0];
        }
    }
}
