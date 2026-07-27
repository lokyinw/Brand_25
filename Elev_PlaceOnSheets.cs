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
        // 1 ft = 304.8 mm. Aliased from ElevationSheetLayoutHelper so the rest of this
        // file's bare "MmToFeet"/"FeetToMm" references don't all need qualifying —
        // the values themselves live in exactly one place (the helper).
        private const double MmToFeet = ElevationSheetLayoutHelper.MmToFeet;
        private const double FeetToMm = ElevationSheetLayoutHelper.FeetToMm;

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

                ViewSheet startingSheet = ElevationSheetLayoutHelper.FindSheetByNumber(doc, inputWindow.SheetNumber);
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
                (double leftEdgeFt, double rightEdgeFt, double topOfSheetFt, double lowerEdgeFt, double xSpacingFt, double ySpacingFt) =
                    ElevationSheetLayoutHelper.ComputeContentEdges(inputWindow);

                // Step 3: collect matching elevation views (exact type + phase match,
                // rather than the original script's substring match on a parameter string —
                // this is more robust since it can't accidentally match an unrelated view
                // whose type/phase name happens to contain the same text).
                List<View> elevationViews = ElevationSheetLayoutHelper.FindMatchingElevationViews(doc, inputWindow.SelectedViewFamilyType, inputWindow.SelectedPhase);
                if (elevationViews.Count == 0)
                {
                    new Warning("Oops...", "No elevation views found matching the selected type and phase.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 4: sort in a logical (natural) order — so "...- 9" sorts before
                // "...- 10", following the room number at the start of each view's name —
                // rather than the original script's sort, which was a leftover from a
                // different naming convention.
                elevationViews = elevationViews.OrderBy(v => ElevationSheetLayoutHelper.NaturalSortKey(v.Name), StringComparer.Ordinal).ToList();
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
                // paper is handled by the flat compensation constants inside
                // ElevationSheetLayoutHelper.ComputeContentEdges, rather than by
                // measuring per-view here.
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

                    // Use the room's OWN level directly, rather than "whichever level
                    // happens to be visible in this view first".
                    Level roomLevel = doc.GetElement(room.LevelId) as Level;

                    bool foundLevel = ElevationSheetLayoutHelper.TryMeasureViewAlignment(
                        v, roomLevel, out double widthFt, out double heightFt, out double dist, out double levelZ, out string measureError);

                    if (measureError != null)
                    {
                        log.AppendLine($"Error measuring level offset for view '{v.Name}': {measureError}");
                    }

                    widthsFt.Add(widthFt);
                    heightsFt.Add(heightFt);

                    if (!foundLevel)
                    {
                        string issue = $"View '{v.Name}': could not determine its room's level line for alignment; using dist=0.";
                        log.AppendLine($"Warning: {issue}");
                        issues.Add(issue);
                    }
                    distsFt.Add(dist);

                    // Re-read the raw crop box for the log table's mm columns below —
                    // TryMeasureViewAlignment already derived widthFt/heightFt/dist/levelZ
                    // from this same box above.
                    BoundingBoxXYZ cropBox = v.CropBox;
                    double cropCenterY = (cropBox.Min.Y + cropBox.Max.Y) / 2.0;

                    log.AppendLine(string.Join("\t",
                        v.Name, room.Number, v.Scale.ToString(),
                        (cropBox.Min.X * FeetToMm).ToString("F3"), (cropBox.Max.X * FeetToMm).ToString("F3"),
                        (cropBox.Min.Y * FeetToMm).ToString("F3"), (cropBox.Max.Y * FeetToMm).ToString("F3"),
                        (widthFt * FeetToMm).ToString("F3"), (heightFt * FeetToMm).ToString("F3"),
                        roomLevel?.Name ?? "N/A", (levelZ * FeetToMm).ToString("F3"),
                        (cropCenterY * FeetToMm).ToString("F3"), (dist * FeetToMm).ToString("F3")));
                }

                // Step 6: layout algorithm — pack views into rows left-to-right, wrapping
                // to a new row when the running width would exceed the content area's
                // right edge, and marking a new sheet when a row would fall below the
                // content area's bottom edge.
                ElevationSheetLayoutHelper.LayoutResult layout = ElevationSheetLayoutHelper.ComputeLayout(
                    widthsFt, heightsFt, distsFt,
                    leftEdgeFt, xSpacingFt, ySpacingFt, rightEdgeFt, topOfSheetFt, lowerEdgeFt);

                // Step 7: create any additional sheets the layout needs.
                List<ViewSheet> sheetList = ElevationSheetLayoutHelper.CreateAdditionalSheets(doc, startingSheet, layout.MaxSheetIndex, log, issues);

                // Step 8: place the real viewports at their computed positions.
                (int placedCount, List<int> placedIndices) = ElevationSheetLayoutHelper.PlaceViewportsOnSheets(doc, elevationViews, layout, sheetList, log, issues);

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
                        Parameter nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME);
                        string roomName = nameParam != null && nameParam.HasValue ? nameParam.AsString() : "No Room Name";
                        titleParam?.Set($"{room.Number} {roomName}");

                        ElevationSheetLayoutHelper.UpdateLevelDisplay(doc, v, isRowEnd, inputWindow.LevelExtensionMm, log, issues);
                    }

                    titleTrans.Commit();
                }

                // Show summary
                bool hasIssues = issues.Count > 0;

                log.AppendLine();
                log.AppendLine($"{placedCount} of {elevationViews.Count} elevation(s) placed across {sheetList.Count} sheet(s).");
                if (hasIssues)
                {
                    log.AppendLine();
                    log.AppendLine($"=== {issues.Count} Issue(s) ===");
                    foreach (string issue in issues) log.AppendLine(issue);
                }

                string logFilePath = Path.Combine(Path.GetTempPath(), "ElevPlaceOnSheetsLog.txt");
                string logWriteError = null;
                try { File.WriteAllText(logFilePath, log.ToString()); }
                catch (Exception ex) { logWriteError = ex.Message; }

                string summary = $"{placedCount} of {elevationViews.Count} elevation(s) placed across {sheetList.Count} sheet(s).";
                if (hasIssues)
                {
                    summary += $"\n\n{issues.Count} issues founded - see log";
                }
                new Warning("Success", summary, credit,
                    revealPath: (logWriteError == null && hasIssues) ? logFilePath : null).ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}\n\nDebug Log:\n{log}");
                return Result.Failed;
            }
        }
    }
}
