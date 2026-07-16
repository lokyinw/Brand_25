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
    // Places a sorted set of internal elevation views onto a sheet in left-to-right
    // rows, wrapping to a new row when the running width exceeds the content area's
    // right edge, and creating an additional sheet (duplicating the title block,
    // incrementing sheet number/name, copying BA_SheetSeries, setting BA_SRT_Level 01)
    // when a row would run below the content area's bottom edge.
    //
    // Unit convention used throughout this file (matches Selection_SheetLayout):
    //   - Variables suffixed "Mm" are raw millimeters, straight from the dialog.
    //   - Variables suffixed "Ft" are paper-space lengths in Revit's internal feet,
    //     i.e. an "Mm" value run through MmToFeet.
    //   - Variables with NO suffix are model-space lengths, already in Revit's
    //     internal feet exactly as the API returns them (e.g. CropBox, level
    //     geometry) — no conversion applied.
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Elev_PlaceOnSheets : IExternalCommand
    {
        // 1 ft = 304.8 mm.
        private const double MmToFeet = 0.0032808;
        private const double FeetToMm = 304.8;

        // --- Empirical compensation constants ------------------------------------
        // Viewport.SetBoxCenter() positions the crop+title envelope, not the crop
        // itself, and the exact envelope-to-crop offset has proven inconsistent to
        // pin down exactly (it didn't match a single clean formula in testing — some
        // other factor we haven't isolated yet is contributing). Rather than chase
        // the exact mechanism further, these two constants are plain fudge factors,
        // tuned from live test measurements, applied on top of the dialog's own
        // margin/spacing inputs. If a future test shows drift, adjust these numbers
        // directly — no other code should need to change.
        //
        //   TopMarginCompensationMm: measured top margin ran ~53.5mm vs. an intended
        //   15mm (ContentMarginTopMm) — i.e. about 38.5mm too generous. Added to the
        //   top-of-sheet reference so the placed crop sits higher (smaller margin).
        private const double TopMarginCompensationMm = 38.5;

        //   YSpacingCompensationMm: measured row-to-row gap ran ~60-68mm against a
        //   YSpacingMm of 27 — roughly 33-41mm too generous, averaging ~37mm.
        //   Subtracted from YSpacingMm before use, so entering 27 lands closer to an
        //   actual 27mm gap. Because the real offset varies row to row (~8mm swing
        //   observed), this won't be exact every time — it's a "close enough"
        //   correction, not a precise one.
        private const double YSpacingCompensationMm = -37.0;
        // ---------------------------------------------------------------------------

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2026-07-16. Beta 0.11";

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

                // Derive the content area's four edges (paper space, feet) from paper
                // size + frame margin (paper edge -> title frame) + content margin
                // (title frame -> drawing area). ContentMarginRightMm doubles as the
                // "Drawing Information Area" width, since that's what actually bounds
                // the drawing area on the right for this title block. Changing to a
                // different title block or paper size only ever means changing these
                // eight dialog inputs — nothing below this point needs to change.
                double leftEdgeFt = (inputWindow.FrameMarginLeftMm + inputWindow.ContentMarginLeftMm) * MmToFeet;
                double rightEdgeFt = (inputWindow.PaperWidthMm - inputWindow.FrameMarginRightMm - inputWindow.ContentMarginRightMm) * MmToFeet;
                double topOfSheetFt = (inputWindow.PaperHeightMm - inputWindow.FrameMarginTopMm - inputWindow.ContentMarginTopMm + TopMarginCompensationMm) * MmToFeet;
                double lowerEdgeFt = (inputWindow.FrameMarginBottomMm + inputWindow.ContentMarginBottomMm) * MmToFeet;

                double xSpacingFt = inputWindow.XSpacingMm * MmToFeet;
                double ySpacingFt = (inputWindow.YSpacingMm + YSpacingCompensationMm) * MmToFeet;

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

                // Step 5: measurement pass. Reads each view's crop box (paper space, via
                // CropBox / view.Scale) and its Room's Level line position. This is a
                // read-only pass over the View/Level/Room API — no Viewport instance or
                // transaction is needed. The gap between this and what actually lands on
                // paper is handled by the flat TopMarginCompensationMm / YSpacingCompensationMm
                // constants above, rather than by measuring per-view here.
                List<double> widthsFt = new List<double>();
                List<double> heightsFt = new List<double>();
                List<double> distsFt = new List<double>();

                log.AppendLine();
                log.AppendLine("=== Measurement Data (tab-separated; paste into Excel) ===");
                log.AppendLine(string.Join("\t",
                    "ViewName", "RoomNumber", "ViewScale",
                    "CropMinX_model_mm", "CropMaxX_model_mm", "CropMinY_model_mm", "CropMaxY_model_mm",
                    "CropWidth_mm", "CropHeight_mm",
                    "LevelName", "LevelZ_model_mm", "CropCenterY_model_mm", "Dist_mm"));

                for (int i = 0; i < elevationViews.Count; i++)
                {
                    View v = elevationViews[i];
                    Room room = matchedRooms[i];

                    BoundingBoxXYZ cropBox = v.CropBox;
                    double cropWidth = cropBox.Max.X - cropBox.Min.X;   // model space
                    double cropHeight = cropBox.Max.Y - cropBox.Min.Y;  // model space
                    double viewScale = v.Scale;

                    widthsFt.Add(cropWidth / viewScale);
                    heightsFt.Add(cropHeight / viewScale);

                    double dist = 0;
                    bool foundLevel = false;
                    double cropCenterY = (cropBox.Min.Y + cropBox.Max.Y) / 2.0;
                    Level roomLevel = null;
                    double levelZ = 0;
                    try
                    {
                        // View.CropBox is in the view's own LOCAL coordinate system,
                        // where Y is vertical.

                        // Use the room's OWN level directly, rather than "whichever
                        // level happens to be visible in this view first".
                        roomLevel = doc.GetElement(room.LevelId) as Level;
                        if (roomLevel != null)
                        {
                            IList<Curve> curves = roomLevel.GetCurvesInView(DatumExtentType.Model, v);
                            if (curves != null && curves.Count > 0)
                            {
                                levelZ = curves[0].GetEndPoint(0).Z;
                                dist = (cropCenterY - levelZ) / viewScale;
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
                    distsFt.Add(dist);

                    log.AppendLine(string.Join("\t",
                        v.Name, room.Number, viewScale.ToString(),
                        (cropBox.Min.X * FeetToMm).ToString("F3"), (cropBox.Max.X * FeetToMm).ToString("F3"),
                        (cropBox.Min.Y * FeetToMm).ToString("F3"), (cropBox.Max.Y * FeetToMm).ToString("F3"),
                        (cropWidth / viewScale * FeetToMm).ToString("F3"), (cropHeight / viewScale * FeetToMm).ToString("F3"),
                        roomLevel?.Name ?? "N/A", (levelZ * FeetToMm).ToString("F3"),
                        (cropCenterY * FeetToMm).ToString("F3"), (dist * FeetToMm).ToString("F3")));
                }

                // Step 6: layout algorithm — pack views into rows left-to-right, wrapping
                // to a new row when the running width would exceed the content area's
                // right edge, and marking a new sheet when a row would fall below the
                // content area's bottom edge.
                LayoutResult layout = ComputeLayout(
                    widthsFt, heightsFt, distsFt,
                    leftEdgeFt, xSpacingFt, ySpacingFt, rightEdgeFt, topOfSheetFt, lowerEdgeFt);

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

                log.AppendLine();
                log.AppendLine("=== Placement Data (tab-separated; paste into Excel) ===");
                log.AppendLine(string.Join("\t",
                    "ViewName", "SheetIndex", "SheetNumber", "IsRowEnd",
                    "TargetX_mm", "TargetY_mm",
                    "EnvelopeMinX_mm", "EnvelopeMaxX_mm", "EnvelopeMinY_mm", "EnvelopeMaxY_mm",
                    "LabelMinX_mm", "LabelMaxX_mm", "LabelMinY_mm", "LabelMaxY_mm"));

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

                        // Log the ACTUAL resulting geometry, post-placement, in sheet
                        // (paper) space — this is the ground truth to compare against
                        // physical sheet measurements.
                        Outline placedEnvelope = vp.GetBoxOutline();
                        Outline placedLabel = vp.GetLabelOutline();
                        log.AppendLine(string.Join("\t",
                            elevationViews[i].Name, sheetIndex.ToString(), sheetList[sheetIndex].SheetNumber, layout.IsRowEnd[i].ToString(),
                            (layout.PlaceX[i] * FeetToMm).ToString("F3"), (layout.PlaceY[i] * FeetToMm).ToString("F3"),
                            (placedEnvelope.MinimumPoint.X * FeetToMm).ToString("F3"), (placedEnvelope.MaximumPoint.X * FeetToMm).ToString("F3"),
                            (placedEnvelope.MinimumPoint.Y * FeetToMm).ToString("F3"), (placedEnvelope.MaximumPoint.Y * FeetToMm).ToString("F3"),
                            placedLabel != null ? (placedLabel.MinimumPoint.X * FeetToMm).ToString("F3") : "N/A",
                            placedLabel != null ? (placedLabel.MaximumPoint.X * FeetToMm).ToString("F3") : "N/A",
                            placedLabel != null ? (placedLabel.MinimumPoint.Y * FeetToMm).ToString("F3") : "N/A",
                            placedLabel != null ? (placedLabel.MaximumPoint.Y * FeetToMm).ToString("F3") : "N/A"));
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

                        UpdateLevelDisplay(doc, v, isRowEnd, inputWindow.LevelLineTrim, log, issues);
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
        // formula (lineScale / view.Scale) is kept exactly as-is rather than
        // reinterpreted — lineScale is a model-space length (no suffix), consumed
        // directly here without unit conversion, same as the original script.
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

        // Packs views into rows left-to-right using an EXPLICIT half-width formula
        // (half of the current view + spacing + half of the next view), so
        // xSpacingFt has its literal meaning: the paper-space gap between two
        // viewports' crop edges. A row wraps to a new one when the next view's
        // right edge would cross rightEdgeFt. Once a row is complete, it's aligned
        // so every view's Level line lines up (using each view's own dist); if that
        // aligned position would fall below lowerEdgeFt, the row instead starts a
        // new sheet from topOfSheetFt.
        private LayoutResult ComputeLayout(List<double> widthsFt, List<double> heightsFt, List<double> distsFt,
            double leftEdgeFt, double xSpacingFt, double ySpacingFt, double rightEdgeFt, double topOfSheetFt, double lowerEdgeFt)
        {
            LayoutResult result = new LayoutResult();
            for (int i = 0; i < widthsFt.Count; i++)
            {
                result.PlaceX.Add(0);
                result.PlaceY.Add(0);
                result.PlaceSheetIndex.Add(0);
                result.IsRowEnd.Add(false);
            }

            double placeX = leftEdgeFt;
            double placeY = topOfSheetFt;
            double prevHalfWidth = 0;
            bool rowHasItems = false;
            int sheetIndex = 0;
            int rowStart = 0;

            List<double> rowHeights = new List<double>();
            List<double> rowDists = new List<double>();

            void FinalizeRow(int rowStartIndex, int rowEndIndex)
            {
                double alignY = placeY - rowHeights.Max() - rowDists.Max();

                if (alignY < lowerEdgeFt)
                {
                    sheetIndex++;
                    placeY = topOfSheetFt;
                    alignY = placeY - rowHeights.Max() - rowDists.Max();
                }

                for (int i = rowStartIndex; i <= rowEndIndex; i++)
                {
                    result.PlaceY[i] = alignY + distsFt[i];
                    result.PlaceSheetIndex[i] = sheetIndex;
                }
                result.IsRowEnd[rowEndIndex] = true;

                // Next row starts below whichever view in this row extends furthest down.
                placeY = result.PlaceY[rowEndIndex] - distsFt[rowEndIndex] - ySpacingFt;

                rowHeights.Clear();
                rowDists.Clear();
            }

            for (int i = 0; i < widthsFt.Count; i++)
            {
                double halfWidth = widthsFt[i] / 2.0;

                placeX = rowHasItems
                    ? placeX + prevHalfWidth + xSpacingFt + halfWidth  // half-current + gap + half-next
                    : leftEdgeFt + halfWidth;                          // first item in row: flush to left edge

                if (rowHasItems && placeX + halfWidth > rightEdgeFt)
                {
                    // Doesn't fit — finalize the row accumulated so far, then start a
                    // new row with the current view.
                    FinalizeRow(rowStart, i - 1);
                    rowStart = i;
                    rowHasItems = false;
                    placeX = leftEdgeFt + halfWidth;
                }

                result.PlaceX[i] = placeX;
                rowHeights.Add(heightsFt[i]);
                rowDists.Add(distsFt[i]);
                prevHalfWidth = halfWidth;
                rowHasItems = true;

                if (i == widthsFt.Count - 1)
                {
                    FinalizeRow(rowStart, i);
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
