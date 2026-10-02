using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Brand_25
{
    /// <summary>
    /// Creates floor finishes (Floor elements) from selected rooms in the active floor plan.
    /// - Outline: room boundary at wall finish, read via SpatialElementBoundaryOptions
    ///   (project Area Computation setting is never touched). Inner loops (columns,
    ///   shafts) come through as holes.
    /// - Top of finish sits on the level by default; the user's offset raises it
    ///   (negative lowers it). Height Offset From Level = offset, since Revit floors
    ///   are referenced from their top face.
    /// - Non-structural, not room bounding, Comments = room number, Phase Created = room phase.
    /// - Only rooms on the view's level and in the active design option are accepted.
    /// Next revision: extend finishes through door openings to the wall core centreline.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Room_CreateFloorFinish : IExternalCommand
    {
        private const string DialogTitle = "Create Floor Finish";

        private static readonly List<string> DebugLog = new List<string>();
        private static int SuppressedWarningCount;

        // Remembered for the rest of the Revit session.
        private static string _lastFloorTypeName;
        private static string _lastOffsetText = "0";
        private static bool _lastSkipExisting = true;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            string credit = "Last Modified by Lok on 2026-09-30. Beta 0.11";

            DebugLog.Clear();
            SuppressedWarningCount = 0;

            // ---------------- 1. Active view must be a floor plan ----------------
            ViewPlan plan = doc.ActiveView as ViewPlan;
            if (plan == null || plan.ViewType != ViewType.FloorPlan || plan.GenLevel == null)
            {
                new Warning(DialogTitle, "Please start this command in a floor plan view.", credit).ShowDialog();
                return Result.Cancelled;
            }
            Level level = plan.GenLevel;

            // ---------------- 2. Rooms must be pickable in this view ----------------
            if (!AreRoomsPickable(doc, plan))
            {
                new Warning(DialogTitle,
                    "Rooms are hidden in this view, so they can't be selected.\n\n" +
                    "In Visibility/Graphics (or the view template), turn on Rooms and at least one of its " +
                    "'Interior Fill' or 'Reference' subcategories, then run the command again.",
                    credit, messageFontSize: 16).ShowDialog();
                return Result.Cancelled;
            }

            // ---------------- Floor types ----------------
            List<VM_FloorTypeItem> floorTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .Where(ft => !ft.IsFoundationSlab)
                .Select(ft => new VM_FloorTypeItem(ft))
                .OrderBy(v => v.Name)
                .ToList();

            if (floorTypes.Count == 0)
            {
                new Warning(DialogTitle, "No floor types found in this project.", credit).ShowDialog();
                return Result.Cancelled;
            }

            // ---------------- Design option context ----------------
            ElementId activeOptionId = DesignOption.GetActiveDesignOptionId(doc);
            string optionName = activeOptionId == ElementId.InvalidElementId
                ? "Main Model"
                : doc.GetElement(activeOptionId)?.Name ?? "Unknown Option";

            RoomPickFilter filter = new RoomPickFilter(level.Id, activeOptionId);

            // Pre-selection: keep eligible rooms, drop anything else silently.
            List<ElementId> preselected = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e != null && filter.AllowElement(e))
                .Select(e => e.Id)
                .ToList();

            VM_FloorFinish vm = new VM_FloorFinish($"Level: {level.Name}     Design Option: {optionName}", floorTypes)
            {
                OffsetText = _lastOffsetText,
                SkipExisting = _lastSkipExisting,
                SelectedFloorType = floorTypes.FirstOrDefault(f => f.Name == _lastFloorTypeName)
            };
            vm.SetRooms(preselected);

            // ---------------- 3. Dialog / pick loop (modal) ----------------
            while (true)
            {
                Selection_FloorFinish dlg = new Selection_FloorFinish(vm, credit, uiapp.MainWindowHandle);
                dlg.ShowDialog();

                if (dlg.Action == FloorFinishAction.PickRooms)
                {
                    vm.SetRooms(PickRooms(uidoc, doc, filter, vm.SelectedRoomIds));
                    continue;
                }
                if (dlg.Action == FloorFinishAction.Create)
                    break;

                return Result.Cancelled;   // closed with X
            }

            _lastFloorTypeName = vm.SelectedFloorType.Name;
            _lastOffsetText = vm.OffsetText;
            _lastSkipExisting = vm.SkipExisting;

            // ---------------- 4. Create floors ----------------
            FloorType floorType = vm.SelectedFloorType.FloorType;
            // Top of finish aligned to the level, raised by the user's offset.
            double heightOffset = UnitUtils.ConvertToInternalUnits(vm.OffsetMillimetres, UnitTypeId.Millimeters);

            DebugLog.Add($"[Settings] Level: {level.Name}, Design Option: {optionName}, Floor Type: {floorType.Name}, " +
                         $"Raise top of finish by: {vm.OffsetMillimetres} mm, Skip existing: {vm.SkipExisting}, Rooms selected: {vm.SelectedRoomIds.Count}");

            HashSet<string> existingNumbers = vm.SkipExisting
                ? CollectExistingFinishNumbers(doc, level.Id, activeOptionId)
                : new HashSet<string>();

            SpatialElementBoundaryOptions boundaryOptions = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };

            List<ElementId> createdIds = new List<ElementId>();
            int skippedRoomCount = 0;     // issues: not enclosed, bad boundary, create failed
            int skippedExistingCount = 0; // user's choice, not an issue
            int paramIssueCount = 0;
            string commitError = null;

            using (Transaction t = new Transaction(doc, "LW_Create Floor Finishes"))
            {
                t.Start();

                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(fho);

                foreach (ElementId roomId in vm.SelectedRoomIds)
                {
                    Room room = doc.GetElement(roomId) as Room;
                    if (room == null)
                    {
                        DebugLog.Add($"[Skipped] Element {roomId.Value}: no longer a room");
                        skippedRoomCount++;
                        continue;
                    }

                    string label = $"Room {room.Number} '{room.Name}' (Id {room.Id.Value})";

                    if (room.Location == null || room.Area <= 0.0)
                    {
                        DebugLog.Add($"[Skipped] {label}: not placed, not enclosed or redundant");
                        skippedRoomCount++;
                        continue;
                    }

                    if (existingNumbers.Contains(room.Number ?? ""))
                    {
                        DebugLog.Add($"[Skipped - Existing] {label}: a floor on this level already has Comments = '{room.Number}'");
                        skippedExistingCount++;
                        continue;
                    }

                    List<CurveLoop> loops = BuildBoundaryLoops(room, boundaryOptions, label);
                    if (loops == null)
                    {
                        skippedRoomCount++;
                        continue;
                    }

                    Floor floor;
                    try
                    {
                        floor = Floor.Create(doc, loops, floorType.Id, level.Id, false, null, 0.0);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Add($"[Skipped] {label}: Floor.Create failed - {ex.Message}");
                        skippedRoomCount++;
                        continue;
                    }

                    paramIssueCount += SetFloorParameters(floor, room, heightOffset, label);
                    createdIds.Add(floor.Id);
                    DebugLog.Add($"[Created] {label} -> Floor {floor.Id.Value} ({loops.Count} loop(s))");
                }

                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                {
                    commitError = $"Transaction ended with status {status}";
                    DebugLog.Add($"[Error] {commitError}");
                    createdIds.Clear();
                }
            }

            if (createdIds.Count > 0)
                uidoc.Selection.SetElementIds(createdIds);

            // ---------------- 5. Log + summary ----------------
            int issueCount = skippedRoomCount + paramIssueCount + (commitError != null ? 1 : 0);

            DebugLog.Add($"[Summary] Created: {createdIds.Count}, Skipped (issues): {skippedRoomCount}, " +
                         $"Skipped (existing finish): {skippedExistingCount}, Parameter issues: {paramIssueCount}, " +
                         $"Revit warnings suppressed: {SuppressedWarningCount}");

            string logPath = Path.Combine(Path.GetTempPath(), $"Room_CreateFloorFinish_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            string logWriteError = null;
            try
            {
                File.WriteAllLines(logPath, DebugLog);
            }
            catch (Exception ex)
            {
                logWriteError = ex.Message;
            }

            bool hasLogWorthyItems = issueCount > 0 || skippedExistingCount > 0 || SuppressedWarningCount > 0;
            bool showLogButton = logWriteError == null && hasLogWorthyItems;

            string summaryMessage =
                $"Rooms selected: {vm.SelectedRoomIds.Count}\n" +
                $"Floor finishes created: {createdIds.Count}\n" +
                (skippedExistingCount > 0 ? $"Skipped, finish already exists: {skippedExistingCount}\n" : "") +
                (SuppressedWarningCount > 0 ? $"{SuppressedWarningCount} Revit warning(s) suppressed - see log\n" : "") +
                (issueCount > 0 ? $"{issueCount} issues founded - see log\n" : "") +
                (logWriteError != null ? $"Log FAILED to write: {logWriteError}" : "");

            new Warning(DialogTitle, summaryMessage, credit,
                revealPath: showLogButton ? logPath : null).ShowDialog();

            return createdIds.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }

        // ---------------- Room picking ----------------

        private static List<ElementId> PickRooms(UIDocument uidoc, Document doc, RoomPickFilter filter, List<ElementId> current)
        {
            // Feed the current selection back in so the user can add/remove rather than start over.
            IList<Reference> preselected = current
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .Select(e => new Reference(e))
                .ToList();

            try
            {
                IList<Reference> picked = uidoc.Selection.PickObjects(
                    ObjectType.Element, filter,
                    "Select rooms (click, window or crossing), then click Finish on the Options Bar",
                    preselected);

                return picked.Select(r => r.ElementId).Distinct().ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return current;   // Esc keeps what was there before
            }
        }

        /// <summary>
        /// Rooms only, on the view's level, in the active design option. The option check
        /// is explicit because with 'Exclude Options' off, primary-option rooms can still be
        /// picked while working in Main Model.
        /// </summary>
        private class RoomPickFilter : ISelectionFilter
        {
            private readonly ElementId _levelId;
            private readonly ElementId _activeOptionId;

            public RoomPickFilter(ElementId levelId, ElementId activeOptionId)
            {
                _levelId = levelId;
                _activeOptionId = activeOptionId;
            }

            public bool AllowElement(Element elem)
            {
                if (!(elem is Room room)) return false;
                if (room.LevelId != _levelId) return false;

                ElementId roomOptionId = room.DesignOption?.Id ?? ElementId.InvalidElementId;
                return roomOptionId == _activeOptionId;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        // ---------------- Room visibility check ----------------

        private static bool AreRoomsPickable(Document doc, View view)
        {
            // If a view template controls Model V/G, the template's settings are what count.
            View source = view;
            if (view.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(view.ViewTemplateId) is View template)
            {
                ElementId vgModelId = new ElementId(BuiltInParameter.VIS_GRAPHICS_MODEL);
                if (!template.GetNonControlledTemplateParameterIds().Contains(vgModelId))
                    source = template;
            }

            if (IsCategoryHidden(doc, source, BuiltInCategory.OST_Rooms)) return false;

            bool fillHidden = IsCategoryHidden(doc, source, BuiltInCategory.OST_RoomInteriorFill);
            bool referenceHidden = IsCategoryHidden(doc, source, BuiltInCategory.OST_RoomReference);
            return !(fillHidden && referenceHidden);
        }

        private static bool IsCategoryHidden(Document doc, View view, BuiltInCategory bic)
        {
            try
            {
                Category cat = Category.GetCategory(doc, bic);
                return cat != null && view.GetCategoryHidden(cat.Id);
            }
            catch
            {
                return false;   // can't tell -> don't block the user
            }
        }

        // ---------------- Existing finish check ----------------

        private static HashSet<string> CollectExistingFinishNumbers(Document doc, ElementId levelId, ElementId activeOptionId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Floor))
                .Cast<Floor>()
                .Where(f => f.LevelId == levelId &&
                            (f.DesignOption?.Id ?? ElementId.InvalidElementId) == activeOptionId)
                .Select(f => f.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet();
        }

        // ---------------- Geometry ----------------

        private static List<CurveLoop> BuildBoundaryLoops(Room room, SpatialElementBoundaryOptions options, string label)
        {
            IList<IList<BoundarySegment>> segmentLoops = room.GetBoundarySegments(options);
            if (segmentLoops == null || segmentLoops.Count == 0)
            {
                DebugLog.Add($"[Skipped] {label}: no boundary segments returned");
                return null;
            }

            List<CurveLoop> loops = new List<CurveLoop>();
            for (int i = 0; i < segmentLoops.Count; i++)
            {
                CurveLoop loop = new CurveLoop();
                try
                {
                    foreach (BoundarySegment seg in segmentLoops[i])
                    {
                        Curve c = seg.GetCurve();
                        if (c != null) loop.Append(c);
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Add($"[Skipped] {label}: boundary loop {i} could not be built - {ex.Message}");
                    return null;
                }

                if (loop.IsOpen())
                {
                    DebugLog.Add($"[Skipped] {label}: boundary loop {i} is open");
                    return null;
                }
                loops.Add(loop);
            }
            return loops;
        }

        // ---------------- Parameters ----------------

        // Returns the number of parameters that failed to set (each logged).
        private static int SetFloorParameters(Floor floor, Room room, double heightOffset, string label)
        {
            int failures = 0;

            if (!TrySetDouble(floor, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, heightOffset))
            {
                DebugLog.Add($"[Parameter] {label}: could not set Height Offset From Level");
                failures++;
            }

            if (!TrySetString(floor, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, room.Number ?? ""))
            {
                DebugLog.Add($"[Parameter] {label}: could not set Comments");
                failures++;
            }

            if (!TrySetInt(floor, BuiltInParameter.WALL_ATTR_ROOM_BOUNDING, 0))
            {
                DebugLog.Add($"[Parameter] {label}: could not turn off Room Bounding");
                failures++;
            }

            ElementId roomPhaseId = room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId();
            if (roomPhaseId != null && roomPhaseId != ElementId.InvalidElementId &&
                !TrySetElementId(floor, BuiltInParameter.PHASE_CREATED, roomPhaseId))
            {
                DebugLog.Add($"[Parameter] {label}: could not set Phase Created to the room's phase");
                failures++;
            }

            return failures;
        }

        private static bool TrySetDouble(Element e, BuiltInParameter bip, double value)
        {
            Parameter p = e.get_Parameter(bip);
            if (p == null || p.IsReadOnly) return false;
            try { return p.Set(value); } catch { return false; }
        }

        private static bool TrySetString(Element e, BuiltInParameter bip, string value)
        {
            Parameter p = e.get_Parameter(bip);
            if (p == null || p.IsReadOnly) return false;
            try { return p.Set(value); } catch { return false; }
        }

        private static bool TrySetInt(Element e, BuiltInParameter bip, int value)
        {
            Parameter p = e.get_Parameter(bip);
            if (p == null || p.IsReadOnly) return false;
            try { return p.Set(value); } catch { return false; }
        }

        private static bool TrySetElementId(Element e, BuiltInParameter bip, ElementId value)
        {
            Parameter p = e.get_Parameter(bip);
            if (p == null || p.IsReadOnly) return false;
            try { return p.Set(value); } catch { return false; }
        }

        // ---------------- Failure handling ----------------

        // Deletes warnings (overlaps, joins, etc.) so they don't interrupt, and logs each one.
        // Errors are left for Revit's normal handling.
        private class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (FailureMessageAccessor f in failuresAccessor.GetFailureMessages())
                {
                    if (f.GetSeverity() != FailureSeverity.Warning) continue;

                    string ids = string.Join(", ", f.GetFailingElementIds().Select(id => id.Value));
                    DebugLog.Add($"[Revit Warning] {f.GetDescriptionText()} (Elements: {ids})");
                    SuppressedWarningCount++;
                    failuresAccessor.DeleteWarning(f);
                }
                return FailureProcessingResult.Continue;
            }
        }
    }
}
