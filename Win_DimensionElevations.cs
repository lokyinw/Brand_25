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
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Win_DimensionElevations : IExternalCommand
    {
        // mm -> feet (Revit's internal length unit)
        private const double MmToFt = 1.0 / 304.8;

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
                // Step 1: elevation view type selection (existing dialog)
                List<ViewFamilyType> elevViewTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .Where(vft => vft.ViewFamily == ViewFamily.Elevation)
                    .ToList();

                if (elevViewTypes.Count == 0)
                {
                    TaskDialog.Show("Error", "No elevation view types found in the document.");
                    return Result.Cancelled;
                }

                Selection_ElevType elevTypeWindow = new Selection_ElevType(elevViewTypes, credit);
                if (elevTypeWindow.ShowDialog() != true || elevTypeWindow.SelectedElevType == null)
                {
                    return Result.Cancelled;
                }
                ViewFamilyType selectedElevType = elevTypeWindow.SelectedElevType;

                List<View> elevViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewType == ViewType.Elevation && v.GetTypeId() == selectedElevType.Id)
                    .ToList();

                if (elevViews.Count == 0)
                {
                    new Warning("Oops...", "No elevation views of the selected type were found.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 2: dimension line distances
                Selection_DimDistance distWindow = new Selection_DimDistance(credit);
                if (distWindow.ShowDialog() != true)
                {
                    return Result.Cancelled;
                }
                double distA = distWindow.Distance1Mm * MmToFt; // closest ring: overall width / height
                double distB = distWindow.Distance2Mm * MmToFt; // mid ring: height tied to Level (when grids present)
                double distC = distWindow.Distance3Mm * MmToFt; // outer ring: curtain grid width / door width tie

                // Step 3: gather candidate curtain walls & doors once up front
                List<Wall> allCurtainWalls = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .Where(w => w.CurtainGrid != null)
                    .ToList();

                List<FamilyInstance> allDoors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                int wallElevCount = 0, doorElevCount = 0, skippedCount = 0;

                using (Transaction trans = new Transaction(doc, "LW_Dimension Window Elevations"))
                {
                    trans.Start();

                    foreach (View view in elevViews)
                    {
                        string viewName = view.Name;

                        List<Wall> matchingWalls = allCurtainWalls
                            .Where(w => GetMark(w) == viewName)
                            .ToList();

                        if (matchingWalls.Count > 1)
                        {
                            string msg = $"View '{viewName}': found {matchingWalls.Count} curtain walls with Mark = view name — skipped.";
                            log.AppendLine("Warning: " + msg);
                            issues.Add(msg);
                            skippedCount++;
                            continue;
                        }

                        if (matchingWalls.Count == 1)
                        {
                            try
                            {
                                DimensionCurtainWallElevation(doc, view, matchingWalls[0], distA, distB, distC, log, issues);
                                wallElevCount++;
                            }
                            catch (Exception ex)
                            {
                                string msg = $"View '{viewName}': error dimensioning curtain wall elevation — {ex.Message}";
                                log.AppendLine("Error: " + msg);
                                issues.Add(msg);
                            }
                            continue;
                        }

                        List<FamilyInstance> matchingDoors = allDoors
                            .Where(d => GetMark(d) == viewName)
                            .ToList();

                        if (matchingDoors.Count > 1)
                        {
                            string msg = $"View '{viewName}': found {matchingDoors.Count} doors with Mark = view name — skipped.";
                            log.AppendLine("Warning: " + msg);
                            issues.Add(msg);
                            skippedCount++;
                            continue;
                        }

                        if (matchingDoors.Count == 1)
                        {
                            try
                            {
                                DimensionDoorOnlyElevation(doc, view, matchingDoors[0], distA);
                                doorElevCount++;
                            }
                            catch (Exception ex)
                            {
                                string msg = $"View '{viewName}': error dimensioning door elevation — {ex.Message}";
                                log.AppendLine("Error: " + msg);
                                issues.Add(msg);
                            }
                            continue;
                        }

                        string noMatchMsg = $"View '{viewName}': no curtain wall or door found with Mark = view name — skipped.";
                        log.AppendLine("Error: " + noMatchMsg);
                        issues.Add(noMatchMsg);
                        skippedCount++;
                    }

                    trans.Commit();
                }

                string summary = $"{wallElevCount} curtain wall elevation(s) and {doorElevCount} door elevation(s) dimensioned.";
                if (skippedCount > 0) summary += $"\n{skippedCount} elevation(s) skipped — see log for details.";

                new Warning(issues.Count > 0 ? "Completed with issues" : "Success", summary, credit).ShowDialog();

                log.AppendLine();
                log.AppendLine(summary);
                if (issues.Count > 0)
                {
                    log.AppendLine();
                    log.AppendLine($"=== {issues.Count} Issue(s) ===");
                    foreach (string issue in issues) log.AppendLine(issue);
                }

                string logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowDimensionLog.txt");
                try { File.WriteAllText(logFilePath, log.ToString()); } catch { /* logging is best-effort */ }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}\n\nDebug Log:\n{log}");
                return Result.Failed;
            }
        }

        private static string GetMark(Element e)
        {
            Parameter p = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            return p != null && p.HasValue ? p.AsString() : null;
        }

        // -----------------------------------------------------------------
        // Curtain wall elevation
        // -----------------------------------------------------------------
        private void DimensionCurtainWallElevation(Document doc, View view, Wall wall, double distA, double distB, double distC,
            StringBuilder log, List<string> issues)
        {
            CurtainGrid grid = wall.CurtainGrid;

            LocationCurve locCurve = wall.Location as LocationCurve;
            if (locCurve == null) throw new InvalidOperationException("Curtain wall has no location curve (curved/complex walls are not supported).");
            XYZ wallDir = (locCurve.Curve.GetEndPoint(1) - locCurve.Curve.GetEndPoint(0)).Normalize();

            Options opt = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = true,
                View = view
            };

            Level level = doc.GetElement(wall.LevelId) as Level;

            ReferenceArray eRefH = new ReferenceArray(); // bottom/top faces, plus (below) any U-grid (horizontal) lines
            ReferenceArray eRefV = new ReferenceArray(); // side (end) faces only
            ReferenceArray eH = new ReferenceArray();    // Level + bottom/top faces
            ReferenceArray eV = new ReferenceArray();    // side faces, plus (below) any V-grid (vertical) lines

            if (level != null) eH.Append(level.GetPlaneReference());

            CurveLoop profileLoop = null;

            foreach (GeometryObject obj in wall.get_Geometry(opt))
            {
                if (!(obj is Solid solid) || solid.Faces.IsEmpty) continue;

                foreach (Face f in solid.Faces)
                {
                    if (!(f is PlanarFace pf)) continue;
                    XYZ n = pf.FaceNormal;
                    double nx = Math.Round(n.X, 4), ny = Math.Round(n.Y, 4), nz = Math.Round(n.Z, 4);

                    if (Math.Abs(nz) == 1)
                    {
                        // Top / bottom face of the curtain wall
                        eRefH.Append(pf.Reference);
                        eH.Append(pf.Reference);
                    }
                    else if (IsParallelInPlan(nx, ny, wallDir))
                    {
                        // Left / right end face of the curtain wall
                        eRefV.Append(pf.Reference);
                        eV.Append(pf.Reference);
                    }
                    else
                    {
                        // Remaining large planar face(s): the wall's elevation "profile" face
                        // (front and back both qualify and give the same in-plane loop shape).
                        IList<CurveLoop> loops = pf.GetEdgesAsCurveLoops();
                        if (loops.Count > 0) profileLoop = loops[0];
                    }
                }
            }

            if (profileLoop == null)
                throw new InvalidOperationException("Could not find the curtain wall's elevation profile face in this view.");

            List<ElementId> uGridIds = grid.GetUGridLineIds().ToList();
            List<ElementId> vGridIds = grid.GetVGridLineIds().ToList();

            foreach (ElementId id in uGridIds)
            {
                Element gridLine = doc.GetElement(id);
                foreach (GeometryObject g in gridLine.get_Geometry(opt))
                {
                    if (g is Curve c && c.Reference != null) eRefH.Append(c.Reference);
                }
            }
            foreach (ElementId id in vGridIds)
            {
                Element gridLine = doc.GetElement(id);
                foreach (GeometryObject g in gridLine.get_Geometry(opt))
                {
                    if (g is Curve c && c.Reference != null) eV.Append(c.Reference);
                }
            }

            CurveLoop loopA = CurveLoop.CreateViaOffset(profileLoop, distA * view.Scale, XYZ.BasisZ);
            CurveLoop loopB = CurveLoop.CreateViaOffset(profileLoop, distB * view.Scale, XYZ.BasisZ);
            CurveLoop loopC = CurveLoop.CreateViaOffset(profileLoop, distC * view.Scale, XYZ.BasisZ);

            List<Curve> originalCurves = profileLoop.ToList();
            List<Curve> curvesA = loopA.ToList();
            List<Curve> curvesB = loopB.ToList();
            List<Curve> curvesC = loopC.ToList();

            double maxZ = originalCurves.SelectMany(c => new[] { c.GetEndPoint(0).Z, c.GetEndPoint(1).Z }).Max();
            int topIndex = -1;
            for (int i = 0; i < originalCurves.Count; i++)
            {
                XYZ p0 = originalCurves[i].GetEndPoint(0);
                XYZ p1 = originalCurves[i].GetEndPoint(1);
                if (Math.Abs(p0.Z - p1.Z) < 1e-6 && Math.Abs(p0.Z - maxZ) < 1e-6)
                {
                    topIndex = i;
                    break;
                }
            }
            if (topIndex < 0) throw new InvalidOperationException("Could not identify the top edge of the curtain wall's profile.");
            int sideIndex = (topIndex - 1 + originalCurves.Count) % originalCurves.Count;

            Line lineH = curvesA[topIndex] as Line;   // width dim line, closest ring
            Line lineV = curvesA[sideIndex] as Line;  // height dim line, closest ring
            Line lineH2 = curvesC[topIndex] as Line;  // width dim line, outer ring (grid ticks / door width)
            Line lineV2 = curvesB[sideIndex] as Line; // height dim line, mid ring (level tie)

            if (lineH == null || lineV == null || lineH2 == null || lineV2 == null)
                throw new InvalidOperationException("The curtain wall's elevation profile is not a straight-sided rectangle; this tool only supports rectangular profiles.");

            bool hasBaseOffset = (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0) != 0;
            bool hasUGrids = uGridIds.Count >= 1;
            bool hasVGrids = vGridIds.Count >= 1;

            // --- Height ---
            // NOTE: this branch mirrors the reference Dynamo graph as-is. Flagged by the user as
            // "not sure about this one yet" — expect this logic to be adjusted after reviewing results.
            if (hasBaseOffset)
            {
                if (hasUGrids)
                {
                    doc.Create.NewDimension(view, lineV, eRefH);  // segmented by U-grid lines
                    doc.Create.NewDimension(view, lineV2, eH);    // overall, tied to Level
                }
                else
                {
                    doc.Create.NewDimension(view, lineV, eH);     // overall, tied to Level
                }
            }
            else
            {
                doc.Create.NewDimension(view, lineV, eRefH);      // sill sits at Level already; no separate Level tie needed
            }

            // --- Width ---
            if (hasVGrids)
            {
                doc.Create.NewDimension(view, lineH, eRefV);      // overall width
                doc.Create.NewDimension(view, lineH2, eV);        // segmented by V-grid lines
            }
            else
            {
                doc.Create.NewDimension(view, lineH, eV);         // overall width
            }

            // --- Doors hosted in this curtain wall ---
            List<FamilyInstance> hostedDoors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .Where(d => d.Host != null && d.Host.Id == wall.Id)
                .ToList();

            foreach (FamilyInstance door in hostedDoors)
            {
                try
                {
                    DimensionDoorInCurtainWall(doc, view, door, wallDir, level, lineH2, distA);
                }
                catch (Exception ex)
                {
                    string msg = $"View '{view.Name}': error dimensioning door (ID {door.Id}) hosted in curtain wall — {ex.Message}";
                    log.AppendLine("Error: " + msg);
                    issues.Add(msg);
                }
            }
        }

        private static bool IsParallelInPlan(double nx, double ny, XYZ dir)
        {
            double dx = Math.Round(dir.X, 4), dy = Math.Round(dir.Y, 4);
            return (Math.Abs(nx - dx) < 1e-3 && Math.Abs(ny - dy) < 1e-3) ||
                   (Math.Abs(nx + dx) < 1e-3 && Math.Abs(ny + dy) < 1e-3);
        }

        // Door hosted in a curtain wall: width tied to Left/Right (+Centre) reference planes,
        // reusing the curtain wall's grid-width dimension line; height tied to Level + Top.
        private void DimensionDoorInCurtainWall(Document doc, View view, FamilyInstance door, XYZ wallDir, Level level, Line widthDimLine, double distA)
        {
            BoundingBoxXYZ bbox = door.get_BoundingBox(view);
            if (bbox == null) throw new InvalidOperationException("Door has no bounding box in this view.");
            XYZ center = bbox.Min + (bbox.Max - bbox.Min) * 0.5;

            XYZ crossDir = wallDir.CrossProduct(XYZ.BasisZ);

            Reference leftRef = door.GetReferenceByName("Left");
            Reference rightRef = door.GetReferenceByName("Right");
            Reference topRef = door.GetReferenceByName("Top");
            Reference centreRef = door.GetReferenceByName("Centre");

            if (leftRef == null || rightRef == null || topRef == null)
                throw new InvalidOperationException("Door family is missing the Left/Right/Top named reference planes.");

            // Width — placed on the same offset line used for the curtain grid's width dimension.
            ReferenceArray widthRefs = new ReferenceArray();
            widthRefs.Append(leftRef);
            widthRefs.Append(rightRef);
            if (centreRef != null) widthRefs.Append(centreRef);

            Dimension widthDim = doc.Create.NewDimension(view, widthDimLine, widthRefs);
            if (centreRef != null)
            {
                foreach (DimensionSegment seg in widthDim.Segments) seg.Below = "Door Width";
            }
            else
            {
                widthDim.Below = "Door Width";
            }

            // Height — a vertical line offset to the side of the door, tied to Level + Top.
            Line heightLine = (Line)Line.CreateUnbound(center, XYZ.BasisZ).CreateOffset(distA * view.Scale, crossDir);
            ReferenceArray heightRefs = new ReferenceArray();
            if (level != null) heightRefs.Append(level.GetPlaneReference());
            heightRefs.Append(topRef);

            Dimension heightDim = doc.Create.NewDimension(view, heightLine, heightRefs);
            heightDim.Below = "Door Height";
        }

        // -----------------------------------------------------------------
        // Door-only elevation (no wall matched to the view)
        // -----------------------------------------------------------------
        private void DimensionDoorOnlyElevation(Document doc, View view, FamilyInstance door, double distA)
        {
            Wall hostWall = door.Host as Wall;
            Level level = hostWall != null ? doc.GetElement(hostWall.LevelId) as Level : null;
            if (level == null && door.LevelId != ElementId.InvalidElementId)
                level = doc.GetElement(door.LevelId) as Level;
            if (level == null) throw new InvalidOperationException("Could not determine a level for this door.");

            XYZ wallDir = (hostWall?.Location as LocationCurve) is LocationCurve lc
                ? (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0)).Normalize()
                : XYZ.BasisX;
            XYZ crossDir = wallDir.CrossProduct(XYZ.BasisZ);

            BoundingBoxXYZ bbox = door.get_BoundingBox(view);
            if (bbox == null) throw new InvalidOperationException("Door has no bounding box in this view.");
            XYZ center = bbox.Min + (bbox.Max - bbox.Min) * 0.5;

            Reference leftRef = door.GetReferenceByName("Left");
            Reference rightRef = door.GetReferenceByName("Right");
            Reference topRef = door.GetReferenceByName("Top");
            if (leftRef == null || rightRef == null || topRef == null)
                throw new InvalidOperationException("Door family is missing the Left/Right/Top named reference planes.");

            // Width — horizontal line offset above the door, tied to Left/Right.
            Line widthLine = (Line)Line.CreateUnbound(center, crossDir).CreateOffset(distA * view.Scale, XYZ.BasisZ);
            ReferenceArray widthRefs = new ReferenceArray();
            widthRefs.Append(leftRef);
            widthRefs.Append(rightRef);
            doc.Create.NewDimension(view, widthLine, widthRefs);

            // Height — vertical line offset to the side of the door, tied to Level + Top.
            Line heightLine = (Line)Line.CreateUnbound(center, XYZ.BasisZ).CreateOffset(distA * view.Scale, crossDir);
            ReferenceArray heightRefs = new ReferenceArray();
            heightRefs.Append(level.GetPlaneReference());
            heightRefs.Append(topRef);
            doc.Create.NewDimension(view, heightLine, heightRefs);
        }
    }
}
