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
    // Aligns the "setting-out" of selected plan viewports (on other sheets) to match
    // a user-chosen source plan viewport, using the Project Base Point as the shared
    // model-space reference.
    //
    // MEASUREMENT METHOD (v0.15): uses Revit's own OFFICIAL, documented model-to-sheet
    // transform chain — View.GetModelToProjectionTransforms() (model space -> view
    // projection space) combined with Viewport.GetProjectionToSheetTransform() (view
    // projection space -> sheet/paper space), per Autodesk's own documented formula:
    //     sheetXYZ = projectionToSheetTransform * modelToProjectionTransform * modelXYZ
    // This is the same mechanism the Revit SDK's own "SheetToView3D" sample uses to
    // convert a sheet click into a 3D model ray (in reverse). Unlike everything tried
    // in earlier versions of this command, this is a real, supported API built
    // specifically for converting between model space and sheet space — not something
    // derived indirectly from crop-box or box-outline geometry.
    //
    // A view's crop can be split into multiple disjoint regions (rare, but supported
    // by the API), each with its own transform and its own model-space boundary
    // (TransformWithBoundary.GetBoundary()) — GetModelToProjectionTransforms() returns
    // one entry per region. This command picks whichever region's boundary actually
    // contains the model point being projected (PBP, or PBP-plus-test-offset for the
    // rotation check); for the overwhelming majority of views (a single, non-split
    // crop) there is exactly one region and this is a non-issue.
    //
    // VERSION HISTORY (each superseded after logged evidence showed it didn't work):
    //   v0.10 — Viewport.GetBoxCenter() + View.CropBox math. Inconsistent, view-
    //           specific offsets confirmed by comparing crop half-width/half-height
    //           against Viewport.GetBoxOutline() on every view.
    //   v0.11 — Same math wrapped in a temporary Annotation Crop toggle intended to
    //           "clean" that mismatch. Logged corrections were exactly 0.00 on every
    //           row — no measurable effect, so that theory was wrong.
    //   v0.12 — Added GetLabelOutline()/crop-shape diagnostics rather than guessing
    //           again; alignment still used the same unreliable formula.
    //   v0.13 — Tried two temporary Grid elements read via
    //           Grid.GetCurvesInView(DatumExtentType.ViewSpecific, sheet). That call
    //           returned the grids' raw MODEL-space input coordinates unchanged, not a
    //           paper-space projection — unusable for this. It also toggled
    //           View.CropBoxActive without separately restoring View.CropBoxVisible,
    //           visibly (and undesirably) re-enabling crop-boundary display on views
    //           where it had deliberately been hidden.
    //   v0.14 — Pure read-only calibration pass (no move) comparing a naive guess
    //           against the sheet's own title block corner. The user's manually
    //           measured ground truth confirmed the earlier formula's error varies
    //           per-view with no discoverable pattern — conclusive evidence that
    //           Viewport.GetBoxOutline()/GetBoxCenter() cannot be corrected into a
    //           true crop-center reading from public API surface alone.
    //   v0.15 (this version) — Revit's own documented model-to-sheet transform chain.
    [Transaction(TransactionMode.Manual)]
    public class Plan_AlignOnSheets : IExternalCommand
    {
        private const double MmPerFt = 304.8;

        // Arbitrary non-zero test distance (model feet) used only to establish a
        // second point for the rotation-direction comparison — see AreRotationsAligned.
        private const double RotationTestDistanceFt = 50.0;

        // Direction vectors are compared via the ABSOLUTE VALUE of their dot product —
        // a line has no inherent forward/backward, so comparing raw (signed) direction
        // would risk a false "rotation mismatch" if two views happened to produce
        // opposite-signed (but really parallel) directions. Anything above this counts
        // as "the same direction" (~0.06 degrees of tolerance).
        private const double RotationAlignedDotThreshold = 0.999999;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2026-08-24. Beta 0.15";

            List<string> log = new List<string>();

            try
            {
                // ---------------- Step 1: Project Base Point ----------------
                BasePoint projectBasePoint = BasePoint.GetProjectBasePoint(doc);
                if (projectBasePoint == null)
                {
                    new Warning("Oops...", "This project has no Project Base Point — alignment needs it as a shared reference point and can't proceed without it.", credit).ShowDialog();
                    return Result.Cancelled;
                }
                XYZ pbp = projectBasePoint.Position;

                // ---------------- Step 2: collect every qualifying plan viewport ----------------
                List<VM_PlanViewport> allPlans = CollectQualifyingPlans(doc);
                if (allPlans.Count == 0)
                {
                    new Warning("Oops...", "No sheets with a Floor Plan, Ceiling Plan, Area Plan, or Structural Plan viewport were found.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // ---------------- Step 3: pre-select the active sheet's own plan, if any ----------------
                VM_PlanViewport preSelectSource = null;
                if (doc.ActiveView is ViewSheet activeSheet)
                {
                    preSelectSource = allPlans
                        .Where(p => p.Sheet.Id == activeSheet.Id)
                        .OrderByDescending(p => p.PaperAreaSqFt)
                        .FirstOrDefault();
                }

                // ---------------- Step 4: dialog ----------------
                Selection_PlanAlign dlg = new Selection_PlanAlign(allPlans, preSelectSource, credit);
                if (dlg.ShowDialog() != true)
                    return Result.Cancelled;

                VM_PlanViewport source = dlg.SelectedSource;
                List<VM_PlanViewport> targets = dlg.SelectedTargets
                    .Where(t => t.Viewport.Id != source.Viewport.Id) // defensive; UI already excludes this
                    .ToList();

                if (targets.Count == 0)
                {
                    new Warning("Oops...", "No target plans were selected.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // ---------------- Step 5: align ----------------
                int alignedCount = 0;
                List<string> rotationMismatches = new List<string>();
                List<string> scaleMismatches = new List<string>();
                List<string> errors = new List<string>();
                List<string> largeResiduals = new List<string>();

                const double residualFlagThresholdMm = 1.0;

                log.Add("Plan Alignment — Log");
                log.Add($"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                log.Add($"Project: {doc.Title}");
                log.Add($"Source: {source.ComboLabel}");
                log.Add($"Project Base Point (model, mm): X={pbp.X * MmPerFt:F2}, Y={pbp.Y * MmPerFt:F2}, Z={pbp.Z * MmPerFt:F2}");
                log.Add("Measurement method: View.GetModelToProjectionTransforms() + Viewport.GetProjectionToSheetTransform()");
                log.Add(new string('-', 70));
                log.Add("");
                log.Add("=== Diagnostic Measurements (tab-separated; paste into Excel) ===");
                log.Add(DiagnosticHeader());

                using (Transaction t = new Transaction(doc, "LW_Align Plans on Sheets"))
                {
                    t.Start();

                    XYZ sourcePaperPoint;
                    XYZ sourceDir;
                    try
                    {
                        ProjectionResult sourceResult = MeasureViaTransforms(doc, source.Sheet, source.PlanView, source.Viewport, pbp, source.ComboLabel, "SOURCE", log);
                        if (!sourceResult.Success)
                        {
                            t.RollBack();
                            new Warning("Oops...", $"Could not measure the source plan's setting-out: {sourceResult.Error}", credit).ShowDialog();
                            return Result.Failed;
                        }
                        sourcePaperPoint = sourceResult.PaperPoint;
                        sourceDir = sourceResult.TestDirection;
                    }
                    catch (Exception ex)
                    {
                        t.RollBack();
                        new Warning("Oops...", $"Could not measure the source plan's setting-out: {ex.Message}", credit).ShowDialog();
                        return Result.Failed;
                    }

                    foreach (VM_PlanViewport target in targets)
                    {
                        string label = target.ComboLabel;

                        try
                        {
                            if (target.PlanView.Scale != source.PlanView.Scale)
                            {
                                log.Add($"  \u26A0 {label}  \u2014 scale (1:{target.PlanView.Scale}) differs from the source (1:{source.PlanView.Scale}); left untouched.");
                                scaleMismatches.Add(label);
                                continue;
                            }

                            ProjectionResult beforeResult = MeasureViaTransforms(doc, target.Sheet, target.PlanView, target.Viewport, pbp, label, "TARGET-BEFORE", log);
                            if (!beforeResult.Success)
                            {
                                log.Add($"  \u2717 {label}  \u2014 error: {beforeResult.Error}");
                                errors.Add(label);
                                continue;
                            }

                            double rotationDot = Math.Abs(sourceDir.DotProduct(beforeResult.TestDirection));
                            log.Add($"    Rotation check vs source: |dot|={rotationDot:F6} (threshold {RotationAlignedDotThreshold:F6})");

                            if (rotationDot < RotationAlignedDotThreshold)
                            {
                                log.Add($"  \u26A0 {label}  \u2014 rotation differs from the source; left untouched.");
                                rotationMismatches.Add(label);
                                continue;
                            }

                            XYZ delta = sourcePaperPoint - beforeResult.PaperPoint;

                            XYZ oldCenter = target.Viewport.GetBoxCenter();
                            XYZ newCenter = new XYZ(oldCenter.X + delta.X, oldCenter.Y + delta.Y, 0);
                            target.Viewport.SetBoxCenter(newCenter);
                            doc.Regenerate();

                            ProjectionResult afterResult = MeasureViaTransforms(doc, target.Sheet, target.PlanView, target.Viewport, pbp, label, "TARGET-AFTER", log);
                            if (!afterResult.Success)
                            {
                                log.Add($"  \u2717 {label}  \u2014 moved, but could not re-measure afterward: {afterResult.Error}");
                                errors.Add(label);
                                continue;
                            }

                            XYZ residual = afterResult.PaperPoint - sourcePaperPoint;
                            double residualMm = Math.Sqrt(residual.X * residual.X + residual.Y * residual.Y) * MmPerFt;
                            log.Add($"    Residual vs source after move: dX={residual.X * MmPerFt:F2} mm, dY={residual.Y * MmPerFt:F2} mm (magnitude {residualMm:F2} mm)");

                            log.Add($"  \u2705 {label}  \u2014 shifted by (X={delta.X * MmPerFt:F1} mm, Y={delta.Y * MmPerFt:F1} mm) on paper.");
                            alignedCount++;

                            if (residualMm > residualFlagThresholdMm)
                            {
                                largeResiduals.Add($"{label} (residual {residualMm:F2} mm)");
                            }
                        }
                        catch (Exception ex)
                        {
                            log.Add($"  \u2717 {label}  \u2014 error: {ex.Message}");
                            errors.Add(label);
                        }
                    }

                    t.Commit();
                }

                // SetBoxCenter() updates the model correctly, but the active view's own
                // on-screen graphics don't repaint until something (e.g. a pan) forces
                // it — RefreshActiveView() is the documented fix for exactly this.
                uiDoc.RefreshActiveView();

                // ---------------- Step 6: log + summary ----------------
                log.Add("");
                log.Add($"{alignedCount} of {targets.Count} plan(s) aligned.");
                if (scaleMismatches.Count > 0)
                    log.Add($"{scaleMismatches.Count} plan(s) skipped — scale differs from the source (see \u26A0 entries above).");
                if (rotationMismatches.Count > 0)
                    log.Add($"{rotationMismatches.Count} plan(s) skipped — rotation differs from the source (see \u26A0 entries above).");
                if (errors.Count > 0)
                    log.Add($"{errors.Count} plan(s) failed with an error (see \u2717 entries above).");
                if (largeResiduals.Count > 0)
                {
                    log.Add("");
                    log.Add($"{largeResiduals.Count} plan(s) still show a residual gap greater than {residualFlagThresholdMm:F1} mm after alignment " +
                        "(this checks the move math's own self-consistency against the same transform chain used for measurement):");
                    log.AddRange(largeResiduals.Select(r => $"  {r}"));
                }

                string logFilePath = Path.Combine(Path.GetTempPath(), $"Plan_AlignOnSheets_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                string logWriteError = null;
                try { File.WriteAllLines(logFilePath, log); }
                catch (Exception ex) { logWriteError = ex.Message; }

                // Kept short and free of implementation detail — the log (via the "Log
                // Folder" button, shown only when there's something worth checking) is
                // where anything technical belongs.
                StringBuilder summary = new StringBuilder();
                summary.AppendLine($"{alignedCount} of {targets.Count} plan(s) aligned to \"{source.ComboLabel}\".");
                if (scaleMismatches.Count > 0)
                    summary.AppendLine($"{scaleMismatches.Count} plan(s) skipped \u2014 scale differs from the source.");
                if (rotationMismatches.Count > 0)
                    summary.AppendLine($"{rotationMismatches.Count} plan(s) skipped \u2014 rotation differs from the source.");
                if (errors.Count > 0)
                    summary.AppendLine($"{errors.Count} plan(s) failed \u2014 see log.");
                if (largeResiduals.Count > 0)
                    summary.AppendLine($"{largeResiduals.Count} plan(s) still show a gap > {residualFlagThresholdMm:F1} mm \u2014 see log for diagnostics.");
                if (logWriteError != null)
                    summary.AppendLine($"\nLog FAILED to write: {logWriteError}");

                bool hasNotesForLog = scaleMismatches.Count > 0 || rotationMismatches.Count > 0 || errors.Count > 0 || largeResiduals.Count > 0;

                new WarningLarge("Align Plans Complete", summary.ToString(), credit,
                    revealPath: (logWriteError == null && hasNotesForLog) ? logFilePath : null).ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}\n\nDebug Log:\n{string.Join("\n", log)}");
                return Result.Failed;
            }
        }

        // ---------------- Collection ----------------

        private static readonly ViewType[] QualifyingPlanTypes =
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.AreaPlan, ViewType.EngineeringPlan
        };

        private static List<VM_PlanViewport> CollectQualifyingPlans(Document doc)
        {
            List<VM_PlanViewport> result = new List<VM_PlanViewport>();

            List<ViewSheet> sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .ToList();

            foreach (ViewSheet sheet in sheets)
            {
                foreach (ElementId viewportId in sheet.GetAllViewports())
                {
                    if (!(doc.GetElement(viewportId) is Viewport vp)) continue;
                    if (!(doc.GetElement(vp.ViewId) is View v)) continue;
                    if (v.IsTemplate) continue;
                    if (!IsQualifyingPlanType(v.ViewType)) continue;

                    result.Add(new VM_PlanViewport(sheet, vp, v));
                }
            }

            return result;
        }

        // ViewType has no distinct "StructuralPlan" member — Structural Plan views in
        // the API are reported as ViewType.EngineeringPlan.
        private static bool IsQualifyingPlanType(ViewType viewType) =>
            QualifyingPlanTypes.Contains(viewType);

        // ---------------- Measurement via Revit's official model-to-sheet transform chain ----------------

        private class ProjectionResult
        {
            public bool Success;
            public string Error;
            public XYZ PaperPoint;
            public XYZ TestDirection; // sheet-space direction from PaperPoint toward a second test point — for rotation comparison only
        }

        private static string DiagnosticHeader() => string.Join("\t",
            "Label", "Stage", "SheetNumber", "ViewName", "ViewportType", "ViewScale",
            "HasViewTransforms", "HasViewportTransforms", "TransformRegionCount",
            "SheetX_mm", "SheetY_mm",
            // Title-block-relative columns were calibration-only — used to verify the
            // transform chain against a manually measured ground truth (confirmed to
            // match exactly). No longer needed now that the method is trusted; left
            // commented out (rather than removed) alongside the code that populates
            // them below, in case a future measurement method needs recalibrating the
            // same way. See LogRow for the matching commented-out block.
            // "TitleBlockFound", "TitleBlockMinX_mm", "TitleBlockMinY_mm",
            // "SheetRelTitleBlockX_mm", "SheetRelTitleBlockY_mm",
            "TestDirX", "TestDirY",
            "Note");

        // Projects PBP (and a second, nearby test point, for the caller's own rotation
        // comparison) from model space to sheet/paper space via Revit's documented
        // transform chain, and logs one diagnostic row. TitleBlock columns are also
        // included — carried over from the previous calibration round — purely so this
        // measurement can be sanity-checked against the same manually-measured ground
        // truth already gathered, without needing another round-trip.
        private static ProjectionResult MeasureViaTransforms(Document doc, ViewSheet sheet, View view, Viewport vp, XYZ pbp, string label, string stage, List<string> log)
        {
            ProjectionResult result = new ProjectionResult { Success = false };

            bool hasViewTransforms = false, hasViewportTransforms = false;
            try { hasViewTransforms = view.HasViewTransforms(); } catch { }
            try { hasViewportTransforms = vp.HasViewportTransforms(); } catch { }

            if (!hasViewTransforms || !hasViewportTransforms)
            {
                result.Error = $"This view/viewport does not report model-to-sheet transforms (HasViewTransforms={hasViewTransforms}, HasViewportTransforms={hasViewportTransforms}).";
                LogRow(log, label, stage, sheet, view, vp, hasViewTransforms, hasViewportTransforms, 0, null, null, result.Error);
                return result;
            }

            Transform projToSheet;
            IList<TransformWithBoundary> transforms;
            try
            {
                projToSheet = vp.GetProjectionToSheetTransform();
                transforms = view.GetModelToProjectionTransforms();
            }
            catch (Exception ex)
            {
                result.Error = $"Could not retrieve transforms: {ex.Message}";
                LogRow(log, label, stage, sheet, view, vp, hasViewTransforms, hasViewportTransforms, 0, null, null, result.Error);
                return result;
            }

            if (transforms == null || transforms.Count == 0)
            {
                result.Error = "No model-to-projection transforms were returned for this view.";
                LogRow(log, label, stage, sheet, view, vp, hasViewTransforms, hasViewportTransforms, 0, null, null, result.Error);
                return result;
            }

            XYZ testModelPoint = pbp + XYZ.BasisX * RotationTestDistanceFt;

            TransformWithBoundary regionForPbp = ChooseRegion(transforms, pbp);
            TransformWithBoundary regionForTest = ChooseRegion(transforms, testModelPoint);

            XYZ sheetPbp = ProjectThroughRegion(regionForPbp, projToSheet, pbp);
            XYZ sheetTest = ProjectThroughRegion(regionForTest, projToSheet, testModelPoint);

            XYZ testDir = sheetTest - sheetPbp;
            if (testDir.IsZeroLength()) testDir = XYZ.BasisX; // degenerate; harmless fallback for the rotation check only
            testDir = testDir.Normalize();

            result.Success = true;
            result.PaperPoint = new XYZ(sheetPbp.X, sheetPbp.Y, 0);
            result.TestDirection = testDir;

            LogRow(log, label, stage, sheet, view, vp, hasViewTransforms, hasViewportTransforms, transforms.Count, result.PaperPoint, testDir, "OK");
            return result;
        }

        // Picks whichever region's model-space boundary contains the given point —
        // relevant only for views whose crop has been split into multiple disjoint
        // regions. Falls back to the first region if none explicitly contains the
        // point (e.g. it happens to fall just outside every crop boundary) or if a
        // region's own boundary is null (a single, uncropped/simple region, per the
        // API's own documented behavior).
        private static TransformWithBoundary ChooseRegion(IList<TransformWithBoundary> transforms, XYZ modelPoint)
        {
            foreach (TransformWithBoundary twb in transforms)
            {
                CurveLoop boundary = null;
                try { boundary = twb.GetBoundary(); } catch { }
                if (boundary == null) return twb;
                if (IsPointInsideCurveLoop(modelPoint, boundary)) return twb;
            }
            return transforms[0];
        }

        private static XYZ ProjectThroughRegion(TransformWithBoundary region, Transform projToSheet, XYZ modelPoint)
        {
            Transform modelToProj = region.GetModelToProjectionTransform();
            XYZ projectionPoint = modelToProj.OfPoint(modelPoint);
            return projToSheet.OfPoint(projectionPoint);
        }

        // Point-in-curveloop test for a planar 3D CurveLoop, via a parity (even/odd)
        // ray-crossing count — ported from the Revit SDK's own "SheetToView3D" sample
        // (MakeView3D.IsPointInsideCurveLoop), which uses this exact technique to
        // decide which crop region a given model point belongs to.
        private static bool IsPointInsideCurveLoop(XYZ point, CurveLoop curveLoop)
        {
            List<Curve> curves = curveLoop.ToList();
            if (curves.Count < 3) return true; // degenerate boundary; don't block on this

            Plane plane;
            try { plane = Plane.CreateByThreePoints(curves[0].GetEndPoint(0), curves[1].GetEndPoint(0), curves[2].GetEndPoint(0)); }
            catch { return true; } // can't determine the plane; don't block

            plane.Project(point, out UV uv, out _);
            XYZ projectedPoint = plane.Origin + plane.XVec * uv.U + plane.YVec * uv.V;

            const double randomXScale = 5631, randomYScale = 4369; // arbitrary, non-axis-aligned — matches the SDK sample
            Line veryLongLine = Line.CreateBound(projectedPoint, projectedPoint + randomXScale * plane.XVec + randomYScale * plane.YVec);

            int intersectionCount = 0;
            foreach (Curve edge in curves)
            {
                SetComparisonResult res = veryLongLine.Intersect(edge, out IntersectionResultArray resultArray);
                if (res == SetComparisonResult.Overlap) intersectionCount += resultArray.Size;
            }

            return (intersectionCount % 2) == 1;
        }

        private static void LogRow(List<string> log, string label, string stage, ViewSheet sheet, View view, Viewport vp,
            bool hasViewTransforms, bool hasViewportTransforms, int regionCount, XYZ sheetPoint, XYZ testDir, string note)
        {
            string viewportTypeName = GetElementName(view, vp.GetTypeId());

            // Calibration-only (see the matching commented-out header columns above) —
            // was used to verify the transform-chain measurement against a manually
            // measured ground truth from the title block's corner (confirmed exact
            // match). Left here, commented out, rather than deleted, in case a future
            // measurement method needs the same kind of recalibration.
            //
            // FamilyInstance titleBlock = new FilteredElementCollector(view.Document, sheet.Id)
            //     .OfCategory(BuiltInCategory.OST_TitleBlocks)
            //     .WhereElementIsNotElementType()
            //     .Cast<FamilyInstance>()
            //     .FirstOrDefault();
            //
            // BoundingBoxXYZ titleBlockBox = null;
            // try { titleBlockBox = titleBlock?.get_BoundingBox(sheet); }
            // catch { }
            // bool titleBlockFound = titleBlockBox != null;

            string F(double v) => (v * MmPerFt).ToString("F2");
            string FX(XYZ p, bool useX) => p == null ? "N/A" : F(useX ? p.X : p.Y);
            string FDir(XYZ v, bool useX) => v == null ? "N/A" : (useX ? v.X : v.Y).ToString("F6");

            // string relX = (sheetPoint != null && titleBlockFound) ? F(sheetPoint.X - titleBlockBox.Min.X) : "N/A";
            // string relY = (sheetPoint != null && titleBlockFound) ? F(sheetPoint.Y - titleBlockBox.Min.Y) : "N/A";

            string row = string.Join("\t",
                label, stage, sheet.SheetNumber, view.Name, viewportTypeName, view.Scale.ToString(),
                hasViewTransforms.ToString(), hasViewportTransforms.ToString(), regionCount.ToString(),
                FX(sheetPoint, true), FX(sheetPoint, false),
                // titleBlockFound.ToString(),
                // titleBlockFound ? F(titleBlockBox.Min.X) : "N/A",
                // titleBlockFound ? F(titleBlockBox.Min.Y) : "N/A",
                // relX, relY,
                FDir(testDir, true), FDir(testDir, false),
                note);

            log.Add(row);
        }

        // Small helper purely so LogRow can resolve the viewport type's display name
        // without needing a separate Document parameter threaded through every call.
        private static string GetElementName(View view, ElementId id) =>
            view.Document.GetElement(id)?.Name ?? "Unknown";
    }
}
