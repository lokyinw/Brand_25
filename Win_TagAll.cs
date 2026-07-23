using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Brand_25
{
    // Tags every curtain wall (BA_TypeFilter = "CW") and every door visible in the
    // active view. Tag types are picked once via Selection_TagTypes.xaml — the same
    // dialog (and same default-type lookup) already used by Win_CreateElevations.cs —
    // so all Windows-panel commands agree on how a "window tag" / "door tag" is chosen.
    [Transaction(TransactionMode.Manual)]
    public class Win_TagAll : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;
            string credit = "Last Modified by Lok on 2026-07-23. Beta 0.10";

            if (activeView is View3D)
            {
                new Warning("Oops...", "Tags can't be placed in a 3D view.\nSwitch to a plan, elevation, or section view and try again.", credit).ShowDialog();
                return Result.Cancelled;
            }

            try
            {
                // Step 1: collect candidate curtain walls (windows) and doors visible in
                // the active view. Using a view-scoped collector (rather than a document-
                // wide one) means view-specific visibility (VG overrides, filters, etc.)
                // is respected automatically; IsHidden() below additionally catches
                // elements individually hidden in this view.
                List<Wall> curtainWalls = new FilteredElementCollector(doc, activeView.Id)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .Where(w => IsCurtainWallWindow(doc, w))
                    .Where(w => !w.IsHidden(activeView))
                    .ToList();

                List<FamilyInstance> doors = new FilteredElementCollector(doc, activeView.Id)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Where(d => !d.IsHidden(activeView))
                    .ToList();

                if (curtainWalls.Count == 0 && doors.Count == 0)
                {
                    new Warning("Oops...", "No curtain walls (BA_TypeFilter = \"CW\") or doors were found in the active view.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 2: skip anything already tagged in this view, so re-running the
                // command doesn't stack duplicate tags on top of existing ones.
                HashSet<ElementId> alreadyTagged = new HashSet<ElementId>(
                    new FilteredElementCollector(doc, activeView.Id)
                        .OfClass(typeof(IndependentTag))
                        .Cast<IndependentTag>()
                        .SelectMany(t => t.GetTaggedLocalElementIds()));

                List<Wall> wallsToTag = curtainWalls.Where(w => !alreadyTagged.Contains(w.Id)).ToList();
                List<FamilyInstance> doorsToTag = doors.Where(d => !alreadyTagged.Contains(d.Id)).ToList();

                if (wallsToTag.Count == 0 && doorsToTag.Count == 0)
                {
                    new Warning("Oops...", "Every matching curtain wall and door in this view is already tagged.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 3: tag type selection.
                List<FamilySymbol> wallTagTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_WallTags)
                    .WhereElementIsElementType()
                    .Cast<FamilySymbol>()
                    .ToList();
                List<FamilySymbol> doorTagTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_DoorTags)
                    .WhereElementIsElementType()
                    .Cast<FamilySymbol>()
                    .ToList();

                if (wallTagTypes.Count == 0 && doorTagTypes.Count == 0)
                {
                    new Warning("Oops...", "No Wall Tag or Door Tag families are loaded into the project.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                Selection_TagTypes tagTypeWindow = new Selection_TagTypes(wallTagTypes, doorTagTypes, credit);
                if (tagTypeWindow.ShowDialog() != true)
                {
                    return Result.Cancelled;
                }

                FamilySymbol windowTagType = tagTypeWindow.SelectedWindowTagType;
                FamilySymbol doorTagType = tagTypeWindow.SelectedDoorTagType;

                // No tag type available/selected for a category -> nothing to tag it
                // with, so drop it rather than throwing later in the transaction.
                if (windowTagType == null) wallsToTag.Clear();
                if (doorTagType == null) doorsToTag.Clear();

                if (wallsToTag.Count == 0 && doorsToTag.Count == 0)
                {
                    new Warning("Oops...", "No tag type was selected for the elements found.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 4: create the tags.
                int wallsTagged = 0, doorsTagged = 0;
                List<string> issues = new List<string>();

                using (Transaction t = new Transaction(doc, "LW_Tag Curtain Walls and Doors"))
                {
                    t.Start();

                    if (windowTagType != null && !windowTagType.IsActive) windowTagType.Activate();
                    if (doorTagType != null && !doorTagType.IsActive) doorTagType.Activate();

                    foreach (Wall wall in wallsToTag)
                    {
                        XYZ tagPoint = GetWallTagPoint(wall);
                        if (tagPoint == null)
                        {
                            issues.Add($"Curtain Wall (Id {wall.Id}): no usable location; skipped.");
                            continue;
                        }

                        try
                        {
                            IndependentTag.Create(doc, windowTagType.Id, activeView.Id, new Reference(wall),
                                false, TagOrientation.Horizontal, tagPoint);
                            wallsTagged++;
                        }
                        catch (Exception ex)
                        {
                            issues.Add($"Curtain Wall (Id {wall.Id}): failed to create tag: {ex.Message}");
                        }
                    }

                    foreach (FamilyInstance door in doorsToTag)
                    {
                        XYZ tagPoint = (door.Location as LocationPoint)?.Point;
                        if (tagPoint == null)
                        {
                            issues.Add($"Door (Id {door.Id}): no usable location point; skipped.");
                            continue;
                        }

                        try
                        {
                            IndependentTag.Create(doc, doorTagType.Id, activeView.Id, new Reference(door),
                                false, TagOrientation.Horizontal, tagPoint);
                            doorsTagged++;
                        }
                        catch (Exception ex)
                        {
                            issues.Add($"Door (Id {door.Id}): failed to create tag: {ex.Message}");
                        }
                    }

                    t.Commit();
                }

                string summary = $"{wallsTagged} curtain wall(s) tagged.\n{doorsTagged} door(s) tagged.";
                if (issues.Count > 0)
                {
                    summary += $"\n\n{issues.Count} issue(s) were encountered:\n" + string.Join("\n", issues);
                }
                new WarningLarge("Tag Complete", summary, credit).ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }

        // Matches Win_AssignMark.cs / Win_CreateElevations.cs exactly, so every
        // Windows-panel command agrees on what counts as a "window" (a curtain wall
        // whose type's Family Name is "Curtain Wall" and whose BA_TypeFilter is "CW").
        private static bool IsCurtainWallWindow(Document doc, Element wall)
        {
            ElementType wallType = doc.GetElement(wall.GetTypeId()) as ElementType;
            if (wallType == null) return false;

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

        // Tag anchor point: the curtain wall's own centerline midpoint. Good enough
        // for a first pass — Revit will still let the user drag the tag afterward.
        private static XYZ GetWallTagPoint(Wall wall)
        {
            LocationCurve loc = wall.Location as LocationCurve;
            if (loc == null) return null;

            XYZ start = loc.Curve.GetEndPoint(0);
            XYZ end = loc.Curve.GetEndPoint(1);
            return (start + end) * 0.5;
        }
    }
}
