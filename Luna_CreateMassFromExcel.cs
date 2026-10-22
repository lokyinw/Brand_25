using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Brand_25
{
    /// <summary>
    /// Creates one MASS_AreaPlanning_Box instance per room in an Excel room schedule.
    /// - Excel: "Input Table" tab = rooms; "Data" tab = Area Group -> colour (family type "COLOUR xxx").
    /// - Area Group not in the Data tab -> LEARNING BASE (logged). Blank Level -> "Ground", or the
    ///   lowest level if the model has no "Ground". Area missing / not positive -> 60 m2 (logged and
    ///   reported in the result dialog). Blank Height -> 3500 mm.
    /// - Length : Width = 4 : 5. BA_Area and BA_Length are written; BA_Width is a family formula.
    /// - Layout per level: starts at the A-1 grid intersection (top-left corner of the first mass),
    ///   left to right (+X) with the user's spacing, new row downward (-Y) when the row length would be
    ///   exceeded. Order within a level follows the Excel order.
    /// - The layout is built from each mass's MEASURED bounding box (after the parameters are set and the
    ///   model regenerated), so it does not depend on where the family origin sits or which way its
    ///   Length / Width run. Corners, sizes and any overlaps are written to the log.
    /// - Masses that already exist (same BA_Building + BA_Level + BA_Briefed Area Name) are skipped and
    ///   logged; their slot in the layout stays reserved.
    /// - Afterwards: Show Mass is switched on and the active view becomes a 3D view.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Luna_CreateMassFromExcel : IExternalCommand
    {
        private const string DialogTitle = "Create Mass From Excel";
        private const string FamilyName = "MASS_AreaPlanning_Box";
        private const string FallbackAreaGroup = "LEARNING BASE";
        private const string DefaultLevelName = "Ground";
        private const double DefaultAreaM2 = 60.0;
        private const double DefaultHeightMm = 3500.0;
        private const double LengthToWidthRatio = 0.8;   // L : W = 4 : 5  (L^2 = ratio * Area)

        private static readonly List<string> DebugLog = new List<string>();
        private static int SuppressedWarningCount;

        // Remembered for the rest of the Revit session.
        private static double _lastSpacingMm = 2500.0;
        private static double _lastRowLengthMm = 50000.0;

        // One room from the Input Table, plus everything resolved for it.
        private class RoomRow
        {
            public int ExcelRow;
            public string Building, LevelText, AreaGroupText, SpaceType, RoomName;
            public double? Area, Height;

            public string AreaGroupWritten;   // text written to BA_Area Group
            public string TypeName;           // "COLOUR xxx"
            public FamilySymbol Symbol;
            public Level Level;
            public double AreaM2, HeightMm;
            public bool AreaDefaulted;
            public double LengthFt, WidthFt;
            public FamilyInstance Inst;          // set once created
            public bool Measured;                // true when the bounding box could be read
            public double MeasuredW, MeasuredD;  // measured X / Y extent (internal ft)
            public XYZ ExpectedTopLeft;          // where layout wants the top-left corner
            public bool AlreadyExists;
            public string Label => $"Row {ExcelRow} '{RoomName}' ({Building} / {Level?.Name})";
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            string credit = "Last Modified by Lok on 2026-10-02. Beta 0.1";

            DebugLog.Clear();
            SuppressedWarningCount = 0;

            // ---------------- 1. Pick the Excel file ----------------
            Microsoft.Win32.OpenFileDialog ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select the Excel room list",
                Filter = "Excel workbook (*.xlsx;*.xlsm)|*.xlsx;*.xlsm",
                CheckFileExists = true
            };
            if (ofd.ShowDialog() != true)
                return Result.Cancelled;
            string excelPath = ofd.FileName;

            // ---------------- 2. Pre-checks on the model ----------------
            Family family = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, FamilyName, StringComparison.OrdinalIgnoreCase));
            if (family == null)
            {
                new Warning(DialogTitle,
                    $"The Mass family '{FamilyName}' was not found in this model.\n\nLoad the family and its COLOUR types, then run the command again.",
                    credit).ShowDialog();
                return Result.Cancelled;
            }

            Dictionary<string, FamilySymbol> symbols = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
            foreach (ElementId id in family.GetFamilySymbolIds())
            {
                FamilySymbol s = doc.GetElement(id) as FamilySymbol;
                if (s != null) symbols[s.Name.Trim()] = s;
            }

            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();
            if (levels.Count == 0)
            {
                new Warning(DialogTitle, "No levels found in this model.", credit).ShowDialog();
                return Result.Cancelled;
            }

            if (!TryGetGridIntersection(doc, out XYZ origin, out string gridError))
            {
                new Warning(DialogTitle, gridError, credit).ShowDialog();
                return Result.Cancelled;
            }

            // ---------------- 3. Read the Excel ----------------
            List<RoomRow> rows;
            Dictionary<string, string> groupToColour;
            try
            {
                if (!TryReadWorkbook(excelPath, out rows, out groupToColour, out string readError))
                {
                    new Warning(DialogTitle, readError, credit).ShowDialog();
                    return Result.Cancelled;
                }
            }
            catch (Exception ex)
            {
                new Warning(DialogTitle, $"Could not read the Excel file:\n{ex.Message}", credit).ShowDialog();
                return Result.Cancelled;
            }

            if (rows.Count == 0)
            {
                new Warning(DialogTitle, "No rooms found in the 'Input Table' tab.", credit).ShowDialog();
                return Result.Cancelled;
            }

            // ---------------- 4. Layout settings ----------------
            Input_MassLayout dlg = new Input_MassLayout(_lastSpacingMm, _lastRowLengthMm, credit);
            if (dlg.ShowDialog() != true)
                return Result.Cancelled;
            _lastSpacingMm = dlg.SpacingMm;
            _lastRowLengthMm = dlg.RowLengthMm;
            double spacingFt = UnitUtils.ConvertToInternalUnits(dlg.SpacingMm, UnitTypeId.Millimeters);
            double rowLengthFt = UnitUtils.ConvertToInternalUnits(dlg.RowLengthMm, UnitTypeId.Millimeters);

            DebugLog.Add($"[Settings] File: {excelPath}");
            DebugLog.Add($"[Settings] Spacing: {dlg.SpacingMm} mm, Row length: {dlg.RowLengthMm} mm, Rooms in Excel: {rows.Count}, " +
                         $"A-1 intersection (internal ft): X={origin.X:0.###}, Y={origin.Y:0.###}");

            // ---------------- 5. Resolve each row ----------------
            int issueCount = 0;
            int areaDefaultedCount = 0;
            int skippedRowCount = 0;
            Level lowestLevel = levels[0];
            Dictionary<string, Level> levelByName = new Dictionary<string, Level>(StringComparer.OrdinalIgnoreCase);
            foreach (Level l in levels) levelByName[l.Name.Trim()] = l;

            Level groundLevel;
            if (!levelByName.TryGetValue(DefaultLevelName, out groundLevel))
                groundLevel = lowestLevel;

            List<RoomRow> valid = new List<RoomRow>();
            foreach (RoomRow r in rows)
            {
                // Level
                if (string.IsNullOrWhiteSpace(r.LevelText))
                {
                    r.Level = groundLevel;
                    DebugLog.Add($"[Level] Row {r.ExcelRow} '{r.RoomName}': Level blank -> '{r.Level.Name}'" +
                                 (r.Level == lowestLevel && !levelByName.ContainsKey(DefaultLevelName) ? $" ('{DefaultLevelName}' not in model, lowest level used)" : ""));
                }
                else if (levelByName.TryGetValue(r.LevelText.Trim(), out Level found))
                {
                    r.Level = found;
                }
                else
                {
                    r.Level = groundLevel;
                    DebugLog.Add($"[Issue] Row {r.ExcelRow} '{r.RoomName}': Level '{r.LevelText}' not found in model -> '{r.Level.Name}'");
                    issueCount++;
                }

                // Area Group -> colour -> family type
                string groupKey = Normalise(r.AreaGroupText);
                string colour;
                if (groupKey.Length == 0 || !groupToColour.TryGetValue(groupKey, out colour))
                {
                    DebugLog.Add($"[Issue] Row {r.ExcelRow} '{r.RoomName}': Area Group '{r.AreaGroupText}' not in Data tab -> '{FallbackAreaGroup}'");
                    issueCount++;
                    r.AreaGroupWritten = FallbackAreaGroup;
                    groupToColour.TryGetValue(FallbackAreaGroup, out colour);
                }
                else
                {
                    r.AreaGroupWritten = r.AreaGroupText.Trim();
                }

                r.TypeName = colour == null ? null : $"COLOUR {colour}".ToUpperInvariant();
                if (r.TypeName == null || !symbols.TryGetValue(r.TypeName, out r.Symbol))
                {
                    DebugLog.Add($"[Skipped] Row {r.ExcelRow} '{r.RoomName}': family type '{r.TypeName ?? "(no colour for LEARNING BASE in Data tab)"}' not found in '{FamilyName}'");
                    issueCount++;
                    skippedRowCount++;
                    continue;
                }

                // Area
                if (r.Area.HasValue && r.Area.Value > 0.0)
                {
                    r.AreaM2 = r.Area.Value;
                }
                else
                {
                    r.AreaM2 = DefaultAreaM2;
                    r.AreaDefaulted = true;
                    areaDefaultedCount++;
                    issueCount++;
                    DebugLog.Add($"[Issue] Row {r.ExcelRow} '{r.RoomName}': Room Required Area missing or not positive -> {DefaultAreaM2} m2");
                }

                // Height (silent default)
                r.HeightMm = r.Height.HasValue && r.Height.Value > 0.0 ? r.Height.Value : DefaultHeightMm;

                // Size, L : W = 4 : 5
                double areaFt2 = UnitUtils.ConvertToInternalUnits(r.AreaM2, UnitTypeId.SquareMeters);
                r.LengthFt = Math.Sqrt(LengthToWidthRatio * areaFt2);
                r.WidthFt = areaFt2 / r.LengthFt;

                valid.Add(r);
            }

            // ---------------- 7. Existing masses (skip + log) ----------------
            Dictionary<string, int> existing = new Dictionary<string, int>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc)
                         .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                         .Where(f => f.Symbol != null && f.Symbol.Family.Id == family.Id))
            {
                string key = ExistingKey(GetText(fi, "BA_Building"), GetText(fi, "BA_Level"), GetText(fi, "BA_Briefed Area Name"));
                existing.TryGetValue(key, out int n);
                existing[key] = n + 1;
            }

            int skippedExistingCount = 0;
            foreach (RoomRow r in valid.OrderBy(x => x.ExcelRow))
            {
                string key = ExistingKey(r.Building, r.Level.Name, r.RoomName);
                if (existing.TryGetValue(key, out int n) && n > 0)
                {
                    existing[key] = n - 1;
                    r.AlreadyExists = true;
                    skippedExistingCount++;
                    DebugLog.Add($"[Skipped - Existing] {r.Label}: a mass with the same Building / Level / Room Name already exists");
                }
            }

            // ---------------- 8. Create ----------------
            List<ElementId> createdIds = new List<ElementId>();
            int paramIssueCount = 0;
            string commitError = null;

            using (Transaction t = new Transaction(doc, "LW_Create Mass From Excel"))
            {
                t.Start();

                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(fho);

                foreach (FamilySymbol s in valid.Select(r => r.Symbol).Distinct())
                    if (!s.IsActive) s.Activate();
                doc.Regenerate();

                // Every mass is first dropped at A-1 on its level; the layout pass below moves it once its
                // real footprint is known.
                Func<RoomRow, XYZ> provisional = r0 => new XYZ(origin.X, origin.Y, r0.Level.Elevation);

                Dictionary<ElementId, RoomRow> createdRows = new Dictionary<ElementId, RoomRow>();
                foreach (RoomRow r in valid.Where(x => !x.AlreadyExists).OrderBy(x => x.Level.Elevation).ThenBy(x => x.ExcelRow))
                {
                    FamilyInstance inst;
                    try
                    {
                        inst = doc.Create.NewFamilyInstance(provisional(r), r.Symbol, StructuralType.NonStructural);
                    }
                    catch (Exception ex1)
                    {
                        try
                        {
                            inst = doc.Create.NewFamilyInstance(provisional(r), r.Symbol, r.Level, StructuralType.NonStructural);
                        }
                        catch (Exception ex2)
                        {
                            DebugLog.Add($"[Skipped] {r.Label}: NewFamilyInstance failed - {ex1.Message} / {ex2.Message}");
                            issueCount++;
                            skippedRowCount++;
                            continue;
                        }
                    }

                    double areaFt2 = UnitUtils.ConvertToInternalUnits(r.AreaM2, UnitTypeId.SquareMeters);
                    paramIssueCount += SetDouble(inst, "BA_Required Area", areaFt2, r.Label);
                    paramIssueCount += SetDouble(inst, "BA_Area", areaFt2, r.Label);
                    paramIssueCount += SetDouble(inst, "BA_Length", r.LengthFt, r.Label);   // BA_Width is a formula
                    paramIssueCount += SetDouble(inst, "BA_Height", UnitUtils.ConvertToInternalUnits(r.HeightMm, UnitTypeId.Millimeters), r.Label);
                    paramIssueCount += SetText(inst, "BA_Building", r.Building, r.Label);
                    paramIssueCount += SetText(inst, "BA_Level", r.Level.Name, r.Label);
                    paramIssueCount += SetText(inst, "BA_Space Type", r.SpaceType, r.Label);
                    paramIssueCount += SetText(inst, "BA_Briefed Area Name", r.RoomName, r.Label);
                    paramIssueCount += SetAreaGroup(inst, r.AreaGroupWritten, r.Label);

                    r.Inst = inst;
                    createdIds.Add(inst.Id);
                    createdRows[inst.Id] = r;
                    DebugLog.Add($"[Created] {r.Label} -> {r.TypeName}, Mass {inst.Id.Value}, {r.AreaM2:0.##} m2" +
                                 (r.AreaDefaulted ? " (default area)" : ""));
                }

                // ---- Measure real footprints (after the family formulas have run) ----
                doc.Regenerate();
                foreach (RoomRow r in createdRows.Values)
                {
                    BoundingBoxXYZ bb = r.Inst.get_BoundingBox(null);
                    if (bb == null)
                    {
                        DebugLog.Add($"[Measure] {r.Label}: no bounding box - using computed size and assuming the origin is the centre");
                        continue;
                    }
                    r.Measured = true;
                    r.MeasuredW = bb.Max.X - bb.Min.X;
                    r.MeasuredD = bb.Max.Y - bb.Min.Y;
                    DebugLog.Add($"[Measure] {r.Label}: footprint {Mm(r.MeasuredW):0} x {Mm(r.MeasuredD):0} mm (X x Y); " +
                                 $"expected Length {Mm(r.LengthFt):0} x Width {Mm(r.WidthFt):0} mm; " +
                                 $"BA_Length = {Mm(ReadDouble(r.Inst, "BA_Length")):0}, BA_Width = {Mm(ReadDouble(r.Inst, "BA_Width")):0} mm; " +
                                 $"min corner offset from origin point: ({Mm(bb.Min.X - r.Inst.GetTransform().Origin.X):0}, {Mm(bb.Min.Y - r.Inst.GetTransform().Origin.Y):0}) mm");
                }

                // Which way does the family's Length run? Used only for slots of skipped (existing) masses.
                bool lengthAlongX = true;
                RoomRow probe = valid.FirstOrDefault(x => x.Measured);
                if (probe != null)
                    lengthAlongX = Math.Abs(probe.MeasuredW - probe.LengthFt) <= Math.Abs(probe.MeasuredW - probe.WidthFt);
                DebugLog.Add($"[Layout] A-1 = ({Mm(origin.X):0}, {Mm(origin.Y):0}) mm model coordinates; family Length runs along {(lengthAlongX ? "X" : "Y")}; " +
                             $"spacing {Mm(spacingFt):0} mm, row length {Mm(rowLengthFt):0} mm");

                // ---- Layout: per level, Excel order, left to right then downward ----
                foreach (var levelGroup in valid.GroupBy(r => r.Level.Id).OrderBy(g => g.First().Level.Elevation))
                {
                    double cursor = 0.0, rowTop = origin.Y, rowDepth = 0.0;
                    int inRow = 0, rowNo = 1;
                    foreach (RoomRow r in levelGroup.OrderBy(x => x.ExcelRow))
                    {
                        double w = r.Measured ? r.MeasuredW : (lengthAlongX ? r.LengthFt : r.WidthFt);
                        double d = r.Measured ? r.MeasuredD : (lengthAlongX ? r.WidthFt : r.LengthFt);

                        if (inRow > 0 && cursor + w > rowLengthFt)
                        {
                            rowTop -= rowDepth + spacingFt;   // next row, downward
                            cursor = 0.0;
                            rowDepth = 0.0;
                            inRow = 0;
                            rowNo++;
                        }

                        r.ExpectedTopLeft = new XYZ(origin.X + cursor, rowTop, r.Level.Elevation);

                        if (r.Inst != null)
                        {
                            XYZ delta;
                            BoundingBoxXYZ bb = r.Inst.get_BoundingBox(null);
                            if (r.Measured && bb != null)
                                delta = new XYZ(r.ExpectedTopLeft.X - bb.Min.X, r.ExpectedTopLeft.Y - bb.Max.Y, 0.0);
                            else   // assume the origin is the centre of the box
                                delta = new XYZ(r.ExpectedTopLeft.X + w / 2.0 - origin.X, r.ExpectedTopLeft.Y - d / 2.0 - origin.Y, 0.0);

                            if (delta.GetLength() > 1e-9)
                                ElementTransformUtils.MoveElement(doc, r.Inst.Id, delta);
                        }

                        cursor += w + spacingFt;
                        rowDepth = Math.Max(rowDepth, d);
                        inRow++;
                    }
                }

                // ---- Verify and log the final corners; flag any overlaps on the same level ----
                doc.Regenerate();
                List<RoomRow> placed = valid.Where(x => x.Inst != null).ToList();
                Dictionary<RoomRow, BoundingBoxXYZ> finalBox = new Dictionary<RoomRow, BoundingBoxXYZ>();
                foreach (RoomRow r in placed.OrderBy(x => x.Level.Elevation).ThenBy(x => x.ExcelRow))
                {
                    BoundingBoxXYZ bb = r.Inst.get_BoundingBox(null);
                    if (bb == null) continue;
                    finalBox[r] = bb;
                    double errX = Mm(bb.Min.X - r.ExpectedTopLeft.X), errY = Mm(bb.Max.Y - r.ExpectedTopLeft.Y);
                    DebugLog.Add($"[Corners] {r.Label}: top-left ({Mm(bb.Min.X - origin.X):0}, {Mm(bb.Max.Y - origin.Y):0}) bottom-right ({Mm(bb.Max.X - origin.X):0}, {Mm(bb.Min.Y - origin.Y):0}) mm from A-1" +
                                 (Math.Abs(errX) > 1.0 || Math.Abs(errY) > 1.0 ? $"  <-- top-left is off the planned position by ({errX:0}, {errY:0}) mm" : ""));
                }
                int overlapCount = 0;
                foreach (var levelGroup in finalBox.Keys.GroupBy(r => r.Level.Id))
                {
                    List<RoomRow> list = levelGroup.OrderBy(x => x.ExcelRow).ToList();
                    for (int i = 0; i < list.Count; i++)
                        for (int j = i + 1; j < list.Count; j++)
                        {
                            BoundingBoxXYZ a = finalBox[list[i]], b = finalBox[list[j]];
                            double ox = Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X);
                            double oy = Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y);
                            if (ox > 1e-4 && oy > 1e-4)
                            {
                                overlapCount++;
                                DebugLog.Add($"[Overlap] {list[i].Label} and {list[j].Label} overlap by {Mm(ox):0} x {Mm(oy):0} mm");
                            }
                        }
                }
                if (overlapCount > 0) issueCount++;

                // Show Mass (document-wide temporary override).
                EnableShowMass(doc);

                TransactionStatus status = t.Commit();
                if (status != TransactionStatus.Committed)
                {
                    commitError = $"Transaction ended with status {status}";
                    DebugLog.Add($"[Error] {commitError}");
                    createdIds.Clear();
                }
            }

            // ---------------- 9. Make sure the user is looking at a 3D view ----------------
            if (commitError == null)
            {
                try { SwitchTo3D(uidoc, doc); }
                catch (Exception ex) { DebugLog.Add($"[Warning] Could not switch to a 3D view: {ex.Message}"); }

                if (createdIds.Count > 0)
                {
                    try { uidoc.Selection.SetElementIds(createdIds); } catch { }
                }
            }

            // ---------------- 10. Log + summary ----------------
            issueCount += paramIssueCount + (commitError != null ? 1 : 0);

            DebugLog.Add($"[Summary] Rooms in Excel: {rows.Count}, Created: {createdIds.Count}, Skipped (issues): {skippedRowCount}, " +
                         $"Skipped (existing): {skippedExistingCount}, Area defaulted: {areaDefaultedCount}, Parameter issues: {paramIssueCount}, " +
                         $"Revit warnings suppressed: {SuppressedWarningCount}");

            string logPath = Path.Combine(Path.GetTempPath(), $"Luna_CreateMassFromExcel_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            string logWriteError = null;
            try { File.WriteAllLines(logPath, DebugLog); }
            catch (Exception ex) { logWriteError = ex.Message; }

            bool hasLogWorthyItems = issueCount > 0 || skippedExistingCount > 0 || SuppressedWarningCount > 0;
            bool showLogButton = logWriteError == null && hasLogWorthyItems;

            string summaryMessage =
                $"Rooms in Excel: {rows.Count}\n" +
                $"Mass instances created: {createdIds.Count}\n" +
                (skippedExistingCount > 0 ? $"Skipped, mass already exists: {skippedExistingCount}\n" : "") +
                (areaDefaultedCount > 0 ? $"Area missing or invalid, set to {DefaultAreaM2:0} m²: {areaDefaultedCount} room(s)\n" : "") +
                (SuppressedWarningCount > 0 ? $"{SuppressedWarningCount} Revit warning(s) suppressed - see log\n" : "") +
                (issueCount > 0 ? $"{issueCount} issues founded - see log\n" : "") +
                (logWriteError != null ? $"Log FAILED to write: {logWriteError}" : "");

            new Warning(DialogTitle, summaryMessage, credit,
                revealPath: showLogButton ? logPath : null).ShowDialog();

            return createdIds.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }

        // ---------------- Excel ----------------

        private static bool TryReadWorkbook(string path, out List<RoomRow> rows, out Dictionary<string, string> groupToColour, out string error)
        {
            rows = new List<RoomRow>();
            groupToColour = new Dictionary<string, string>();
            error = null;

            Dictionary<string, List<object[]>> sheets = ReadSheets(path);

            if (!sheets.TryGetValue("Input Table", out List<object[]> input) || input.Count == 0)
            {
                error = "The Excel file has no 'Input Table' tab.";
                return false;
            }
            if (!sheets.TryGetValue("Data", out List<object[]> data) || data.Count == 0)
            {
                error = "The Excel file has no 'Data' tab.";
                return false;
            }

            // ---- Data tab: every column left of "Colour Name" lists Area Groups for that colour ----
            object[] dataHeader = data[0];
            int colourCol = FindColumn(dataHeader, "colour name");
            if (colourCol < 0) colourCol = 2;
            for (int i = 1; i < data.Count; i++)
            {
                string colour = Normalise(CellText(data[i], colourCol));
                if (colour.Length == 0) continue;
                for (int c = 0; c < colourCol; c++)
                {
                    string group = Normalise(CellText(data[i], c));
                    if (group.Length == 0) continue;
                    if (groupToColour.TryGetValue(group, out string prior) && prior != colour)
                        DebugLog.Add($"[Data] Area Group '{group}' appears with more than one colour ('{prior}', '{colour}'); keeping '{prior}'");
                    else
                        groupToColour[group] = colour;
                }
            }

            // ---- Input Table ----
            object[] h = input[0];
            int cBuilding = FindColumn(h, "building name");
            int cLevel = FindColumn(h, "level");
            int cGroup = FindColumn(h, "area group");
            int cSpace = FindColumn(h, "space type");
            int cRoom = FindColumn(h, "room name");
            int cArea = FindColumn(h, "room required area");
            int cHeight = FindColumn(h, "room height");
            if (cGroup < 0 || cRoom < 0 || cArea < 0)
            {
                error = "The 'Input Table' tab is missing a required column header (Area Group, Room Name or Room Required Area).";
                return false;
            }

            for (int i = 1; i < input.Count; i++)
            {
                object[] line = input[i];
                RoomRow r = new RoomRow
                {
                    ExcelRow = i + 1,
                    Building = CellText(line, cBuilding).Trim(),
                    LevelText = CellText(line, cLevel).Trim(),
                    AreaGroupText = CellText(line, cGroup).Trim(),
                    SpaceType = CellText(line, cSpace).Trim(),
                    RoomName = CellText(line, cRoom).Trim(),
                    Area = CellNumber(line, cArea),
                    Height = CellNumber(line, cHeight)
                };

                bool blank = r.Building.Length == 0 && r.LevelText.Length == 0 && r.AreaGroupText.Length == 0 &&
                             r.SpaceType.Length == 0 && r.RoomName.Length == 0 && !r.Area.HasValue;
                if (blank) continue;
                rows.Add(r);
            }
            return true;
        }

        // Minimal .xlsx reader (no third-party DLL): an .xlsx is a zip of XML parts. Returns each sheet as
        // a list of rows (row index = Excel row - 1, column index = Excel column - 1). Numbers come back as
        // double, everything else as string. Merged cells and formatting are ignored; formulas give their
        // cached value.
        private static Dictionary<string, List<object[]>> ReadSheets(string path)
        {
            Dictionary<string, List<object[]>> result = new Dictionary<string, List<object[]>>(StringComparer.OrdinalIgnoreCase);

            using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                // Shared strings (optional part).
                List<string> shared = new List<string>();
                ZipArchiveEntry ssEntry = zip.GetEntry("xl/sharedStrings.xml");
                if (ssEntry != null)
                {
                    XDocument ss = LoadXml(ssEntry);
                    foreach (XElement si in ss.Root.Elements().Where(e => e.Name.LocalName == "si"))
                        shared.Add(string.Concat(si.Descendants()
                            .Where(e => e.Name.LocalName == "t" && e.Parent.Name.LocalName != "rPh")   // skip phonetic runs
                            .Select(e => e.Value)));
                }

                // Sheet name -> part path, via workbook.xml + its relationships.
                ZipArchiveEntry wbEntry = zip.GetEntry("xl/workbook.xml");
                ZipArchiveEntry relEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
                if (wbEntry == null || relEntry == null)
                    throw new InvalidDataException("This file is not a valid .xlsx workbook.");

                Dictionary<string, string> relTargets = new Dictionary<string, string>();
                foreach (XElement rel in LoadXml(relEntry).Root.Elements().Where(e => e.Name.LocalName == "Relationship"))
                    relTargets[(string)rel.Attribute("Id")] = (string)rel.Attribute("Target");

                foreach (XElement sheet in LoadXml(wbEntry).Descendants().Where(e => e.Name.LocalName == "sheet"))
                {
                    string name = ((string)sheet.Attribute("name") ?? "").Trim();
                    string rid = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
                    if (name.Length == 0 || rid == null || !relTargets.TryGetValue(rid, out string target)) continue;

                    string partPath = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                    ZipArchiveEntry sheetEntry = zip.GetEntry(partPath);
                    if (sheetEntry == null) continue;

                    result[name] = ReadSheetRows(LoadXml(sheetEntry), shared);
                }
            }
            return result;
        }

        private static XDocument LoadXml(ZipArchiveEntry entry)
        {
            using (Stream st = entry.Open())
                return XDocument.Load(st);
        }

        private static List<object[]> ReadSheetRows(XDocument sheetDoc, List<string> shared)
        {
            List<object[]> rows = new List<object[]>();
            XElement data = sheetDoc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
            if (data == null) return rows;

            int nextRow = 0;   // 0-based index of the next row expected
            foreach (XElement row in data.Elements().Where(e => e.Name.LocalName == "row"))
            {
                int rowIndex = int.TryParse((string)row.Attribute("r"), out int rn) ? rn - 1 : nextRow;
                while (rows.Count < rowIndex) rows.Add(new object[0]);   // keep Excel row numbering over gaps

                List<KeyValuePair<int, object>> cells = new List<KeyValuePair<int, object>>();
                int nextCol = 0;
                foreach (XElement c in row.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    int col = ColumnIndex((string)c.Attribute("r"), nextCol);
                    nextCol = col + 1;

                    string type = (string)c.Attribute("t");
                    XElement v = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
                    object value = null;

                    if (type == "inlineStr")
                    {
                        XElement isEl = c.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
                        value = isEl == null ? "" : string.Concat(isEl.Descendants()
                            .Where(e => e.Name.LocalName == "t" && e.Parent.Name.LocalName != "rPh").Select(e => e.Value));
                    }
                    else if (v != null)
                    {
                        if (type == "s")
                            value = int.TryParse(v.Value, out int si) && si >= 0 && si < shared.Count ? shared[si] : "";
                        else if (type == "str" || type == "e")
                            value = v.Value;
                        else if (type == "b")
                            value = v.Value == "1" ? "TRUE" : "FALSE";
                        else if (double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                            value = d;
                        else
                            value = v.Value;
                    }

                    if (value != null) cells.Add(new KeyValuePair<int, object>(col, value));
                }

                object[] line = new object[cells.Count == 0 ? 0 : cells.Max(k => k.Key) + 1];
                foreach (var kv in cells) line[kv.Key] = kv.Value;

                if (rowIndex < rows.Count) rows[rowIndex] = line; else rows.Add(line);
                nextRow = rowIndex + 1;
            }
            return rows;
        }

        // "B2" -> 1, "AA10" -> 26. Falls back to the next column when the reference is missing.
        private static int ColumnIndex(string cellRef, int fallback)
        {
            if (string.IsNullOrEmpty(cellRef)) return fallback;
            int col = 0, n = 0;
            foreach (char ch in cellRef)
            {
                if (ch >= 'A' && ch <= 'Z') { col = col * 26 + (ch - 'A' + 1); n++; }
                else if (ch >= 'a' && ch <= 'z') { col = col * 26 + (ch - 'a' + 1); n++; }
                else break;
            }
            return n == 0 ? fallback : col - 1;
        }

        // Header cells carry hints on later lines ("Level\n(leave blank...)"), so match on the first line.
        private static int FindColumn(object[] header, string startsWith)
        {
            for (int i = 0; i < header.Length; i++)
            {
                string text = CellText(header, i);
                string first = text.Split('\n')[0].Trim();
                if (first.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static string CellText(object[] row, int col)
        {
            if (col < 0 || col >= row.Length || row[col] == null) return "";
            object v = row[col];
            if (v is double d) return d.ToString("0.##########", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }

        private static double? CellNumber(object[] row, int col)
        {
            if (col < 0 || col >= row.Length || row[col] == null) return null;
            object v = row[col];
            if (v is double d) return d;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return x;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out x)) return x;
            return null;
        }

        // Trim, collapse inner whitespace, upper-case - so "STORAGE  / plant" matches "STORAGE / PLANT".
        private static string Normalise(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return string.Join(" ", s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        }

        private static string ExistingKey(string building, string level, string roomName)
            => $"{Normalise(building)}|{Normalise(level)}|{Normalise(roomName)}";

        // ---------------- Grid A-1 ----------------

        private static bool TryGetGridIntersection(Document doc, out XYZ point, out string error)
        {
            point = null;
            error = null;

            List<Grid> grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            Grid gA = grids.FirstOrDefault(g => string.Equals(g.Name.Trim(), "A", StringComparison.OrdinalIgnoreCase));
            Grid g1 = grids.FirstOrDefault(g => string.Equals(g.Name.Trim(), "1", StringComparison.OrdinalIgnoreCase));
            if (gA == null || g1 == null)
            {
                error = "Grid " + (gA == null && g1 == null ? "'A' and grid '1' were" : gA == null ? "'A' was" : "'1' was") +
                        " not found in this model, so the start point (A-1) can't be located.";
                return false;
            }

            Line a = gA.Curve as Line;
            Line b = g1.Curve as Line;
            if (a == null || b == null)
            {
                error = "Grid 'A' and grid '1' must both be straight grid lines.";
                return false;
            }

            // Plan intersection of the two infinite lines (Z ignored).
            XYZ p = a.GetEndPoint(0), r = a.Direction;
            XYZ q = b.GetEndPoint(0), s = b.Direction;
            double cross = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(cross) < 1e-9)
            {
                error = "Grid 'A' and grid '1' are parallel, so they have no intersection.";
                return false;
            }
            double tt = ((q.X - p.X) * s.Y - (q.Y - p.Y) * s.X) / cross;
            point = new XYZ(p.X + tt * r.X, p.Y + tt * r.Y, 0.0);
            return true;
        }

        // ---------------- Parameters ----------------

        private static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

        private static double ReadDouble(Element e, string name)
        {
            Parameter p = e.LookupParameter(name);
            return p != null && p.StorageType == StorageType.Double ? p.AsDouble() : 0.0;
        }

        private static string GetText(Element e, string name)
        {
            Parameter p = e.LookupParameter(name);
            return p != null && p.StorageType == StorageType.String ? p.AsString() ?? "" : "";
        }

        // Returns 1 on an issue.
        private static int SetDouble(Element inst, string name, double value, string label)
        {
            Parameter p = inst.LookupParameter(name);
            if (p == null) { DebugLog.Add($"[Parameter] {label}: instance parameter '{name}' not found"); return 1; }
            if (p.IsReadOnly) { DebugLog.Add($"[Parameter] {label}: '{name}' is read-only"); return 1; }
            if (p.StorageType != StorageType.Double) { DebugLog.Add($"[Parameter] {label}: '{name}' is not a number parameter"); return 1; }
            return p.Set(value) ? 0 : Fail(label, name);
        }

        private static int SetText(Element inst, string name, string value, string label)
        {
            Parameter p = inst.LookupParameter(name);
            if (p == null) { DebugLog.Add($"[Parameter] {label}: instance parameter '{name}' not found (if it is a type parameter it was not written)"); return 1; }
            if (p.IsReadOnly) { DebugLog.Add($"[Parameter] {label}: '{name}' is read-only"); return 1; }
            if (p.StorageType != StorageType.String) { DebugLog.Add($"[Parameter] {label}: '{name}' is not a text parameter"); return 1; }
            return p.Set(value ?? "") ? 0 : Fail(label, name);
        }

        // BA_Area Group is a TYPE parameter in the family (agreed with Lok): every row of a colour type
        // overwrites it, so the last row written wins. An instance parameter is used if one exists.
        private static int SetAreaGroup(FamilyInstance inst, string value, string label)
        {
            const string name = "BA_Area Group";
            if (inst.LookupParameter(name) != null)
                return SetText(inst, name, value, label);

            Parameter p = inst.Symbol?.LookupParameter(name);
            if (p == null) { DebugLog.Add($"[Parameter] {label}: '{name}' not found on instance or type"); return 1; }
            if (p.IsReadOnly || p.StorageType != StorageType.String) { DebugLog.Add($"[Parameter] {label}: type parameter '{name}' is read-only or not text"); return 1; }
            return p.Set(value ?? "") ? 0 : Fail(label, name);
        }

        private static int Fail(string label, string name)
        {
            DebugLog.Add($"[Parameter] {label}: could not set '{name}'");
            return 1;
        }

        // ---------------- Show Mass / 3D view ----------------

        // Document.MassDisplayTemporaryOverride is Revit's "Show Mass". The enum is resolved by name at
        // runtime so a different member name can't break the build; the choice is written to the log.
        private static void EnableShowMass(Document doc)
        {
            try
            {
                var prop = typeof(Document).GetProperty("MassDisplayTemporaryOverride");
                if (prop == null) { DebugLog.Add("[Warning] Show Mass: property not available in this Revit version"); return; }

                string[] names = Enum.GetNames(prop.PropertyType);
                string pick = names.FirstOrDefault(n => n == "MassFormAndFloors")
                              ?? names.FirstOrDefault(n => n.IndexOf("Mass", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                                           n.IndexOf("NoOverride", StringComparison.OrdinalIgnoreCase) < 0);
                if (pick == null) { DebugLog.Add($"[Warning] Show Mass: no usable value in ({string.Join(", ", names)})"); return; }

                prop.SetValue(doc, Enum.Parse(prop.PropertyType, pick));
                DebugLog.Add($"[Show Mass] MassDisplayTemporaryOverride = {pick}");
            }
            catch (Exception ex)
            {
                DebugLog.Add($"[Warning] Show Mass could not be switched on: {ex.Message}");
            }
        }

        private static void SwitchTo3D(UIDocument uidoc, Document doc)
        {
            View3D target = doc.ActiveView as View3D;
            if (target != null && !target.IsTemplate) return;   // already in a 3D view

            List<View3D> views3D = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate && !v.IsPerspective)
                .ToList();

            target = views3D.FirstOrDefault(v => v.Name.StartsWith("{3D", StringComparison.OrdinalIgnoreCase))
                     ?? views3D.FirstOrDefault();

            if (target == null)
            {
                ViewFamilyType vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                if (vft == null) { DebugLog.Add("[Warning] No 3D view type in the model; stayed in the current view"); return; }

                using (Transaction t = new Transaction(doc, "LW_Create 3D View"))
                {
                    t.Start();
                    target = View3D.CreateIsometric(doc, vft.Id);
                    t.Commit();
                }
                DebugLog.Add($"[View] No 3D view existed; created '{target.Name}'");
            }

            uidoc.ActiveView = target;
        }

        // ---------------- Warning swallower ----------------

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
