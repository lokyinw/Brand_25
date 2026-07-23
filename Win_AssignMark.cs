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
    [Transaction(TransactionMode.Manual)]
    public class Win_AssignMark : IExternalCommand
    {
        private const double MmToFt = 1.0 / 304.8;
        private const double ProbeOffsetFt = 500.0 * MmToFt;   // 500mm out from centerline
        private const double ProbeHeightFt = 50.0 * MmToFt;    // 50mm up from base offset

        // Per-element working data carried from geometry through naming.
        private class MarkTarget
        {
            public Element Element;
            public XYZ Probe1;   // one side of the centerline
            public XYZ Probe2;   // other side of the centerline
            public Room RoomA;   // room found at Probe1 (filled in later)
            public Room RoomB;   // room found at Probe2
            public string RawName; // room-number-based name before prefix/dedup, or "_NoRoom"
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            string credit = "Last Modified by Lok on 2026-07-20. Beta 0.10";

            // ---------------- Phase / Design Option / tie-break dialog ----------------
            // Ordered by sequence so "latest phase" fallbacks make sense, and so the
            // combo box reads top-to-bottom in construction order rather than A-Z.
            List<Phase> phaseElements = doc.Phases.Cast<Phase>()
                .OrderBy(p => p.get_Parameter(BuiltInParameter.PHASE_SEQUENCE_NUMBER)?.AsInteger() ?? 0)
                .ToList();
            List<string> phaseNames = phaseElements.Select(p => p.Name).ToList();

            // Default to "New Construction" rather than whatever sorts/sequences first
            // (usually "Existing" — rarely what's wanted here). Falls back to the
            // latest phase if "New Construction" isn't in this project.
            string preSelectedPhase = phaseNames.Contains("New Construction")
                ? "New Construction"
                : phaseNames.LastOrDefault();

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
                    })
            );

            var dlg = new Selection_PhaseDO(
                "Assign Window Mark",
                "Select Phase, Design Option, and naming rule:",
                phaseNames,
                designOptionNames,
                credit,
                preSelectedPhase: preSelectedPhase);

            if (dlg.ShowDialog() != true)
                return Result.Cancelled;

            string selectedDesignOption = dlg.SelectedDesignOption;
            MarkTieBreak tieBreak = dlg.SelectedTieBreak;

            Phase phase = phaseElements.FirstOrDefault(p => p.Name == dlg.SelectedPhase);
            if (phase == null)
            {
                new Warning("Assign Window Mark", "Selected Phase could not be found.", "Brand_25 \u2013 Assign Mark").ShowDialog();
                return Result.Failed;
            }
            ElementId phaseId = phase.Id;

            // ---------------- Collect Windows (curtain walls tagged BA_TypeFilter = CW) ----------------
            List<Element> windows = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Where(w => IsCurtainWallWindow(doc, w))
                .Where(w => MatchesPhaseAndDesignOption(w, phaseId, selectedDesignOption))
                .ToList();

            // ---------------- Collect Doors ----------------
            List<Element> doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .Where(d => MatchesPhaseAndDesignOption(d, phaseId, selectedDesignOption))
                .ToList();

            // ---------------- Rooms in the selected phase (shared by both categories) ----------------
            // Rooms are phase-specific via their "Phase" instance parameter (ROOM_PHASE),
            // not necessarily CreatedPhaseId - a room can persist across phases without being
            // recreated, so CreatedPhaseId can miss rooms that legitimately belong to this phase.
            List<Room> roomsInPhase = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() == phaseId)
                .ToList();

            List<string> skipped = new List<string>();
            int ambiguousCount = 0;

            List<MarkTarget> windowTargets = BuildTargets(windows, isDoor: false, roomsInPhase, skipped, ref ambiguousCount);
            List<MarkTarget> doorTargets = BuildTargets(doors, isDoor: true, roomsInPhase, skipped, ref ambiguousCount);

            ResolveRawNames(windowTargets, tieBreak);
            ResolveRawNames(doorTargets, tieBreak);

            Dictionary<MarkTarget, string> windowMarks = BuildMarkNames(windowTargets, "W");
            Dictionary<MarkTarget, string> doorMarks = BuildMarkNames(doorTargets, "D");

            // ---------------- Write parameters, one transaction ----------------
            int baMarkMissingCount = 0;

            using (Transaction t = new Transaction(doc, "LW_Assign Window/Door Mark"))
            {
                t.Start();

                foreach (var kvp in windowMarks)
                    if (!TrySetMarks(kvp.Key.Element, kvp.Value)) baMarkMissingCount++;

                foreach (var kvp in doorMarks)
                    if (!TrySetMarks(kvp.Key.Element, kvp.Value)) baMarkMissingCount++;

                t.Commit();
            }

            new WarningLarge("Assign Window Mark",
                $"Rooms found in selected phase: {roomsInPhase.Count}\n" +
                $"Windows processed: {windowTargets.Count}\n" +
                $"Doors processed: {doorTargets.Count}\n" +
                $"Elements skipped (geometry issue): {skipped.Count}\n" +
                $"Elevation Marker Point located in more than one room: {ambiguousCount}\n" +
                $"Elements missing BA_Mark parameter (Mark still set): {baMarkMissingCount}",
                credit).ShowDialog();

            return Result.Succeeded;
        }

        // ---------------- Collection filters ----------------

        private static bool IsCurtainWallWindow(Document doc, Element wall)
        {
            ElementType wallType = doc.GetElement(wall.GetTypeId()) as ElementType;
            if (wallType == null) return false;

            // Matches the original script's approach: iterate parameters looking for
            // "Family Name" by Definition.Name, since neither a BuiltInParameter nor
            // ElementType.Family.Name reliably carries "Curtain Wall" for this system
            // family type across Revit versions.
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

        private static bool MatchesPhaseAndDesignOption(Element e, ElementId phaseId, string selectedDesignOption)
        {
            Parameter doParam = e.get_Parameter(BuiltInParameter.DESIGN_OPTION_PARAM);
            string dop = doParam?.AsString();
            // Not in any Design Option -> the parameter is either missing (null) or present but
            // empty (""), depending on element/category. Either way, treat it as auto-matching
            // whatever was selected (i.e. "Main Model").
            if (string.IsNullOrEmpty(dop)) dop = selectedDesignOption;
            return dop == selectedDesignOption && e.CreatedPhaseId == phaseId;
        }

        // ---------------- Geometry + room lookup ----------------

        private static List<MarkTarget> BuildTargets(
            List<Element> source, bool isDoor, List<Room> roomsInPhase,
            List<string> skipped, ref int ambiguousCount)
        {
            List<MarkTarget> targets = new List<MarkTarget>();

            foreach (Element e in source)
            {
                Line centerline = isDoor ? GetDoorCenterline(e as FamilyInstance) : GetWallCenterline(e);
                if (centerline == null)
                {
                    skipped.Add($"{e.Category?.Name} {e.Id}: could not resolve a centerline");
                    continue;
                }

                double baseOffsetFt = 0.0;
                if (!isDoor)
                {
                    Parameter boParam = e.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                    baseOffsetFt = boParam?.AsDouble() ?? 0.0;
                }

                XYZ dir = centerline.Direction;
                XYZ nor = dir.CrossProduct(XYZ.BasisZ).Normalize();
                XYZ mpt = centerline.GetEndPoint(0).Add(centerline.GetEndPoint(1)).Divide(2.0);
                XYZ mpt50 = new XYZ(mpt.X, mpt.Y, mpt.Z + baseOffsetFt + ProbeHeightFt);

                MarkTarget target = new MarkTarget
                {
                    Element = e,
                    Probe1 = mpt50 + nor.Multiply(ProbeOffsetFt),
                    Probe2 = mpt50 + nor.Negate().Multiply(ProbeOffsetFt)
                };

                target.RoomA = FindRoomForPoint(target.Probe1, roomsInPhase, ref ambiguousCount);
                target.RoomB = FindRoomForPoint(target.Probe2, roomsInPhase, ref ambiguousCount);

                targets.Add(target);
            }

            return targets;
        }

        private static Line GetWallCenterline(Element wall)
        {
            LocationCurve loc = wall.Location as LocationCurve;
            return loc?.Curve as Line;
        }

        private static Line GetDoorCenterline(FamilyInstance door)
        {
            if (door == null) return null;

            LocationPoint lp = door.Location as LocationPoint;
            if (lp != null && door.HandOrientation != null)
            {
                try
                {
                    XYZ p0 = lp.Point + door.HandOrientation;
                    XYZ p1 = lp.Point - door.HandOrientation;
                    return Line.CreateBound(p0, p1);
                }
                catch
                {
                    // fall through to host fallback
                }
            }

            // Fallback: door has no usable Location.Point/HandOrientation - use the host wall's centerline.
            LocationCurve hostLoc = door.Host?.Location as LocationCurve;
            return hostLoc?.Curve as Line;
        }

        private static Room FindRoomForPoint(XYZ pt, List<Room> roomsInPhase, ref int ambiguousCount)
        {
            Room found = null;
            bool ambiguous = false;

            foreach (Room r in roomsInPhase)
            {
                if (r.IsPointInRoom(pt))
                {
                    if (found == null)
                        found = r;
                    else
                        ambiguous = true;
                }
            }

            if (ambiguous) ambiguousCount++;
            return found;
        }

        // ---------------- Naming ----------------

        private static void ResolveRawNames(List<MarkTarget> targets, MarkTieBreak tieBreak)
        {
            foreach (MarkTarget target in targets)
            {
                Room winner = PickRoom(target.RoomA, target.RoomB, tieBreak);
                target.RawName = winner == null ? "_NoRoom" : ParseRoomNumber(winner, out string suffix) + suffix;
            }
        }

        private static Room PickRoom(Room rA, Room rB, MarkTieBreak tieBreak)
        {
            if (rA != null && rB == null) return rA;
            if (rB != null && rA == null) return rB;
            if (rA == null && rB == null) return null;

            switch (tieBreak)
            {
                case MarkTieBreak.SmallerArea:
                    return rA.Area < rB.Area ? rA : rB;
                case MarkTieBreak.LargerArea:
                    return rA.Area > rB.Area ? rA : rB;
                case MarkTieBreak.SmallerRoomNumber:
                    return LastTwoDigits(rA) < LastTwoDigits(rB) ? rA : rB;
                case MarkTieBreak.LargerRoomNumber:
                    return LastTwoDigits(rA) > LastTwoDigits(rB) ? rA : rB;
                default:
                    return rA;
            }
        }

        // Splits a room number like "101A" into ("101", "A"); "101" into ("101", "").
        // Mirrors the original Dynamo script's try/except-on-last-char approach.
        private static string ParseRoomNumber(Room room, out string suffix)
        {
            string number = room.Number ?? "";
            if (number.Length == 0) { suffix = ""; return ""; }

            char last = number[number.Length - 1];
            if (char.IsDigit(last))
            {
                suffix = "";
                return number;
            }
            suffix = last.ToString();
            return number.Substring(0, number.Length - 1);
        }

        private static int LastTwoDigits(Room room)
        {
            string numStr = ParseRoomNumber(room, out _);
            string tail = numStr.Length >= 2 ? numStr.Substring(numStr.Length - 2) : numStr;
            int.TryParse(tail, out int value);
            return value;
        }

        // Sorts by RawName (stable), groups identical names, and assigns ".1", ".2", etc.
        // in original-list order within each group - same effect as the Python
        // sorted()+groupby() dedup in the source graphs.
        private static Dictionary<MarkTarget, string> BuildMarkNames(List<MarkTarget> targets, string prefix)
        {
            var result = new Dictionary<MarkTarget, string>();
            var ordered = targets
                .Select((t, idx) => (Target: t, Idx: idx))
                .OrderBy(x => x.Target.RawName, StringComparer.Ordinal)
                .ToList();

            string prevName = null;
            int counter = 0;
            foreach (var item in ordered)
            {
                counter = item.Target.RawName == prevName ? counter + 1 : 1;
                prevName = item.Target.RawName;
                result[item.Target] = $"{prefix}{item.Target.RawName}.{counter}";
            }

            return result;
        }

        // Sets Mark always; sets BA_Mark only if the parameter exists on this element.
        // Returns false if BA_Mark was missing (for the summary count), true otherwise.
        private static bool TrySetMarks(Element e, string value)
        {
            Parameter markParam = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            markParam?.Set(value);

            Parameter baMarkParam = e.LookupParameter("BA_Mark");
            if (baMarkParam != null && !baMarkParam.IsReadOnly)
            {
                baMarkParam.Set(value);
                return true;
            }
            return false;
        }
    }
}
