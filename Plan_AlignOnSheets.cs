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
    // Aligns selected plan viewports on other sheets to the setting-out of a source
    // plan viewport, using the Project Base Point as the shared model-space reference.
    //
    // The PBP is projected onto the sheet with Revit's own transform chain:
    //     sheetXYZ = Viewport.GetProjectionToSheetTransform()
    //              * View.GetModelToProjectionTransforms()[region] * modelXYZ
    // and each target viewport is shifted by the difference to the source.
    [Transaction(TransactionMode.Manual)]
    public class Plan_AlignOnSheets : IExternalCommand
    {
        private const double MmPerFt = 304.8;

        // Second model point (PBP + this distance along X) used only to derive each
        // plan's sheet-space rotation.
        private const double RotationTestDistanceFt = 50.0;

        // Rotations are compared as |dot| of the two directions (~0.06 degrees tolerance).
        internal const double RotationAlignedDotThreshold = 0.999999;

        private const double ResidualFlagThresholdMm = 1.0;
        private const double OffSheetToleranceMm = 0.5;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2026-10-22. Beta 0.17";

            List<string> log = new List<string>();

            try
            {
                BasePoint projectBasePoint = BasePoint.GetProjectBasePoint(doc);
                if (projectBasePoint == null)
                {
                    new Warning("Oops...", "This project has no Project Base Point — alignment needs it as a shared reference point and can't proceed without it.", credit).ShowDialog();
                    return Result.Cancelled;
                }
                XYZ pbp = projectBasePoint.Position;

                List<VM_PlanViewport> allPlans = CollectQualifyingPlans(doc);
                if (allPlans.Count == 0)
                {
                    new Warning("Oops...", "No sheets with a Floor Plan, Ceiling Plan, Area Plan, or Structural Plan viewport were found.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Rotation is measured up front so the dialog can grey out incompatible plans.
                foreach (VM_PlanViewport plan in allPlans)
                    plan.RotationDirection = Measure(plan.PlanView, plan.Viewport, pbp).Direction;

                // Pre-select the largest plan on the active sheet; other plans on it
                // (e.g. a key plan) stay available in the source combo.
                VM_PlanViewport preSelectSource = null;
                if (doc.ActiveView is ViewSheet activeSheet)
                {
                    preSelectSource = allPlans
                        .Where(p => p.Sheet.Id == activeSheet.Id)
                        .OrderByDescending(p => p.PaperAreaSqFt)
                        .FirstOrDefault();
                }

                Selection_PlanAlign dlg = new Selection_PlanAlign(allPlans, preSelectSource, credit, doc.ActiveView is ViewSheet);
                if (dlg.ShowDialog() != true)
                    return Result.Cancelled;

                VM_PlanViewport source = dlg.SelectedSource;
                List<VM_PlanViewport> targets = dlg.SelectedTargets
                    .Where(t => t.Viewport.Id != source.Viewport.Id)
                    .ToList();

                if (targets.Count == 0)
                {
                    new Warning("Oops...", "No target plans were selected.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                log.Add("Plan Alignment — Log");
                log.Add($"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                log.Add($"Project: {doc.Title}");
                log.Add($"Source: {source.ComboLabel}");
                log.Add($"Project Base Point (model, mm): X={pbp.X * MmPerFt:F2}, Y={pbp.Y * MmPerFt:F2}, Z={pbp.Z * MmPerFt:F2}");
                log.Add(new string('-', 70));
                log.Add("");
                log.Add("=== Measurements (tab-separated; paste into Excel) ===");
                log.Add(DiagnosticHeader());

                Measurement sourceMeasurement = Measure(source.PlanView, source.Viewport, pbp);
                LogRow(log, source.ComboLabel, "SOURCE", source, sourceMeasurement);
                if (!sourceMeasurement.Success)
                {
                    ShowFailure($"Could not measure the source plan's setting-out: {sourceMeasurement.Note}", log, credit);
                    return Result.Failed;
                }

                List<string> issues = new List<string>();
                int alignedCount = 0;

                // Each target takes on the source's viewport type and title position.
                ElementId sourceTypeId = source.Viewport.GetTypeId();
                XYZ sourceTitleAnchor = GetTitleAnchor(source.Viewport, out string titleAnchorNote);
                if (sourceTitleAnchor == null)
                {
                    log.Add($"Note: titles will not be aligned — {titleAnchorNote}.");
                    issues.Add($"Titles not aligned — {titleAnchorNote}");
                }

                // Ownership is checked up front: plans owned by someone else are
                // reported at the end, everything else proceeds.
                log.Add("");
                log.Add("=== Alignment ===");
                List<VM_PlanViewport> movable = new List<VM_PlanViewport>();
                foreach (VM_PlanViewport target in targets)
                {
                    string blocker = GetEditBlocker(doc, target);
                    if (blocker != null)
                    {
                        log.Add($"  ✗ {target.ComboLabel}  — not moved: {blocker}.");
                        issues.Add($"{target.ComboLabel}: not moved — {blocker}");
                    }
                    else
                    {
                        movable.Add(target);
                    }
                }

                if (movable.Count > 0)
                {
                    using (Transaction t = new Transaction(doc, "LW_Align Plans on Sheets"))
                    {
                        t.Start();

                        foreach (VM_PlanViewport target in movable)
                        {
                            string label = target.ComboLabel;

                            try
                            {
                                // Defensive: the dialog already greys these out.
                                string incompatible = target.GetIncompatibility(source);
                                if (incompatible != null)
                                {
                                    log.Add($"  ⚠ {label}  — skipped: {incompatible}.");
                                    issues.Add($"{label}: skipped — {incompatible}");
                                    continue;
                                }

                                MatchViewportType(doc, target, sourceTypeId, log, issues);

                                Measurement before = Measure(target.PlanView, target.Viewport, pbp);
                                LogRow(log, label, "TARGET-BEFORE", target, before);
                                if (!before.Success)
                                {
                                    log.Add($"  ✗ {label}  — error: {before.Note}");
                                    issues.Add($"{label}: {before.Note}");
                                    continue;
                                }

                                XYZ delta = sourceMeasurement.PaperPoint - before.PaperPoint;

                                XYZ oldCenter = target.Viewport.GetBoxCenter();
                                target.Viewport.SetBoxCenter(new XYZ(oldCenter.X + delta.X, oldCenter.Y + delta.Y, 0));
                                doc.Regenerate();

                                Measurement after = Measure(target.PlanView, target.Viewport, pbp);
                                LogRow(log, label, "TARGET-AFTER", target, after);
                                if (!after.Success)
                                {
                                    log.Add($"  ✗ {label}  — moved, but could not re-measure afterward: {after.Note}");
                                    issues.Add($"{label}: moved, but could not re-measure — {after.Note}");
                                    continue;
                                }

                                XYZ residual = after.PaperPoint - sourceMeasurement.PaperPoint;
                                double residualMm = Math.Sqrt(residual.X * residual.X + residual.Y * residual.Y) * MmPerFt;
                                log.Add($"    Residual vs source: dX={residual.X * MmPerFt:F2} mm, dY={residual.Y * MmPerFt:F2} mm (magnitude {residualMm:F2} mm)");
                                log.Add($"  ✅ {label}  — shifted by (X={delta.X * MmPerFt:F1} mm, Y={delta.Y * MmPerFt:F1} mm) on paper.");
                                alignedCount++;

                                if (residualMm > ResidualFlagThresholdMm)
                                    issues.Add($"{label}: residual gap {residualMm:F2} mm after alignment");

                                if (sourceTitleAnchor != null)
                                    AlignTitle(doc, source.Viewport, target, sourceTitleAnchor, log, issues);

                                string overhang = DescribeOverhang(target.Sheet, target.Viewport);
                                if (overhang != null)
                                {
                                    log.Add($"    ⚠ viewport now extends beyond the sheet: {overhang}.");
                                    issues.Add($"{label}: now extends beyond the sheet — {overhang}");
                                }
                            }
                            catch (Exception ex)
                            {
                                log.Add($"  ✗ {label}  — error: {ex.Message}");
                                issues.Add($"{label}: {ex.Message}");
                            }
                        }

                        t.Commit();
                    }

                    // Regenerate already ran inside the transaction; what goes stale is the
                    // display of sheets that are open but not active, so redraw every open view.
                    uiDoc.UpdateAllOpenViews();
                    uiDoc.RefreshActiveView();
                }

                log.Add("");
                log.Add($"{alignedCount} of {targets.Count} plan(s) aligned.");
                if (issues.Count > 0)
                {
                    log.Add("");
                    log.Add($"=== {issues.Count} issues ===");
                    log.AddRange(issues.Select(i => $"  {i}"));
                }

                string logPath = WriteLog(log, out string logError);

                StringBuilder summary = new StringBuilder();
                summary.AppendLine($"{alignedCount} of {targets.Count} plan(s) aligned to \"{source.ComboLabel}\".");
                if (issues.Count > 0)
                    summary.AppendLine($"{issues.Count} issues founded - see log");
                if (logError != null)
                    summary.AppendLine($"\nLog FAILED to write: {logError}");

                new WarningLarge("Align Plans Complete", summary.ToString(), credit,
                    revealPath: (logError == null && issues.Count > 0) ? logPath : null).ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                log.Add("");
                log.Add(ex.ToString());
                ShowFailure($"An error occurred: {ex.Message}", log, credit);
                return Result.Failed;
            }
        }

        // ---------------- Logging / warnings ----------------

        private static string WriteLog(List<string> log, out string error)
        {
            string path = Path.Combine(Path.GetTempPath(), $"Plan_AlignOnSheets_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            error = null;
            try
            {
                File.WriteAllLines(path, log);
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static void ShowFailure(string text, List<string> log, string credit)
        {
            log.Add("");
            log.Add($"FAILED: {text}");
            string path = WriteLog(log, out string writeError);
            new Warning("Oops...", text, credit, revealPath: writeError == null ? path : null).ShowDialog();
        }

        // ---------------- Collection ----------------

        // Structural Plan views are reported by the API as ViewType.EngineeringPlan.
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
                    if (!QualifyingPlanTypes.Contains(v.ViewType)) continue;

                    result.Add(new VM_PlanViewport(sheet, vp, v));
                }
            }

            return result;
        }

        // ---------------- Ownership ----------------

        // Returns why this plan's viewport (or its sheet) can't be edited right now —
        // owned by another user, or changed in the central model since the last sync —
        // or null when it can (always null in a non-workshared project).
        private static string GetEditBlocker(Document doc, VM_PlanViewport plan)
        {
            if (!doc.IsWorkshared) return null;

            try
            {
                foreach (Element element in new Element[] { plan.Viewport, plan.Sheet })
                {
                    string what = element is Viewport ? "viewport" : "sheet";

                    CheckoutStatus status = WorksharingUtils.GetCheckoutStatus(doc, element.Id, out string owner);
                    if (status == CheckoutStatus.OwnedByOtherUser)
                        return $"the {what} is owned by {(string.IsNullOrEmpty(owner) ? "another user" : owner)}";

                    if (WorksharingUtils.GetModelUpdatesStatus(doc, element.Id) == ModelUpdatesStatus.UpdatedInCentral)
                        return $"the {what} was changed in the central model — reload latest first";
                }
            }
            catch
            {
                // Status unavailable — don't block; a real failure is caught and logged when the move is attempted.
            }

            return null;
        }

        // ---------------- Viewport type and title ----------------

        // Switches the target viewport to the source's viewport type so the title
        // symbol matches. A failure is logged and reported; alignment carries on.
        private static void MatchViewportType(Document doc, VM_PlanViewport target, ElementId sourceTypeId, List<string> log, List<string> issues)
        {
            Viewport vp = target.Viewport;
            if (vp.GetTypeId() == sourceTypeId) return;

            string oldName = doc.GetElement(vp.GetTypeId())?.Name ?? "Unknown";
            string newName = doc.GetElement(sourceTypeId)?.Name ?? "Unknown";

            try
            {
                if (!vp.GetValidTypes().Contains(sourceTypeId))
                {
                    log.Add($"    ⚠ viewport type \"{newName}\" is not valid for this viewport; left as \"{oldName}\".");
                    issues.Add($"{target.ComboLabel}: viewport type \"{newName}\" is not valid for this viewport");
                    return;
                }

                ElementId changedId = vp.ChangeTypeId(sourceTypeId);
                if (changedId != null && changedId != ElementId.InvalidElementId && changedId != vp.Id
                    && doc.GetElement(changedId) is Viewport replaced)
                {
                    target.Viewport = replaced;
                }

                doc.Regenerate();
                log.Add($"    Viewport type: \"{oldName}\" → \"{newName}\".");
            }
            catch (Exception ex)
            {
                log.Add($"    ⚠ could not change viewport type to \"{newName}\": {ex.Message}");
                issues.Add($"{target.ComboLabel}: could not change viewport type to \"{newName}\" — {ex.Message}");
            }
        }

        // Sheet position of the left end of a viewport's title line: Viewport.LabelOffset
        // is a 2D vector from the viewport's bottom-left corner to that point (only valid
        // for an unrotated viewport). Returns null, with the reason, when unavailable.
        private static XYZ GetTitleAnchor(Viewport vp, out string note)
        {
            note = null;
            try
            {
                if (vp.Rotation != ViewportRotation.None)
                {
                    note = "the source viewport is rotated on its sheet";
                    return null;
                }

                XYZ boxMin = vp.GetBoxOutline().MinimumPoint;
                XYZ offset = vp.LabelOffset;
                return new XYZ(boxMin.X + offset.X, boxMin.Y + offset.Y, 0);
            }
            catch (Exception ex)
            {
                note = $"the source title position could not be read ({ex.Message})";
                return null;
            }
        }

        // Moves the target's title so its line starts at the source's title position on the sheet.
        private static void AlignTitle(Document doc, Viewport sourceVp, VM_PlanViewport target, XYZ sourceAnchor, List<string> log, List<string> issues)
        {
            try
            {
                Viewport vp = target.Viewport;
                if (vp.Rotation != ViewportRotation.None)
                {
                    log.Add("    ⚠ title not aligned — the viewport is rotated on its sheet.");
                    issues.Add($"{target.ComboLabel}: title not aligned — viewport is rotated on its sheet");
                    return;
                }

                XYZ boxMin = vp.GetBoxOutline().MinimumPoint;
                XYZ newOffset = new XYZ(sourceAnchor.X - boxMin.X, sourceAnchor.Y - boxMin.Y, 0);
                XYZ oldOffset = vp.LabelOffset;

                vp.LabelOffset = newOffset;
                doc.Regenerate();

                log.Add($"    Title moved by (X={(newOffset.X - oldOffset.X) * MmPerFt:F1} mm, Y={(newOffset.Y - oldOffset.Y) * MmPerFt:F1} mm) to match the source.");

                // Informational only: label outlines also depend on title text, so a
                // small difference here is expected when the view names differ.
                XYZ sourceLabelMin = sourceVp.GetLabelOutline().MinimumPoint;
                XYZ targetLabelMin = vp.GetLabelOutline().MinimumPoint;
                log.Add($"    Title outline corner vs source: dX={(targetLabelMin.X - sourceLabelMin.X) * MmPerFt:F2} mm, dY={(targetLabelMin.Y - sourceLabelMin.Y) * MmPerFt:F2} mm");
            }
            catch (Exception ex)
            {
                log.Add($"    ⚠ could not align the title: {ex.Message}");
                issues.Add($"{target.ComboLabel}: could not align the title — {ex.Message}");
            }
        }

        // ---------------- Off-sheet check ----------------

        // Describes how far the viewport's box extends past the sheet edges
        // (e.g. "left 12.0 mm, top 3.5 mm"), or null when it is within the sheet.
        private static string DescribeOverhang(ViewSheet sheet, Viewport vp)
        {
            try
            {
                BoundingBoxUV sheetBox = sheet.Outline;
                if (sheetBox.Max.U - sheetBox.Min.U <= 0 || sheetBox.Max.V - sheetBox.Min.V <= 0) return null;

                Outline box = vp.GetBoxOutline();
                double tolerance = OffSheetToleranceMm / MmPerFt;

                List<string> sides = new List<string>();
                double left = sheetBox.Min.U - box.MinimumPoint.X;
                double right = box.MaximumPoint.X - sheetBox.Max.U;
                double bottom = sheetBox.Min.V - box.MinimumPoint.Y;
                double top = box.MaximumPoint.Y - sheetBox.Max.V;

                if (left > tolerance) sides.Add($"left {left * MmPerFt:F1} mm");
                if (right > tolerance) sides.Add($"right {right * MmPerFt:F1} mm");
                if (bottom > tolerance) sides.Add($"bottom {bottom * MmPerFt:F1} mm");
                if (top > tolerance) sides.Add($"top {top * MmPerFt:F1} mm");

                return sides.Count == 0 ? null : string.Join(", ", sides);
            }
            catch
            {
                return null;
            }
        }

        // ---------------- Measurement ----------------

        private class Measurement
        {
            public bool Success;
            public string Note;
            public int RegionCount;
            public XYZ PaperPoint;  // PBP on the sheet
            public XYZ Direction;   // sheet-space direction of model +X; for the rotation comparison only
        }

        private static Measurement Measure(View view, Viewport vp, XYZ pbp)
        {
            Measurement m = new Measurement();

            try
            {
                if (!view.HasViewTransforms() || !vp.HasViewportTransforms())
                {
                    m.Note = "This view/viewport does not report model-to-sheet transforms.";
                    return m;
                }

                Transform projToSheet = vp.GetProjectionToSheetTransform();
                IList<TransformWithBoundary> transforms = view.GetModelToProjectionTransforms();
                if (transforms == null || transforms.Count == 0)
                {
                    m.Note = "No model-to-projection transforms were returned for this view.";
                    return m;
                }

                XYZ testPoint = pbp + XYZ.BasisX * RotationTestDistanceFt;
                XYZ sheetPbp = ProjectThroughRegion(ChooseRegion(transforms, pbp), projToSheet, pbp);
                XYZ sheetTest = ProjectThroughRegion(ChooseRegion(transforms, testPoint), projToSheet, testPoint);

                XYZ direction = sheetTest - sheetPbp;
                direction = direction.IsZeroLength() ? XYZ.BasisX : direction.Normalize();

                m.RegionCount = transforms.Count;
                m.PaperPoint = new XYZ(sheetPbp.X, sheetPbp.Y, 0);
                m.Direction = direction;
                m.Success = true;
                m.Note = "OK";
            }
            catch (Exception ex)
            {
                m.Note = $"Could not measure: {ex.Message}";
            }

            return m;
        }

        // A view's crop can be split into disjoint regions, each with its own transform
        // and model-space boundary; pick the one containing the point. Falls back to the
        // first region (also used when a region has no boundary, i.e. a simple crop).
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
            XYZ projectionPoint = region.GetModelToProjectionTransform().OfPoint(modelPoint);
            return projToSheet.OfPoint(projectionPoint);
        }

        // Parity (even/odd) ray-crossing test for a planar 3D CurveLoop, as in the
        // Revit SDK "SheetToView3D" sample.
        private static bool IsPointInsideCurveLoop(XYZ point, CurveLoop curveLoop)
        {
            List<Curve> curves = curveLoop.ToList();
            if (curves.Count < 3) return true;

            Plane plane;
            try { plane = Plane.CreateByThreePoints(curves[0].GetEndPoint(0), curves[1].GetEndPoint(0), curves[2].GetEndPoint(0)); }
            catch { return true; }

            plane.Project(point, out UV uv, out _);
            XYZ projectedPoint = plane.Origin + plane.XVec * uv.U + plane.YVec * uv.V;

            const double randomXScale = 5631, randomYScale = 4369; // arbitrary, non-axis-aligned
            Line veryLongLine = Line.CreateBound(projectedPoint, projectedPoint + randomXScale * plane.XVec + randomYScale * plane.YVec);

            int intersectionCount = 0;
            foreach (Curve edge in curves)
            {
                SetComparisonResult res = veryLongLine.Intersect(edge, out IntersectionResultArray resultArray);
                if (res == SetComparisonResult.Overlap) intersectionCount += resultArray.Size;
            }

            return (intersectionCount % 2) == 1;
        }

        // ---------------- Measurement log ----------------

        private static string DiagnosticHeader() => string.Join("\t",
            "Label", "Stage", "SheetNumber", "ViewName", "ViewportType", "ViewScale", "TransformRegions",
            "SheetX_mm", "SheetY_mm", "TestDirX", "TestDirY", "Note");

        private static void LogRow(List<string> log, string label, string stage, VM_PlanViewport plan, Measurement m)
        {
            string viewportType = plan.Viewport.Document.GetElement(plan.Viewport.GetTypeId())?.Name ?? "Unknown";

            string Mm(XYZ p, bool useX) => p == null ? "N/A" : ((useX ? p.X : p.Y) * MmPerFt).ToString("F2");
            string Dir(XYZ v, bool useX) => v == null ? "N/A" : (useX ? v.X : v.Y).ToString("F6");

            log.Add(string.Join("\t",
                label, stage, plan.SheetNumber, plan.ViewName, viewportType, plan.Scale.ToString(), m.RegionCount.ToString(),
                Mm(m.PaperPoint, true), Mm(m.PaperPoint, false), Dir(m.Direction, true), Dir(m.Direction, false),
                m.Note));
        }
    }
}
