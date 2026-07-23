using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Brand_25
{
    // Places a sorted set of window/door elevation views (created by
    // Win_CreateElevations.cs) onto a sheet in left-to-right rows, wrapping to a new
    // row when the running width exceeds the content area's right edge, and creating
    // an additional sheet when a row would run below the content area's bottom edge.
    //
    // This is the window/door counterpart to Elev_PlaceOnSheets.cs (internal/room
    // elevations). The paper-space math, row-packing/sheet-overflow algorithm, sheet
    // creation, and viewport placement are IDENTICAL between the two commands, so both
    // call into ElevationSheetLayoutHelper for that shared work rather than each command
    // keeping its own copy. The one part that's genuinely different is how each view
    // gets matched back to a Level for alignment:
    //   - Elev_PlaceOnSheets parses the view name against a Room-naming convention,
    //     then reads that Room's Level.
    //   - This command has no Room to go through at all. Win_CreateElevations already
    //     hides every Level in the view except the host window/door's own base Level
    //     (see HideOtherLevels in Win_CreateElevations.cs), so the correct Level is
    //     simply "whichever one Level is still visible in the view" — read directly,
    //     no name-parsing involved.
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
    public class Win_PlaceElevOnSheets : IExternalCommand
    {
        private const double MmToFeet = ElevationSheetLayoutHelper.MmToFeet;
        private const double FeetToMm = ElevationSheetLayoutHelper.FeetToMm;

        // Adjust this if your project's window/door elevation ViewFamilyType is named
        // differently — it's only used to pick a sensible default in the dialog's
        // combo box; the user can always pick a different type manually. Internal
        // elevations and window/door elevations always use different ViewFamilyTypes
        // in this project, so no further disambiguation beyond Type + Phase is needed
        // when collecting matching views (Step 3 below).
        private const string PreferredElevationTypeHint = "Window";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2026-07-22. Beta 0.10";

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

                // Step 2: gather user input — same dialog as Elev_PlaceOnSheets, just
                // with its own title and default-type hint so it doesn't look/behave
                // like the internal-elevation command.
                Selection_SheetLayout inputWindow = new Selection_SheetLayout(
                    elevationTypes, phases, credit,
                    dialogTitle: "Place Window/Door Elevations on Sheet",
                    preferredTypeNameContains: PreferredElevationTypeHint);
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
                // size + frame margin + content margin — identical to Elev_PlaceOnSheets.
                (double leftEdgeFt, double rightEdgeFt, double topOfSheetFt, double lowerEdgeFt, double xSpacingFt, double ySpacingFt) =
                    ElevationSheetLayoutHelper.ComputeContentEdges(inputWindow);

                // Step 3: collect matching elevation views (exact type + phase match).
                // Internal elevations and window/door elevations always use different
                // ViewFamilyTypes in this project, so this alone is enough to only pick
                // up window/door elevations here.
                List<View> elevationViews = ElevationSheetLayoutHelper.FindMatchingElevationViews(doc, inputWindow.SelectedViewFamilyType, inputWindow.SelectedPhase);
                if (elevationViews.Count == 0)
                {
                    new Warning("Oops...", "No elevation views found matching the selected type and phase.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 4: sort in a logical (natural) order — so "W2.1" sorts before
                // "W10.1" rather than a plain string sort.
                elevationViews = elevationViews.OrderBy(v => ElevationSheetLayoutHelper.NaturalSortKey(v.Name), StringComparer.Ordinal).ToList();
                log.AppendLine($"Found {elevationViews.Count} matching elevation view(s):");
                foreach (View v in elevationViews) log.AppendLine($"  {v.Name}");

                // Step 4.5: find each view's Level directly — no Room, no name-parsing.
                // Win_CreateElevations already hides every Level in the view except the
                // host window/door's own base Level (HideOtherLevels), so the Level we
                // want is simply the one Level still visible in the view. A view with
                // zero or more than one visible Level means something about it doesn't
                // match that convention (e.g. levels re-shown manually after the fact) —
                // skipped rather than guessed at.
                List<(View View, Level Level)> matchedViews = new List<(View, Level)>();
                foreach (View v in elevationViews)
                {
                    List<Level> visibleLevels = new FilteredElementCollector(doc, v.Id)
                        .OfCategory(BuiltInCategory.OST_Levels)
                        .WhereElementIsNotElementType()
                        .Cast<Level>()
                        .Where(l => !l.IsHidden(v))
                        .ToList();

                    if (visibleLevels.Count != 1)
                    {
                        string issue = visibleLevels.Count == 0
                            ? $"View '{v.Name}': no visible Level found in this view — skipped."
                            : $"View '{v.Name}': {visibleLevels.Count} Levels visible in this view (expected exactly 1) — ambiguous, skipped.";
                        log.AppendLine($"Warning: {issue}");
                        issues.Add(issue);
                        continue;
                    }

                    matchedViews.Add((v, visibleLevels[0]));
                }

                if (matchedViews.Count == 0)
                {
                    new Warning("Oops...", "None of the matching elevation views had a single, unambiguous Level visible. See the log for details.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Collapse back into parallel lists (same order), so the rest of the
                // pipeline below — which indexes into elevationViews — needs minimal change.
                elevationViews = matchedViews.Select(m => m.View).ToList();
                List<Level> matchedLevels = matchedViews.Select(m => m.Level).ToList();

                // Step 5: measurement pass. Reads each view's crop box (paper space, via
                // CropBox / view.Scale) and its own Level's line position. This is a
                // read-only pass over the View/Level API — no Viewport instance or
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
                    "ViewName", "ViewScale",
                    "CropMinX_model_mm", "CropMaxX_model_mm", "CropMinY_model_mm", "CropMaxY_model_mm",
                    "CropWidth_mm", "CropHeight_mm",
                    "LevelName", "LevelZ_model_mm", "CropCenterY_model_mm", "Dist_mm"));

                for (int i = 0; i < elevationViews.Count; i++)
                {
                    View v = elevationViews[i];
                    Level level = matchedLevels[i];

                    bool foundLevel = ElevationSheetLayoutHelper.TryMeasureViewAlignment(
                        v, level, out double widthFt, out double heightFt, out double dist, out double levelZ, out string measureError);

                    if (measureError != null)
                    {
                        log.AppendLine($"Error measuring level offset for view '{v.Name}': {measureError}");
                    }

                    widthsFt.Add(widthFt);
                    heightsFt.Add(heightFt);

                    if (!foundLevel)
                    {
                        string issue = $"View '{v.Name}': could not determine its level line for alignment; using dist=0.";
                        log.AppendLine($"Warning: {issue}");
                        issues.Add(issue);
                    }
                    distsFt.Add(dist);

                    BoundingBoxXYZ cropBox = v.CropBox;
                    double cropCenterY = (cropBox.Min.Y + cropBox.Max.Y) / 2.0;

                    log.AppendLine(string.Join("\t",
                        v.Name, v.Scale.ToString(),
                        (cropBox.Min.X * FeetToMm).ToString("F3"), (cropBox.Max.X * FeetToMm).ToString("F3"),
                        (cropBox.Min.Y * FeetToMm).ToString("F3"), (cropBox.Max.Y * FeetToMm).ToString("F3"),
                        (widthFt * FeetToMm).ToString("F3"), (heightFt * FeetToMm).ToString("F3"),
                        level?.Name ?? "N/A", (levelZ * FeetToMm).ToString("F3"),
                        (cropCenterY * FeetToMm).ToString("F3"), (dist * FeetToMm).ToString("F3")));
                }

                // Step 6: layout algorithm — identical to Elev_PlaceOnSheets.
                ElevationSheetLayoutHelper.LayoutResult layout = ElevationSheetLayoutHelper.ComputeLayout(
                    widthsFt, heightsFt, distsFt,
                    leftEdgeFt, xSpacingFt, ySpacingFt, rightEdgeFt, topOfSheetFt, lowerEdgeFt);

                // Step 7: create any additional sheets the layout needs.
                List<ViewSheet> sheetList = ElevationSheetLayoutHelper.CreateAdditionalSheets(doc, startingSheet, layout.MaxSheetIndex, log, issues);

                // Step 8: place the real viewports at their computed positions.
                (int placedCount, List<int> placedIndices) = ElevationSheetLayoutHelper.PlaceViewportsOnSheets(doc, elevationViews, layout, sheetList, log, issues);

                // Step 9: for every successfully placed view, copy its own title (the
                // Mark-based view Name Win_CreateElevations assigned, e.g. "W101.1")
                // into "Title on Sheet", and clean up level line display — hide bubbles
                // on every level visible in the view except the right bubble of the
                // row-end view on each row, and shorten each level line — identical
                // treatment to Elev_PlaceOnSheets, just with no Room to pull a title from.
                using (Transaction titleTrans = new Transaction(doc, "LW_Set View Titles and Level Display"))
                {
                    titleTrans.Start();

                    foreach (int i in placedIndices)
                    {
                        View v = elevationViews[i];
                        bool isRowEnd = layout.IsRowEnd[i];

                        Parameter titleParam = v.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                        titleParam?.Set(v.Name);

                        ElevationSheetLayoutHelper.UpdateLevelDisplay(doc, v, isRowEnd, inputWindow.LevelExtensionMm, log, issues);
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

                string logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WinPlaceElevOnSheetsLog.txt");
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
    }
}
