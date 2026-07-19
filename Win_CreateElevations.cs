using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Brand_25
{
    // Ported from WinEle2_Create_Window_Elevations__R10__Py3.dyn and
    // WinEle2a_Create_Door_Elevations_with_AL_frame__R2__Py3.dyn. Runs AFTER
    // Win_AssignMark.cs — Mark/BA_Mark are only ever READ here (for naming
    // views), never written back; AssignMark owns that.
    //
    // Handles windows (curtain walls tagged BA_TypeFilter = CW) and AL-frame
    // doors (on non-curtain-wall hosts) together in a single run, reusing the
    // same Selection_PhaseDO dialog as Win_AssignMark — in its numeric-fields
    // mode instead of its tie-break mode.
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Win_CreateElevations : IExternalCommand
    {
        private const double MmToFt = 1.0 / 304.8;

        // One element (window or door) queued up for elevation creation.
        private class ElevationTarget
        {
            public Element HostElement;      // the Wall (window) or FamilyInstance (door)
            public bool IsDoor;
            public XYZ Midpoint;
            public XYZ WidthDir;
            public double HalfWidthFt;
            public Level BaseLevel;
            public double BaseOffsetFt;
            public double UnconnectedHeightFt;
            public string RawMark;
            public string ViewName;          // filled in after dedup
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            string credit = "Last Modified by Lok on 2026-07-20. Beta 0.10";

            StringBuilder log = new StringBuilder();
            List<string> issues = new List<string>();

            try
            {
                // ---------------- Phase / Design Option / crop & marker settings ----------------
                // Ordered by sequence so "latest phase" fallbacks make sense, and so the
                // combo box reads top-to-bottom in construction order rather than A-Z.
                List<Phase> phaseElements = doc.Phases.Cast<Phase>()
                    .OrderBy(p => p.get_Parameter(BuiltInParameter.PHASE_SEQUENCE_NUMBER)?.AsInteger() ?? 0)
                    .ToList();
                List<string> phaseNames = phaseElements.Select(p => p.Name).ToList();

                List<string> designOptionNames = new List<string> { "Main Model" };
                designOptionNames.AddRange(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(DesignOption))
                        .Cast<DesignOption>()
                        .Select(d =>
                        {
                            ElementId setId = d.get_Parameter(BuiltInParameter.OPTION_SET_ID).AsElementId();
                            string setName = doc.GetElement(setId)?.Name ?? "Option Set";
                            return $"{setName} : {d.Name}";
                        }));

                // Default to "New Construction" rather than whatever sorts/sequences
                // first (usually "Existing" — rarely what's wanted here). Falls back
                // to the latest phase if "New Construction" isn't in this project.
                string preSelectedPhase = phaseNames.Contains("New Construction")
                    ? "New Construction"
                    : phaseNames.LastOrDefault();

                var dlg = new Selection_PhaseDO(
                    "Create Window/Door Elevations",
                    "Select Phase, Design Option, and crop/marker settings:",
                    phaseNames,
                    designOptionNames,
                    credit,
                    preSelectedPhase: preSelectedPhase,
                    showNumericFields: true);

                if (dlg.ShowDialog() != true)
                    return Result.Cancelled;

                Phase phase = phaseElements.FirstOrDefault(p => p.Name == dlg.SelectedPhase);
                if (phase == null)
                {
                    new Warning(credit, "Selected Phase could not be found.", credit).ShowDialog();
                    return Result.Failed;
                }
                ElementId phaseId = phase.Id;
                string selectedDesignOption = dlg.SelectedDesignOption;

                double sideExtentFt = dlg.SelectedSideExtentMm * MmToFt;
                double topExtentFt = dlg.SelectedTopExtentMm * MmToFt;
                double bottomExtentFt = dlg.SelectedBottomExtentMm * MmToFt;
                double farClipOffsetFt = dlg.SelectedFarClipOffsetMm * MmToFt;
                double markerOffsetFt = dlg.SelectedMarkerOffsetMm * MmToFt;
                int markerScale = (int)Math.Round(dlg.SelectedMarkerRadius); // CreateElevationMarker's 3rd arg is a unitless scale, not a physical size

                // ---------------- Elevation view type ----------------
                List<ViewFamilyType> elevTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .Where(vft => vft.ViewFamily == ViewFamily.Elevation)
                    .ToList();

                if (elevTypes.Count == 0)
                {
                    TaskDialog.Show("Error", "No elevation view types found in the document.");
                    return Result.Cancelled;
                }

                Selection_ElevType elevTypeWindow = new Selection_ElevType(elevTypes, credit);
                if (elevTypeWindow.ShowDialog() != true || elevTypeWindow.SelectedElevType == null)
                {
                    return Result.Cancelled;
                }
                ViewFamilyType elevationType = elevTypeWindow.SelectedElevType;

                // ---------------- Rooms in the selected phase (shared by both categories) ----------------
                List<Room> roomsInPhase = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() == phaseId)
                    .ToList();

                // ---------------- Collect windows (curtain walls tagged BA_TypeFilter = CW) ----------------
                List<Wall> curtainWalls = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .Where(w => IsCurtainWallWindow(doc, w))
                    .Where(w => MatchesPhaseAndDesignOption(w, phaseId, selectedDesignOption))
                    .ToList();

                // ---------------- Collect AL-frame doors on non-curtain-wall hosts ----------------
                // Curtain-wall-hosted doors are covered by the window elevation above —
                // they're part of the CW's own elevation, not a separate one.
                List<FamilyInstance> alDoors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(d => d.Host is Wall hostWall && hostWall.WallType?.Kind != WallKind.Curtain)
                    .Where(HasAluminiumFrame)
                    .Where(d => MatchesPhaseAndDesignOption(d, phaseId, selectedDesignOption))
                    .ToList();

                if (curtainWalls.Count == 0 && alDoors.Count == 0)
                {
                    new Warning(credit, "No curtain walls or AL-frame doors matched the selected Phase / Design Option.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                Dictionary<ElementId, ViewPlan> planViewCache = new Dictionary<ElementId, ViewPlan>();

                List<ElevationTarget> windowTargets = BuildWindowTargets(curtainWalls, issues);
                List<ElevationTarget> doorTargets = BuildDoorTargets(alDoors, issues);

                AssignDedupedViewNames(windowTargets, "Window Elevation");
                AssignDedupedViewNames(doorTargets, "Door Elevation");

                List<ElevationTarget> allTargets = windowTargets.Concat(doorTargets).ToList();

                int created = 0;

                // --- Single pass: markers, elevations, crop shapes, naming, far clip,
                // isolate — all in one transaction, mirroring the proven Dynamo-graph
                // algorithm exactly (see "Create Window/Door Elevations" python node):
                // create the real view immediately at index 0, THEN rotate the marker
                // (which already hosts that view) in place using two half-angle
                // rotations, THEN set the crop shape right away. No dummy views, no
                // deferred second transaction — both turned out to be unnecessary
                // complexity carried over from a different command (Room_CreateIntElev.cs)
                // that doesn't apply here.
                using (Transaction t1 = new Transaction(doc, "LW_CreateWindowDoorElevations"))
                {
                    t1.Start();

                    foreach (ElevationTarget target in allTargets)
                    {
                        string label = $"{(target.IsDoor ? "Door" : "Window")} (Id {target.HostElement.Id})";

                        ViewPlan planView = GetPlanViewForLevel(doc, target.BaseLevel, planViewCache);
                        if (planView == null)
                        {
                            issues.Add($"{label}: no matching floor plan found for its base level; skipped.");
                            continue;
                        }

                        double floorZ = (target.BaseLevel?.Elevation ?? 0.0) + target.BaseOffsetFt;

                        XYZ testPoint = new XYZ(target.Midpoint.X, target.Midpoint.Y, floorZ + 1000.0 * MmToFt);
                        Room hostRoom = FindHostingRoom(testPoint, roomsInPhase, label, issues);

                        XYZ interiorDirection;
                        if (hostRoom != null)
                        {
                            LocationPoint roomLoc = hostRoom.Location as LocationPoint;
                            XYZ roomPoint = roomLoc?.Point ?? testPoint;
                            interiorDirection = new XYZ(roomPoint.X - target.Midpoint.X, roomPoint.Y - target.Midpoint.Y, 0).Normalize();
                        }
                        else
                        {
                            // Fallback when no room is found at this point/phase — VERIFY
                            // this points the right way in your project. Wall.Orientation
                            // isn't guaranteed to point exterior for every wall.
                            Wall referenceWall = target.IsDoor ? (target.HostElement as FamilyInstance)?.Host as Wall : target.HostElement as Wall;
                            interiorDirection = referenceWall != null ? -referenceWall.Orientation : XYZ.BasisX;
                        }

                        XYZ markerPoint = target.Midpoint
                            + interiorDirection * markerOffsetFt
                            + new XYZ(0, 0, 50.0 * MmToFt);

                        // Angle is measured from the element's own midpoint TOWARD the
                        // marker point (matches the graph's combX/combY = elevationPT -
                        // modelMP exactly) — NOT from a separately-computed facing vector.
                        // The explicit combY==0 branches sidestep a +0.0/-0.0 sign edge
                        // case in atan2 that matters once the angle is halved below.
                        double combX = markerPoint.X - target.Midpoint.X;
                        double combY = markerPoint.Y - target.Midpoint.Y;
                        double angle;
                        if (combY == 0 && combX > 0) angle = 0;
                        else if (combY == 0 && combX < 0) angle = Math.PI;
                        else angle = Math.Atan2(combY, combX);

                        ElevationMarker marker;
                        ViewSection view;
                        try
                        {
                            marker = ElevationMarker.CreateElevationMarker(doc, elevationType.Id, markerPoint, markerScale);
                            if (marker == null) { issues.Add($"{label}: failed to create elevation marker."); continue; }

                            view = marker.CreateElevation(doc, planView.Id, 0);
                            if (view == null) { issues.Add($"{label}: CreateElevation returned null."); continue; }

                            // Rotate the marker (with its already-hosted view) in place, as
                            // two half-angle rotations rather than one full-angle rotation —
                            // ported as-is from the graph, which needed this split to avoid
                            // a degenerate case at angle == +/-pi.
                            Line rotationAxis = Line.CreateBound(markerPoint, markerPoint + XYZ.BasisZ);
                            ElementTransformUtils.RotateElement(doc, marker.Id, rotationAxis, angle / 2.0);
                            ElementTransformUtils.RotateElement(doc, marker.Id, rotationAxis, angle / 2.0);
                            doc.Regenerate(); // required under RegenerationOption.Manual before the rotation is reflected
                        }
                        catch (Exception ex)
                        {
                            issues.Add($"{label}: failed to create elevation: {ex.Message}");
                            continue;
                        }

                        try { view.Name = target.ViewName; }
                        catch (Exception ex) { issues.Add($"Failed to rename view (Id {view.Id}) to \"{target.ViewName}\": {ex.Message}"); }

                        Parameter farClipParam = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                        if (farClipParam != null && !farClipParam.IsReadOnly) farClipParam.Set(farClipOffsetFt);

                        // Crop line hidden and the element isolated in its own view — always
                        // on, not user-configurable; there's nothing to decide here.
                        view.CropBoxVisible = false;
                        IsolateElementInView(doc, view, target.HostElement.Id, log);

                        // Crop shape set immediately, same transaction — matches the graph,
                        // which sets it right after rotation with no separate pass. This is
                        // a brand-new shape built from scratch (not editing an auto-fitted
                        // one), so the "separate transaction" restriction that applies to
                        // Room_CreateIntElev.cs's room-fitted crop editing doesn't apply here.
                        double halfWidthFt = target.HalfWidthFt + sideExtentFt;

                        // Crop curve Z values are relative to the view's associated LEVEL,
                        // not absolute world Z (confirmed by testing), and are measured from
                        // the LEVEL LINE itself — not from the wall's own Base Offset. The
                        // marker point / room test above still correctly use absolute floorZ
                        // (which does include BaseOffsetFt, since that's genuine world
                        // geometry); it's only the crop CurveLoop that excludes it.
                        double cropBottomZ = bottomExtentFt;
                        double cropTopZ = target.UnconnectedHeightFt + topExtentFt;
                        try
                        {
                            XYZ p0 = target.Midpoint - target.WidthDir * halfWidthFt;
                            XYZ p1 = target.Midpoint + target.WidthDir * halfWidthFt;

                            XYZ bl = new XYZ(p0.X, p0.Y, cropBottomZ);
                            XYZ br = new XYZ(p1.X, p1.Y, cropBottomZ);
                            XYZ tr = new XYZ(p1.X, p1.Y, cropTopZ);
                            XYZ tl = new XYZ(p0.X, p0.Y, cropTopZ);

                            CurveLoop loop = new CurveLoop();
                            loop.Append(Line.CreateBound(bl, br));
                            loop.Append(Line.CreateBound(br, tr));
                            loop.Append(Line.CreateBound(tr, tl));
                            loop.Append(Line.CreateBound(tl, bl));

                            ViewCropRegionShapeManager cropManager = view.GetCropRegionShapeManager();
                            cropManager.SetCropShape(loop);
                            view.CropBoxActive = true;
                        }
                        catch (Exception ex)
                        {
                            log.AppendLine($"  Warning: failed to set crop shape for view '{view.Name}': {ex.Message}");
                        }

                        created++;
                        log.AppendLine($"{label}: created elevation view '{view.Name}' (Id {view.Id}).");
                    }

                    doc.Regenerate();
                    t1.Commit();
                }

                string summary = $"Windows matched: {curtainWalls.Count}\nDoors matched: {alDoors.Count}\n" +
                    $"Elevations created: {created} of {allTargets.Count}";
                if (issues.Count > 0)
                {
                    summary += $"\n\n{issues.Count} issue(s):\n" + string.Join("\n", issues.Take(20));
                    if (issues.Count > 20) summary += $"\n...and {issues.Count - 20} more.";
                }
                new WarningLarge("Win_CreateElevations", summary, credit).ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ---------------- Collection filters (mirrors Win_AssignMark.cs exactly, so
        // the two commands always agree on what counts as a "window") ----------------

        private static bool IsCurtainWallWindow(Document doc, Element wall)
        {
            ElementType wallType = doc.GetElement(wall.GetTypeId()) as ElementType;
            if (wallType == null) return false;

            string famName = null;
            foreach (Parameter p in wallType.Parameters)
            {
                if (p.Definition.Name == "Family Name")
                {
                    famName = p.AsString();
                    break;
                }
            }
            if (famName != "Curtain Wall") return false;

            Parameter typeFilterParam = wallType.LookupParameter("BA_TypeFilter");
            return typeFilterParam != null && typeFilterParam.AsString() == "CW";
        }

        private static bool HasAluminiumFrame(FamilyInstance door)
        {
            string[] paramNames = { "BA_DoorLeafType", "BA_DoorLeafAType", "BA_DoorFrameType" };
            foreach (Parameter p in door.Parameters)
            {
                if (paramNames.Contains(p.Definition.Name))
                {
                    string val = p.AsValueString() ?? p.AsString();
                    if (val != null && val.Contains("AL")) return true;
                }
            }
            return false;
        }

        private static bool MatchesPhaseAndDesignOption(Element e, ElementId phaseId, string selectedDesignOption)
        {
            Parameter doParam = e.get_Parameter(BuiltInParameter.DESIGN_OPTION_PARAM);
            string dop = doParam?.AsString();
            if (string.IsNullOrEmpty(dop)) dop = selectedDesignOption;
            return dop == selectedDesignOption && e.CreatedPhaseId == phaseId;
        }

        // ---------------- Target building ----------------

        private static List<ElevationTarget> BuildWindowTargets(List<Wall> curtainWalls, List<string> issues)
        {
            List<ElevationTarget> targets = new List<ElevationTarget>();

            foreach (Wall wall in curtainWalls)
            {
                LocationCurve locCurve = wall.Location as LocationCurve;
                if (locCurve == null)
                {
                    issues.Add($"Window (Id {wall.Id}): no location curve; skipped.");
                    continue;
                }

                Curve curve = locCurve.Curve;
                XYZ start = curve.GetEndPoint(0);
                XYZ end = curve.GetEndPoint(1);

                double baseOffsetFt = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0;
                double unconnectedHeightFt = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0.0;

                targets.Add(new ElevationTarget
                {
                    HostElement = wall,
                    IsDoor = false,
                    Midpoint = (start + end) * 0.5,
                    WidthDir = (end - start).Normalize(),
                    HalfWidthFt = curve.Length / 2.0, // side extent (dialog setting) is added at the point of use, in Execute()
                    BaseLevel = GetWallBaseLevel(wall),
                    BaseOffsetFt = baseOffsetFt,
                    UnconnectedHeightFt = unconnectedHeightFt,
                    RawMark = wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""
                });
            }

            return targets;
        }

        private static List<ElevationTarget> BuildDoorTargets(List<FamilyInstance> doors, List<string> issues)
        {
            List<ElevationTarget> targets = new List<ElevationTarget>();

            foreach (FamilyInstance door in doors)
            {
                Wall hostWall = door.Host as Wall;
                LocationPoint doorLoc = door.Location as LocationPoint;
                if (hostWall == null || doorLoc == null)
                {
                    issues.Add($"Door (Id {door.Id}): missing host wall or location point; skipped.");
                    continue;
                }

                LocationCurve hostCurve = hostWall.Location as LocationCurve;
                if (hostCurve == null)
                {
                    issues.Add($"Door (Id {door.Id}): host wall has no location curve; skipped.");
                    continue;
                }

                double baseOffsetFt = hostWall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0.0;
                double unconnectedHeightFt = hostWall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0.0;

                targets.Add(new ElevationTarget
                {
                    HostElement = door,
                    IsDoor = true,
                    Midpoint = doorLoc.Point,
                    WidthDir = (hostCurve.Curve.GetEndPoint(1) - hostCurve.Curve.GetEndPoint(0)).Normalize(),
                    HalfWidthFt = GetDoorWidthFt(door) / 2.0, // side extent (dialog setting) is added at the point of use, in Execute()
                    BaseLevel = GetWallBaseLevel(hostWall),
                    BaseOffsetFt = baseOffsetFt,
                    UnconnectedHeightFt = unconnectedHeightFt,
                    RawMark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""
                });
            }

            return targets;
        }

        // Prefers BA_DoorOverallWidth (double-leaf doors, indicated by BA_DoorLeafA_Width
        // existing on the type) and falls back to Width otherwise. Uses AsDouble()
        // (Revit's internal feet) rather than parsing AsValueString()/304.8, which
        // depends on the project's display-unit formatting.
        private static double GetDoorWidthFt(FamilyInstance door)
        {
            Element type = door.Symbol;
            if (type == null) return 0.0;

            bool isDoubleLeaf = type.Parameters.Cast<Parameter>().Any(p => p.Definition.Name == "BA_DoorLeafA_Width");
            string widthParamName = isDoubleLeaf ? "BA_DoorOverallWidth" : "Width";

            foreach (Parameter p in type.Parameters)
            {
                if (p.Definition.Name == widthParamName) return p.AsDouble();
            }
            return 0.0;
        }

        // Deduped, read-only naming: blanks become "unknown" and repeats get
        // "_Dup.N" appended — mirrors the graph's rename node, but this is only
        // ever applied to the in-memory view name, never written back to Mark.
        private static void AssignDedupedViewNames(List<ElevationTarget> targets, string suffix)
        {
            HashSet<string> seen = new HashSet<string>();
            int dupCounter = 0;

            foreach (ElevationTarget target in targets)
            {
                string name = string.IsNullOrEmpty(target.RawMark) ? "unknown" : target.RawMark;

                if (!seen.Add(name))
                {
                    dupCounter++;
                    name = $"{name}_Dup.{dupCounter}";
                    seen.Add(name);
                }

                target.ViewName = $"{name} {suffix}";
            }
        }

        // Element.LevelId is not reliable for a wall's actual base level — it can
        // reflect the level active in the view where the wall was originally sketched
        // rather than the wall's own Base Constraint. The original Dynamo graph read
        // WALL_BASE_CONSTRAINT directly for exactly this reason; this does the same.
        private static Level GetWallBaseLevel(Wall wall)
        {
            ElementId levelId = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId();
            return levelId != null && levelId != ElementId.InvalidElementId
                ? wall.Document.GetElement(levelId) as Level
                : null;
        }

        // ---------------- Plan view / room lookups ----------------

        private static ViewPlan GetPlanViewForLevel(Document doc, Level level, Dictionary<ElementId, ViewPlan> cache)
        {
            if (level == null) return null;
            if (cache.TryGetValue(level.Id, out ViewPlan cached)) return cached;

            ElementId planViewId = level.FindAssociatedPlanViewId();
            ViewPlan planView = planViewId != ElementId.InvalidElementId ? doc.GetElement(planViewId) as ViewPlan : null;
            cache[level.Id] = planView;
            return planView;
        }

        private static Room FindHostingRoom(XYZ testPoint, List<Room> phaseRooms, string elementLabel, List<string> issues)
        {
            Room found = null;
            foreach (Room room in phaseRooms)
            {
                if (room.IsPointInRoom(testPoint))
                {
                    if (found == null) found = room;
                    else issues.Add($"{elementLabel}: test point falls inside more than one room " +
                        $"(\"{found.Name}\" and \"{room.Name}\") \u2014 using \"{found.Name}\".");
                }
            }
            return found;
        }

        // ---------------- Isolate element in view (by ElementId, not by matching
        // Mark strings against the view name as the original graph did) ----------------

        private static void IsolateElementInView(Document doc, ViewSection view, ElementId keepHostId, StringBuilder log)
        {
            BuiltInCategory[] categories =
            {
                BuiltInCategory.OST_Walls, BuiltInCategory.OST_CurtainWallPanels,
                BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows,
                BuiltInCategory.OST_Cornices, BuiltInCategory.OST_Reveals
            };

            List<ElementId> toHide = new List<ElementId>();

            foreach (BuiltInCategory cat in categories)
            {
                IList<Element> elems = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(cat)
                    .WhereElementIsNotElementType()
                    .ToElements();

                foreach (Element e in elems)
                {
                    if (e.Id == keepHostId) continue;
                    if (e is FamilyInstance fi && fi.Host != null && fi.Host.Id == keepHostId) continue;
                    if (!e.IsHidden(view) && e.CanBeHidden(view)) toHide.Add(e.Id);
                }
            }

            if (toHide.Count > 0)
            {
                try { view.HideElements(toHide); }
                catch (Exception ex) { log.AppendLine($"  Warning: failed to isolate element in view '{view.Name}': {ex.Message}"); }
            }
        }
    }
}
