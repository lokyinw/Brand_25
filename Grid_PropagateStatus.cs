using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Brand_25
{
    [Transaction(TransactionMode.Manual)]
    public class Grid_PropagateStatus : IExternalCommand
    {
        // Running totals for the end-of-run dialog. Issues = things the user should look at in the log.
        private class RunStats
        {
            public int GridsFound;
            public int GridsSkippedSource;   // grid cannot be visible in the source view
            public int PairsSkippedTarget;   // grid cannot be visible in a particular target view
            public int PairsProcessed;       // grid/target-view pairs actually propagated
            public int Errors;               // unexpected exceptions caught and logged
            public int Notes;                // informational only, not counted as issues
            public int Issues => GridsSkippedSource + PairsSkippedTarget + Errors;
        }

        // Source-view state of one grid, read once and applied to every target view.
        private class GridSourceState
        {
            public Grid Grid;
            public bool Hidden;
            public bool BubbleEnd0;
            public bool BubbleEnd1;
            public XYZ CurveStart;   // null when the grid has no view-specific curve in the source view
            public XYZ CurveEnd;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uidoc = uiApp.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;
            string credit = "Last Modified by Lok on 2026-10-22. Beta 0.92";

            // Validate active view
            if (activeView.ViewType != ViewType.FloorPlan && activeView.ViewType != ViewType.CeilingPlan)
            {
                new Warning("Oops...", "Set the Active View to a Floor Plan or a Ceiling Plan before proceeding.", credit).ShowDialog();

                if (activeView.ViewType == ViewType.DrawingSheet)
                {
                    new Warning("...um...", "By the way, a sheet is not a plan view.", credit).ShowDialog();
                }
                return Result.Failed;
            }

            // Get eligible views
            List<View> eligibleViews = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan)
                .Where(v => v.CanHaveTypeAssigned())
                .OrderBy(v => v.Name)
                .ToList();

            if (eligibleViews.Count == 0)
            {
                new Warning("No Views", "No eligible plan views found in project.", credit).ShowDialog();
                return Result.Succeeded;
            }

            // Show selection dialog
            Selection_ViewPlan selector = new Selection_ViewPlan(
                eligibleViews,
                "Propagate Grid Status - Select Target View(s)",
                credit);

            if (selector.ShowDialog() != true)
                return Result.Cancelled;

            // Process selected views
            List<View> selectedViews = selector.SelectedViews;
            if (selectedViews.Count == 0)
                return Result.Succeeded;

            // Execute the grid propagation. Everything of interest is recorded in 'log'.
            List<string> log = new List<string>
            {
                $"Grid_PropagateStatus log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Source view: '{activeView.Name}' (Id {activeView.Id.Value})",
                $"Target views selected: {selectedViews.Count}",
                ""
            };
            RunStats stats = new RunStats();
            string fatalError = null;

            try
            {
                PropagateGridStatus(doc, activeView, selectedViews, log, stats);
            }
            catch (Exception ex)
            {
                // Anything that escapes the per-grid handling. The transaction is disposed
                // uncommitted, so Revit rolls the whole run back.
                fatalError = ex.Message;
                stats.Errors++;
                log.Add($"[Fatal] {ex.GetType().Name}: {ex.Message}");
                log.Add("[Fatal] The transaction was rolled back - no changes were kept.");
            }

            log.Add("");
            log.Add(
                $"[Summary] Grids found: {stats.GridsFound}, " +
                $"grids skipped (not visible in source view): {stats.GridsSkippedSource}, " +
                $"grid/view pairs skipped (not visible in target view): {stats.PairsSkippedTarget}, " +
                $"pairs propagated: {stats.PairsProcessed}, " +
                $"errors: {stats.Errors}, notes: {stats.Notes}");

            // Log is written to the temp folder; its path is never shown in the dialog body.
            string logPath = Path.Combine(Path.GetTempPath(), $"Grid_PropagateStatus_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            string logWriteError = null;
            try
            {
                File.WriteAllLines(logPath, log);
            }
            catch (Exception ex)
            {
                logWriteError = ex.Message;
            }

            bool hasIssues = stats.Issues > 0;
            bool showLogButton = logWriteError == null && hasIssues;

            string summaryMessage =
                (fatalError != null
                    ? "The run failed and was rolled back. No changes were made.\n"
                    : $"Grid status propagated to {selectedViews.Count} view(s).\n") +
                (hasIssues ? $"{stats.Issues} issues founded - see log\n" : "") +
                (logWriteError != null ? $"Log FAILED to write: {logWriteError}" : "");

            new Warning(fatalError != null ? "Error" : "Propagate Grid Status", summaryMessage, credit,
                revealPath: showLogButton ? logPath : null).ShowDialog();

            if (fatalError != null)
            {
                message = fatalError;
                return Result.Failed;
            }
            return Result.Succeeded;
        }

        private void PropagateGridStatus(Document doc, View sourceView, List<View> targetViews,
                                         List<string> log, RunStats stats)
        {
            // Collected once (the previous version re-ran the query for every target view).
            List<Grid> grids = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Grids)
                .WhereElementIsNotElementType()
                .OfType<Grid>()
                .ToList();
            stats.GridsFound = grids.Count;

            // ---- Phase 1: read the source state of every grid, once ----
            // A grid that cannot be visible in the source view is skipped for ALL targets and
            // left untouched there (Revit throws ArgumentException when asking such a grid
            // for bubbles or curves in a view where it cannot appear).
            List<GridSourceState> states = new List<GridSourceState>();
            foreach (Grid grid in grids)
            {
                if (!grid.CanBeVisibleInView(sourceView))
                {
                    stats.GridsSkippedSource++;
                    log.Add($"[Skip] {Describe(grid)}: cannot be visible in source view '{sourceView.Name}' - left untouched in all target views");
                    continue;
                }

                try
                {
                    GridSourceState state = new GridSourceState
                    {
                        Grid = grid,
                        Hidden = grid.IsHidden(sourceView),
                        BubbleEnd0 = grid.IsBubbleVisibleInView(DatumEnds.End0, sourceView),
                        BubbleEnd1 = grid.IsBubbleVisibleInView(DatumEnds.End1, sourceView)
                    };

                    IList<Curve> sourceCurves = grid.GetCurvesInView(DatumExtentType.ViewSpecific, sourceView);
                    if (sourceCurves.Count > 0)
                    {
                        state.CurveStart = sourceCurves[0].GetEndPoint(0);
                        state.CurveEnd = sourceCurves[0].GetEndPoint(1);
                    }
                    else
                    {
                        stats.Notes++;
                        log.Add($"[Note] {Describe(grid)}: no view-specific curve in source view - extent not propagated");
                    }

                    states.Add(state);
                }
                catch (Exception ex)
                {
                    stats.Errors++;
                    log.Add($"[Error] {Describe(grid)} | source view '{sourceView.Name}' | step: read source state | {ex.GetType().Name}: {ex.Message}");
                }
            }

            // ---- Phase 2: apply to every target view ----
            using (Transaction tx = new Transaction(doc, "LW_Propagate Grid Status"))
            {
                tx.Start();

                foreach (View targetView in targetViews)
                {
                    foreach (GridSourceState s in states)
                    {
                        Grid grid = s.Grid;

                        // Same guard on the target side: skip this pair and carry on.
                        if (!grid.CanBeVisibleInView(targetView))
                        {
                            stats.PairsSkippedTarget++;
                            log.Add($"[Skip] {Describe(grid)}: cannot be visible in target view '{targetView.Name}' (Id {targetView.Id.Value})");
                            continue;
                        }

                        stats.PairsProcessed++;

                        // Each step is isolated so one failure is logged with its exact location
                        // and the remaining steps / grids / views still run.
                        void Step(string stepName, Action action)
                        {
                            try
                            {
                                action();
                            }
                            catch (Exception ex)
                            {
                                stats.Errors++;
                                log.Add($"[Error] {Describe(grid)} | target view '{targetView.Name}' (Id {targetView.Id.Value}) | step: {stepName} | {ex.GetType().Name}: {ex.Message}");
                            }
                        }

                        // Visibility
                        Step("visibility", () =>
                        {
                            if (s.Hidden)
                                targetView.HideElements(new List<ElementId> { grid.Id });
                            else
                                targetView.UnhideElements(new List<ElementId> { grid.Id });
                        });

                        // Bubble visibility
                        foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                        {
                            bool showBubble = end == DatumEnds.End0 ? s.BubbleEnd0 : s.BubbleEnd1;
                            Step($"bubble {end}", () =>
                            {
                                if (showBubble)
                                    grid.ShowBubbleInView(end, targetView);
                                else
                                    grid.HideBubbleInView(end, targetView);
                            });
                        }

                        // Curve position (X/Y from the source, Z kept from the target)
                        if (s.CurveStart != null)
                        {
                            Step("curve extent", () =>
                            {
                                IList<Curve> targetCurves = grid.GetCurvesInView(DatumExtentType.ViewSpecific, targetView);
                                if (targetCurves.Count == 0)
                                {
                                    stats.Notes++;
                                    log.Add($"[Note] {Describe(grid)}: no view-specific curve in target view '{targetView.Name}' - extent not propagated");
                                    return;
                                }

                                double targetZ = targetCurves[0].GetEndPoint(0).Z;
                                XYZ newStart = new XYZ(s.CurveStart.X, s.CurveStart.Y, targetZ);
                                XYZ newEnd = new XYZ(s.CurveEnd.X, s.CurveEnd.Y, targetZ);

                                grid.SetCurveInView(DatumExtentType.ViewSpecific, targetView, Line.CreateBound(newStart, newEnd));
                            });
                        }
                    }
                }

                TransactionStatus status = tx.Commit();
                if (status != TransactionStatus.Committed)
                {
                    stats.Errors++;
                    log.Add($"[Error] Transaction did not commit (status: {status}) - no changes were kept.");
                }
            }
        }

        private static string Describe(Grid grid)
        {
            return $"Grid '{grid.Name}' (Id {grid.Id.Value})";
        }
    }
}
