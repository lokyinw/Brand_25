using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Brand_25
{
    // Places a sorted set
    // of internal elevation views onto a sheet in left-to-right rows, wrapping to a new
    // row when the running width exceeds the configured Return Width, and creating an
    // additional sheet (duplicating the title block, incrementing sheet number/name,
    // copying BA_SheetSeries, setting BA_SRT_Level 01) when a row would run below the
    // configured Lower Margin.
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Elev_PlaceOnSheets : IExternalCommand
    {
        // 1 ft = 304.8 mm.
        private const double MmToFeet = 0.0032808;

        // A1 landscape paper height, and the title block's own top margins, in mm —
        // carried over from the original script. If your title block isn't A1, or its
        // margins differ, these three constants are the ones to adjust.
        private const double PaperHeightMm = 594.0;
        private const double TopMarginMm = 21.0;
        private const double TopMarginBMm = 15.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2026-07-14. Beta 0.10";

            StringBuilder log = new StringBuilder();
            List<string> issues = new List<string>();

            try
            {
                // Step 1: collect elevation view family types and project phases for the dialog
                List<ViewFamilyType> elevationTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .Where(vft => vft.ViewFamily == ViewFamily.Elevation)
                    .ToList();

                if (elevationTypes.Count == 0)
                {
                    TaskDialog.Show("Error", "No elevation view types found in the document.");
                    return Result.Cancelled;
                }

                List<Phase> phases = new FilteredElementCollector(doc)
                    .OfClass(typeof(Phase))
                    .Cast<Phase>()
                    .OrderBy(p => p.get_Parameter(BuiltInParameter.PHASE_SEQUENCE_NUMBER).AsInteger())
                    .ToList();

                if (phases.Count == 0)
                {
                    TaskDialog.Show("Error", "No phases found in the document.");
                    return Result.Cancelled;
                }

                // Step 2: gather user input
                Selection_SheetLayout inputWindow = new Selection_SheetLayout(elevationTypes, phases, credit);
                if (inputWindow.ShowDialog() != true)
                {
                    return Result.Cancelled;
                }

                ViewSheet startingSheet = FindSheetByNumber(doc, inputWindow.SheetNumber);
                if (startingSheet == null)
                {
                    new Warning("Oops...", $"No sheet found with number \"{inputWindow.SheetNumber}\".", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 3: collect matching elevation views (exact type + phase match,
                // rather than the original script's substring match on a parameter string —
                // this is more robust since it can't accidentally match an unrelated view
                // whose type/phase name happens to contain the same text).
                List<View> elevationViews = FindMatchingElevationViews(doc, inputWindow.SelectedViewFamilyType, inputWindow.SelectedPhase);
                if (elevationViews.Count == 0)
                {
                    new Warning("Oops...", "No elevation views found matching the selected type and phase.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 4: sort in a logical (natural) order — so "...- 9" sorts before
                // "...- 10", following the room number at the start of each view's name —
                // rather than the original script's sort, which was a leftover from a
                // different naming convention.
                elevationViews = elevationViews.OrderBy(v => NaturalSortKey(v.Name), StringComparer.Ordinal).ToList();
                log.AppendLine($"Found {elevationViews.Count} matching elevation view(s):");
                foreach (View v in elevationViews) log.AppendLine($"  {v.Name}");

                // Step 4.5: match each view back to the Room it was built from (by the
                // "{Number} {Name} - " naming convention Room_CreateIntElev uses), so we
                // can use that room's actual Level directly — rather than guessing at
                // "whichever level happens to be visible in this view". Views that don't
                // match any room (i.e. weren't created by Room_CreateIntElev) are skipped
                // entirely rather than placed on the sheet.
                List<Room> allRooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .ToList();

                List<(View View, Room Room)> matchedViews = new List<(View, Room)>();
                foreach (View v in elevationViews)
                {
                    Room matchedRoom = allRooms.FirstOrDefault(r =>
                    {
                        Parameter nameParam = r.get_Parameter(BuiltInParameter.ROOM_NAME);
                        string roomName = nameParam != null && nameParam.HasValue ? nameParam.AsString() : "Unknown";

                        return v.Name.StartsWith($"{r.Number} {roomName} - ");
                    });

                    //Room matchedRoom = allRooms.FirstOrDefault(r => v.Name.StartsWith($"{r.Number} {r.Name} - "));
                    if (matchedRoom == null)
                    {
                        string issue = $"View '{v.Name}': doesn't match any room's naming pattern (not created by Room_CreateIntElev?) — skipped.";
                        log.AppendLine($"Warning: {issue}");
                        issues.Add(issue);
                        continue;
                    }
                    matchedViews.Add((v, matchedRoom));
                }

                if (matchedViews.Count == 0)
                {
                    new Warning("Oops...", "None of the matching elevation views could be matched back to a room. See the log for details.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Collapse back into parallel lists (same order), so the rest of the
                // pipeline below — which indexes into elevationViews — needs minimal change.
                elevationViews = matchedViews.Select(m => m.View).ToList();
                List<Room> matchedRooms = matchedViews.Select(m => m.Room).ToList();

                // Step 5: measurement pass. Each view is temporarily placed on the starting
                // sheet purely to read its rendered viewport size and its "dist" (the
                // vertical offset needed to align that view's Level line consistently
                // across all viewports), then removed. This can't be computed from the
                // crop box alone since it depends on how Revit actually renders the
                // viewport's content at that view's scale.
                List<double> widths = new List<double>();
                List<double> heights = new List<double>();
                List<double> dists = new List<double>();

                using (Transaction measureTrans = new Transaction(doc, "LW_Measure Elevation Viewports"))
                {
                    measureTrans.Start();

                    for (int i = 0; i < elevationViews.Count; i++)
                    {
                        View v = elevationViews[i];
                        Room room = matchedRooms[i];

                        Viewport vp = Viewport.Create(doc, startingSheet.Id, v.Id, XYZ.Zero);
                        if (vp == null)
                        {
                            string issue = $"View '{v.Name}': failed to create a measurement viewport.";
                            log.AppendLine($"Error: {issue}");
                            issues.Add(issue);
                            widths.Add(0);
                            heights.Add(0);
                            dists.Add(0);
                            continue;
                        }

                        Outline outline = vp.GetBoxOutline();
                        widths.Add(outline.MaximumPoint.X);
                        heights.Add(outline.MaximumPoint.Y);

                        double dist = 0;
                        bool foundLevel = false;
                        try
                        {
                            // View.CropBox is in the view's own LOCAL coordinate system,
                            // where Y is vertical
                            BoundingBoxXYZ cropBox = v.CropBox;
                            double cropCenterY = (cropBox.Min.Y + cropBox.Max.Y) / 2.0;

                            // Use the room's OWN level directly
                            Level roomLevel = doc.GetElement(room.LevelId) as Level;
                            if (roomLevel != null)
                            {
                                IList<Curve> curves = roomLevel.GetCurvesInView(DatumExtentType.Model, v);
                                if (curves != null && curves.Count > 0)
                                {
                                    double levelZ = curves[0].GetEndPoint(0).Z;
                                    dist = (cropCenterY - levelZ) / v.Scale;
                                    foundLevel = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            log.AppendLine($"Error measuring level offset for view '{v.Name}': {ex.Message}");
                        }

                        if (!foundLevel)
                        {
                            string issue = $"View '{v.Name}': could not determine its room's level line for alignment; using dist=0.";
                            log.AppendLine($"Warning: {issue}");
                            issues.Add(issue);
                        }
                        dists.Add(dist);

                        doc.Delete(vp.Id);
                    }

                    measureTrans.Commit();
                }

                // Step 6: layout algorithm — pack views into rows left-to-right, wrapping
                // to a new row when the running width would exceed Return Width, and
                // marking a new sheet when a row would fall below Lower Margin.
                LayoutResult layout = ComputeLayout(
                    widths, heights, dists,
                    inputWindow.LeftMarginMm * MmToFeet,
                    inputWindow.XSpacingMm * MmToFeet,
                    inputWindow.YSpacingMm * MmToFeet,
                    inputWindow.ReturnWidthMm * MmToFeet,
                    inputWindow.LowerMarginMm * MmToFeet);

                // Step 7: create any additional sheets the layout needs.
                List<ViewSheet> sheetList = new List<ViewSheet> { startingSheet };
                using (Transaction sheetTrans = new Transaction(doc, "LW_Create Additional Sheets"))
                {
                    sheetTrans.Start();

                    // Check if the starting sheet name needs the " 01" suffix applied
                    string currentName = startingSheet.get_Parameter(BuiltInParameter.SHEET_NAME).AsString();
                    if (!TryIncrementLastDigit(currentName, out _))
                    {
                        startingSheet.get_Parameter(BuiltInParameter.SHEET_NAME).Set($"{currentName} 1");
                        log.AppendLine($"Renamed initial sheet to: {startingSheet.SheetNumber} - {currentName} 1");
                    }

                    ViewSheet current = startingSheet;
                    for (int i = 1; i <= layout.MaxSheetIndex; i++)
                    {
                        ViewSheet nextSheet = CreateNextSheet(doc, current, log, issues);
                        if (nextSheet == null)
                        {
                            string issue = "Failed to create an additional sheet; remaining views will be skipped.";
                            log.AppendLine($"Error: {issue}");
                            issues.Add(issue);
                            break;
                        }
                        sheetList.Add(nextSheet);
                        current = nextSheet;
                    }

                    sheetTrans.Commit();
                }

                // Step 8: place the real viewports at their computed positions.
                int placedCount = 0;
                List<int> placedIndices = new List<int>();
                using (Transaction placeTrans = new Transaction(doc, "LW_Place Elevations on Sheet"))
                {
                    placeTrans.Start();

                    for (int i = 0; i < elevationViews.Count; i++)
                    {
                        int sheetIndex = layout.PlaceSheetIndex[i];
                        if (sheetIndex >= sheetList.Count)
                        {
                            string issue = $"View '{elevationViews[i].Name}': target sheet could not be created; skipped.";
                            log.AppendLine($"Error: {issue}");
                            issues.Add(issue);
                            continue;
                        }

                        Viewport vp = Viewport.Create(doc, sheetList[sheetIndex].Id, elevationViews[i].Id, XYZ.Zero);
                        if (vp == null)
                        {
                            string issue = $"View '{elevationViews[i].Name}': failed to place on sheet.";
                            log.AppendLine($"Error: {issue}");
                            issues.Add(issue);
                            continue;
                        }

                        vp.SetBoxCenter(new XYZ(layout.PlaceX[i], layout.PlaceY[i], 0));
                        placedCount++;
                        placedIndices.Add(i);
                    }

                    placeTrans.Commit();
                }

                // Step 9: for every successfully placed view, set "Title on Sheet" from its
                // room, and clean up level line display — hide bubbles on every level
                // visible in the view (there can be more than one for an atrium or a room
                // adjacent to a split level), shorten each line, and, only for the view
                // that ends its row, retain the bubble on the right so the row still
                // visually indicates which floor it belongs to.
                using (Transaction titleTrans = new Transaction(doc, "LW_Set View Titles and Level Display"))
                {
                    titleTrans.Start();

                    foreach (int i in placedIndices)
                    {
                        View v = elevationViews[i];
                        Room room = matchedRooms[i];
                        bool isRowEnd = layout.IsRowEnd[i];

                        Parameter titleParam = v.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                        titleParam?.Set($"{room.Number} {room.Name}");

                        UpdateLevelDisplay(doc, v, isRowEnd, inputWindow.LevelLineScale, log, issues);
                    }

                    titleTrans.Commit();
                }

                // Show summary
                string summary = $"{placedCount} of {elevationViews.Count} elevation(s) placed across {sheetList.Count} sheet(s).";
                if (issues.Count > 0)
                {
                    summary += $"\n\n{issues.Count} issue(s) were encountered — see the log for details.";
                }
                new Warning("Success", summary, credit).ShowDialog();
                log.AppendLine();
                log.AppendLine(summary);
                if (issues.Count > 0)
                {
                    log.AppendLine();
                    log.AppendLine($"=== {issues.Count} Issue(s) ===");
                    foreach (string issue in issues) log.AppendLine(issue);
                }

                string logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ElevPlaceOnSheetsLog.txt");
                try
                {
                    File.WriteAllText(logFilePath, log.ToString());
                }
                catch
                {
                    // Non-critical — don't fail the whole command over a log file write issue.
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

        // Hides the bubble at both ends of every level visible in the view, shortens each
        // level's line, and — only when this view ends its row — leaves the RIGHT-hand
        // bubble visible, so the row still visually indicates which floor it belongs to.
        // Translated from a Dynamo Python node the team already relies on; the trim
        // formula (scale / view.Scale) is kept exactly as-is rather than reinterpreted.
        private void UpdateLevelDisplay(Document doc, View view, bool isRowEnd, double lineScale, StringBuilder log, List<string> issues)
        {
            List<Level> levelsInView = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Levels)
                .Cast<Level>()
                .ToList();

            if (levelsInView.Count == 0) return;

            // Used to work out which end of each level's line is on the RIGHT in this
            // specific view, so the row-end retained bubble is on the correct side
            // regardless of the view's rotation. Not part of the original script — worth
            // confirming visually the first time this runs.
            Transform transform = view.CropBox.Transform;
            double s = view.Scale;

            foreach (Level level in levelsInView)
            {
                try
                {
                    IList<Curve> curves = level.GetCurvesInView(DatumExtentType.ViewSpecific, view);
                    if (curves == null || curves.Count == 0) continue;

                    Curve curve = curves[0];
                    XYZ ep0 = curve.GetEndPoint(0);
                    XYZ ep1 = curve.GetEndPoint(1);

                    double localX0 = transform.Inverse.OfPoint(ep0).X;
                    double localX1 = transform.Inverse.OfPoint(ep1).X;
                    bool end0IsRight = localX0 > localX1;
                    DatumEnds rightEnd = end0IsRight ? DatumEnds.End0 : DatumEnds.End1;
                    DatumEnds leftEnd = end0IsRight ? DatumEnds.End1 : DatumEnds.End0;

                    if (isRowEnd)
                    {
                        // Keep the right-hand bubble to mark this row's floor; hide the left.
                        if (level.IsBubbleVisibleInView(leftEnd, view)) level.HideBubbleInView(leftEnd, view);
                    }
                    else
                    {
                        if (level.IsBubbleVisibleInView(DatumEnds.End0, view)) level.HideBubbleInView(DatumEnds.End0, view);
                        if (level.IsBubbleVisibleInView(DatumEnds.End1, view)) level.HideBubbleInView(DatumEnds.End1, view);
                    }

                    // Shorten the line from each end — same technique as the original
                    // script: offset along the curve's own direction by (scale / view.Scale).
                    XYZ direction = (curve as Line).Direction;
                    XYZ newEp0 = new XYZ(ep0.X + direction.X * lineScale / s, ep0.Y + direction.Y * lineScale / s, ep0.Z + direction.Z * lineScale / s);
                    XYZ newEp1 = new XYZ(ep1.X - direction.X * lineScale / s, ep1.Y - direction.Y * lineScale / s, ep1.Z - direction.Z * lineScale / s);
                    Curve newCurve = Line.CreateBound(newEp0, newEp1);
                    level.SetCurveInView(DatumExtentType.ViewSpecific, view, newCurve);
                }
                catch (Exception ex)
                {
                    string issue = $"View '{view.Name}': failed to adjust level '{level.Name}' display: {ex.Message}";
                    log.AppendLine($"Warning: {issue}");
                    issues.Add(issue);
                }
            }
        }

        private ViewSheet FindSheetByNumber(Document doc, string number)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Sheets)
                .WhereElementIsNotElementType()
                .Cast<ViewSheet>()
                .FirstOrDefault(s => s.get_Parameter(BuiltInParameter.SHEET_NUMBER).AsString() == number);
        }

        private List<View> FindMatchingElevationViews(Document doc, ViewFamilyType elevationType, Phase phase)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.ViewType == ViewType.Elevation
                            && !v.IsTemplate
                            && v.GetTypeId() == elevationType.Id
                            && v.get_Parameter(BuiltInParameter.VIEW_PHASE) != null
                            && v.get_Parameter(BuiltInParameter.VIEW_PHASE).AsElementId() == phase.Id)
                .ToList();
        }

        // Splits a name into digit / non-digit runs and pads digit runs to a fixed width,
        // so an ordinary string sort on the result produces natural numeric ordering
        // (e.g. "Room 9" sorts before "Room 10"). This replaces the original script's
        // sort, which assumed a different, no-longer-used naming convention.
        private string NaturalSortKey(string name)
        {
            return Regex.Replace(name, "[0-9]+", match => match.Value.PadLeft(10, '0'));
        }

        private class LayoutResult
        {
            public List<double> PlaceX = new List<double>();
            public List<double> PlaceY = new List<double>();
            public List<int> PlaceSheetIndex = new List<int>();
            public List<bool> IsRowEnd = new List<bool>();
            public int MaxSheetIndex;
        }

        // Faithful translation of the original "Place Views on Sheet" packing algorithm:
        // views are added left-to-right; when the next view wouldn't fit within
        // returnWidthFt, the current row is finalized (vertically aligned so every view's
        // Level line lines up) and a new row starts. If a row's aligned position would
        // fall below lowerMarginFt from the bottom of the sheet, a new sheet is used
        // instead and the row starts again from the top.
        private LayoutResult ComputeLayout(List<double> widths, List<double> heights, List<double> dists,
            double leftMarginFt, double xSpacingFt, double ySpacingFt, double returnWidthFt, double lowerMarginFt)
        {
            double placeY = (PaperHeightMm - TopMarginMm - TopMarginBMm) * MmToFeet;
            double placeX = leftMarginFt - xSpacingFt;
            double topOfSheetY = (PaperHeightMm - TopMarginMm - TopMarginBMm) * MmToFeet;

            double prevWidth = 0;
            List<double> rowHeights = new List<double>();
            List<double> rowDists = new List<double>();
            int sheetIndex = 0;

            LayoutResult result = new LayoutResult();
            // Pre-size the output lists so we can assign by index in any order, matching
            // the original script's index bookkeeping.
            for (int i = 0; i < widths.Count; i++)
            {
                result.PlaceX.Add(0);
                result.PlaceY.Add(0);
                result.PlaceSheetIndex.Add(0);
                result.IsRowEnd.Add(false);
            }

            void FinalizeRow(int lastIndexInRow, int rowStartIndex)
            {
                double alignY = placeY - rowHeights.Max() - rowDists.Max();

                if (alignY < lowerMarginFt)
                {
                    sheetIndex++;
                    placeY = topOfSheetY;
                    alignY = placeY - rowHeights.Max() - rowDists.Max();
                }

                for (int i = 0; i < rowHeights.Count; i++)
                {
                    int index = rowStartIndex + i;
                    double y = alignY + dists[index];
                    result.PlaceY[index] = y;
                    result.PlaceSheetIndex[index] = sheetIndex;
                }

                result.IsRowEnd[rowStartIndex + rowHeights.Count - 1] = true;

                //int lastIndex = rowStartIndex + rowHeights.Count - 1;
                //placeY = result.PlaceY[lastIndex];
            }

            int currentRowStart = 0;

            for (int ind = 0; ind < widths.Count; ind++)
            {
                double vX = widths[ind];

                if (placeX + prevWidth + xSpacingFt + vX + vX < returnWidthFt)
                {
                    // Fits in the current row.
                    placeX = placeX + prevWidth + xSpacingFt + vX;
                    result.PlaceX[ind] = placeX;
                    prevWidth = vX;
                    rowHeights.Add(heights[ind]);
                    rowDists.Add(dists[ind]);

                    if (ind == widths.Count - 1)
                    {
                        FinalizeRow(ind, currentRowStart);
                    }
                }
                else
                {
                    // Doesn't fit — finalize the row accumulated so far...
                    FinalizeRow(ind - 1, currentRowStart);
                    placeY = (result.PlaceY[ind - 1]) - dists[ind - 1] - ySpacingFt; // set placeY for next row, relative to the row just finalized

                    // ...then start a new row with the current view.
                    rowHeights.Clear();
                    rowDists.Clear();
                    currentRowStart = ind;

                    prevWidth = vX;
                    placeX = leftMarginFt + vX;
                    result.PlaceX[ind] = placeX;
                    rowHeights.Add(heights[ind]);
                    rowDists.Add(dists[ind]);

                    if (ind == widths.Count - 1)
                    {
                        FinalizeRow(ind, currentRowStart);
                    }
                }
            }

            result.MaxSheetIndex = sheetIndex;
            return result;
        }

        // Creates the next sheet in sequence by duplicating the reference sheet's title
        // block, incrementing the sheet number and name's final digit (matching the
        // original script; per team confirmation, a project won't need more than 9
        // elevation sheets, so a single-digit increment is sufficient), and copying over
        // the BA_SheetSeries shared parameter and BA_SRT_Level 01 value.
        private ViewSheet CreateNextSheet(Document doc, ViewSheet referenceSheet, StringBuilder log, List<string> issues)
        {
            string currentNumber = referenceSheet.get_Parameter(BuiltInParameter.SHEET_NUMBER).AsString();
            string currentName = referenceSheet.get_Parameter(BuiltInParameter.SHEET_NAME).AsString();

            if (!TryIncrementLastDigit(currentNumber, out string nextNumber))
            {
                string issue = $"Sheet '{currentNumber}': number doesn't end in a digit; can't auto-increment.";
                log.AppendLine($"Error: {issue}");
                issues.Add(issue);
                return null;
            }

            // This will now successfully increment "xxx 01" to "xxx 02", "xxx 03", etc.
            if (!TryIncrementLastDigit(currentName, out string nextName))
            {
                nextName = $"{currentName} 1";
            }

            Parameter sheetSeriesParam = referenceSheet.LookupParameter("BA_SheetSeries");
            string sheetSeriesValue = sheetSeriesParam?.AsString();

            FamilyInstance titleBlock = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .FirstOrDefault(tb => tb.get_Parameter(BuiltInParameter.SHEET_NUMBER)?.AsString() == currentNumber);

            if (titleBlock == null)
            {
                string issue = $"Sheet '{currentNumber}': couldn't find its title block instance.";
                log.AppendLine($"Error: {issue}");
                issues.Add(issue);
                return null;
            }

            ViewSheet newSheet = ViewSheet.Create(doc, titleBlock.GetTypeId());
            newSheet.get_Parameter(BuiltInParameter.SHEET_NUMBER).Set(nextNumber);
            newSheet.get_Parameter(BuiltInParameter.SHEET_NAME).Set(nextName);

            if (sheetSeriesValue != null)
            {
                newSheet.LookupParameter("BA_SheetSeries")?.Set(sheetSeriesValue);
            }
            newSheet.LookupParameter("BA_SRT_Level 01")?.Set("100 Building");

            log.AppendLine($"Created additional sheet: {nextNumber} - {nextName}");
            return newSheet;
        }

        private bool TryIncrementLastDigit(string value, out string result)
        {
            result = value;
            if (string.IsNullOrEmpty(value) || !char.IsDigit(value[value.Length - 1]))
            {
                return false;
            }

            int lastDigit = value[value.Length - 1] - '0';
            result = value.Substring(0, value.Length - 1) + (lastDigit + 1);
            return true;
        }
    }
}
