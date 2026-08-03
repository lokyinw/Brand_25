using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Brand_25
{
    // Line styles under OST_Lines fall into three kinds:
    //   - Custom (user-created): fully ordinary — deletable, renamable.
    //   - "Type A" built-in (e.g. <Beyond>, <Centerline>, <Demolished>, <Hidden
    //     Lines>, <Hidden>, <Lines>, <Medium Lines>, <Overhead>, <Path of Travel
    //     Lines>, <Thin Lines>, <Wide Lines>): selectable when a user draws a
    //     line, and otherwise behave like custom styles for this tool — their
    //     instances CAN be reassigned during consolidation. The only restriction
    //     is they can never be deleted or renamed (VM_LineStyle.IsBuiltIn).
    //   - "Type B" built-in (e.g. <Area Based Load Boundary>, <Area Boundary>,
    //     <Axis of Rotation>, <Fabric Envelope>, <Fabric Sheets>, <Insulation
    //     Batting Lines>, <Room Separation>, <Sketch>, <Space Separation>):
    //     functional elements first, graphical line styles only incidentally —
    //     not selectable when drawing a line. These are excluded entirely from
    //     all three phases below (IsTypeBExcluded), as if they don't exist for
    //     this tool's purposes at all.
    //
    // Three-phase workflow (Type B already filtered out before Phase 1 runs):
    //
    //  1. Find every remaining line-style subcategory that no CurveElement in the
    //     project currently uses. Ask the user whether to delete them now or
    //     handle it themselves — if the latter, write the list to a log and stop;
    //     nothing else in this command runs that session. Type A built-in styles
    //     are never attempted for deletion even if unused; they're just logged.
    //
    //  2. Group the REMAINING line styles by (LineWeight, Colour, LinePattern) —
    //     identical values on all three is what "duplicate" means here. Every style
    //     in a duplicate group is shown to the user via Consolidate_LineStyle,
    //     including Type A built-in ones (with their instance counts and a Status
    //     column explaining why a style may be constrained). The Survivor radio is
    //     never disabled — any style can be picked. A separate "Retain Instances"
    //     checkbox lets the user opt any non-survivor style OUT of reassignment
    //     entirely, independent of the survivor choice.
    //
    //  3. For every non-survivor, non-retained style: its EDITABLE instances (not
    //     inside a model/detail group, not a filled region's own boundary/sketch
    //     line — found via FilledRegion.GetDependentElements(ElementFilter)) are
    //     reassigned to the survivor. Afterward, that style's subcategory is
    //     deleted only if it's not built-in AND has no remaining group/filled-
    //     region instances — otherwise it's kept (with the reason logged). Every
    //     group/filled-region instance is logged individually (element Id, owning
    //     group/region Id, group kind, and view/sheet for detail groups and
    //     filled regions).
    [Transaction(TransactionMode.Manual)]
    public class Line_ConsolidateStyles : IExternalCommand
    {
        private const string credit = "Last Modified by Lok on 2026-07-31. Beta 0.10";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            try
            {
                return Run(doc);
            }
            catch (Exception ex)
            {
                new Warning("Unexpected Error",
                    $"Line Style consolidation could not complete:\n\n{ex.Message}",
                    credit).ShowDialog();
                message = ex.Message;
                return Result.Failed;
            }
        }

        private Result Run(Document doc)
        {
            List<string> log = new List<string>();

            // ── Step 1: collect every line-style subcategory ──────────────────

            Category linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            List<Category> allLineStyles = linesCategory.SubCategories.Cast<Category>().ToList();

            // Type B built-in styles (see IsTypeBExcluded below) are functional
            // elements first and graphical line styles only incidentally — the
            // user explicitly wants these excluded from ALL THREE phases (unused
            // detection, duplicate detection/dialog, and consolidation) entirely,
            // as if they don't exist for this tool's purposes. Filtering here,
            // once, at the very start, guarantees that.
            List<Category> excludedTypeB = allLineStyles.Where(IsTypeBExcluded).ToList();
            allLineStyles = allLineStyles.Where(c => !IsTypeBExcluded(c)).ToList();

            if (excludedTypeB.Count > 0)
            {
                log.Add($"{excludedTypeB.Count} built-in style(s) excluded entirely (functional, not purely graphical — e.g. Room Separation, Area Boundary):");
                foreach (Category c in excludedTypeB.OrderBy(c => SafeCategoryName(c)))
                    log.Add($"  {SafeCategoryName(c)} (Id {SafeCategoryIdString(c)})");
                log.Add("");
            }

            if (allLineStyles.Count == 0)
            {
                new Warning("No Line Styles", "No line style subcategories were found in this project.", credit).ShowDialog();
                return Result.Succeeded;
            }

            // ── Step 2: find every CurveElement so we know which styles are used ──

            List<CurveElement> allCurveElements = new FilteredElementCollector(doc)
                .OfClass(typeof(CurveElement))
                .WhereElementIsNotElementType()
                .Cast<CurveElement>()
                .Where(ce => ce.LineStyle != null)
                .ToList();

            HashSet<ElementId> usedStyleIds = new HashSet<ElementId>(
                allCurveElements.Select(ce => ((GraphicsStyle)ce.LineStyle).GraphicsStyleCategory.Id));

            List<Category> unusedStyles = allLineStyles.Where(c => !usedStyleIds.Contains(c.Id)).ToList();

            // ── Step 3: unused styles — ask the user how to proceed ───────────

            if (unusedStyles.Count > 0)
            {
                List<string> choices = new List<string>
                {
                    "Delete the unused line styles for me",
                    "I'll handle it myself"
                };

                BulletPointSelector choiceDialog = new BulletPointSelector(
                    title: "Unused Line Styles Found",
                    instruction: $"{unusedStyles.Count} line style(s) are not used by any element in this project. How would you like to proceed?",
                    bulletPoints: choices,
                    credit: credit,
                    showCustomInput: false,
                    singleSelectionMode: true);

                if (choiceDialog.ShowDialog() != true || choiceDialog.SelectedItems.Count == 0)
                    return Result.Cancelled;

                bool userWantsToHandleItThemselves = choiceDialog.SelectedItems[0] == "I'll handle it myself";

                if (userWantsToHandleItThemselves)
                {
                    log.Add("Unused Line Styles — Log");
                    log.Add($"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    log.Add($"Project: {doc.Title}");
                    log.Add(new string('-', 70));
                    log.Add($"{unusedStyles.Count} unused line style(s):");
                    log.Add("");
                    foreach (Category c in unusedStyles.OrderBy(c => SafeCategoryName(c)))
                        log.Add($"  {SafeCategoryName(c)}  (Id {SafeCategoryIdString(c)})");

                    string unusedLogPath = WriteLog(log, "UnusedLineStyles");

                    new Warning("Unused Line Styles",
                        $"{unusedStyles.Count} unused line style(s) found - see log.\n\nNo changes were made.",
                        credit,
                        revealPath: unusedLogPath).ShowDialog();

                    return Result.Succeeded;
                }

                // Delete unused styles for the user — skipping any that are
                // built-in/reserved (negative Id), which can never be deleted via
                // the API. These are reported separately rather than attempted.
                int deletedCount = 0;
                List<string> deleteIssues = new List<string>();
                List<string> builtInSkipped = new List<string>();

                using (Transaction t = new Transaction(doc, "LW_Delete Unused Line Styles"))
                {
                    t.Start();
                    foreach (Category c in unusedStyles)
                    {
                        if (SafeCategoryIsBuiltIn(c))
                        {
                            builtInSkipped.Add($"  {SafeCategoryName(c)} (Id {SafeCategoryIdString(c)})");
                            continue;
                        }

                        try
                        {
                            doc.Delete(c.Id);
                            deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            deleteIssues.Add($"  {SafeCategoryName(c)} (Id {SafeCategoryIdString(c)}): could not be deleted — {ex.Message}");
                        }
                    }
                    t.Commit();
                }

                log.Add($"Deleted {deletedCount} of {unusedStyles.Count} unused line style(s).");
                if (builtInSkipped.Count > 0)
                {
                    log.Add($"{builtInSkipped.Count} unused style(s) skipped — built-in/reserved, cannot be deleted via the API:");
                    log.AddRange(builtInSkipped);
                }
                if (deleteIssues.Count > 0)
                {
                    log.Add("Issues:");
                    log.AddRange(deleteIssues);
                }
                log.Add("");

                // Transaction.Commit() above already forces a full regeneration —
                // no separate doc.Regenerate() needed (and calling it here, with no
                // transaction/transaction group open, throws "Modification of the
                // document is forbidden"). Just re-fetch the parent category fresh,
                // in case the earlier reference is affected by the deletions.
                linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                allLineStyles = linesCategory.SubCategories.Cast<Category>().Where(c => !IsTypeBExcluded(c)).ToList();
            }

            // ── Step 4: build VMs for remaining styles, group by duplicate key ──
            // Any category whose Projection graphics style / weight / colour /
            // pattern couldn't be read (VM_LineStyle.ConstructionError set) is
            // excluded from comparison entirely and reported separately below —
            // rather than letting one bad category take down the whole command.

            List<VM_LineStyle> allViewModelsRaw = allLineStyles.Select(c => new VM_LineStyle(c, doc)).ToList();

            List<string> unreadableStyleEntries = allViewModelsRaw
                .Where(vm => vm.ConstructionError != null)
                .Select(vm => $"  {vm.Name} (Id {vm.Category.Id.Value}): {vm.ConstructionError}")
                .ToList();

            List<VM_LineStyle> viewModels = allViewModelsRaw.Where(vm => vm.ConstructionError == null).ToList();

            foreach (VM_LineStyle vm in viewModels)
                vm.ComparisonKey = $"{vm.LineWeight}|{vm.Colour}|{vm.LinePattern}";
            List<List<VM_LineStyle>> duplicateGroups = viewModels
                .GroupBy(vm => vm.ComparisonKey)
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();

            if (duplicateGroups.Count == 0)
            {
                if (unreadableStyleEntries.Count > 0)
                {
                    log.Add($"{unreadableStyleEntries.Count} style(s) could not be read and were excluded from comparison:");
                    log.AddRange(unreadableStyleEntries);
                    log.Add("");
                }

                new Warning("No Duplicates Found",
                    "No line styles share identical Line Weight, Colour, and Line Pattern.",
                    credit).ShowDialog();

                if (log.Count > 0)
                {
                    string logPath = WriteLog(log, "LineStyleConsolidation");
                    new Warning("Done", "Unused line styles were processed - see log for details.", credit,
                        revealPath: logPath).ShowDialog();
                }
                return Result.Succeeded;
            }

            // ── Step 5: instance counts, then mark held-back reasons ───────────
            // Re-collect CurveElements fresh HERE, rather than reusing the list
            // gathered back in Step 2 — that earlier list may have been captured
            // before the delete-unused-styles transaction committed, and Element
            // references obtained before a transaction can be invalidated by it.
            // Acting on those stale references later (setting .LineStyle inside the
            // consolidation transaction below) is what produced "Element id not
            // found in this Document" — re-querying here avoids that regardless of
            // whether a deletion actually happened above.
            allCurveElements = new FilteredElementCollector(doc)
                .OfClass(typeof(CurveElement))
                .WhereElementIsNotElementType()
                .Cast<CurveElement>()
                .Where(ce => ce.LineStyle != null)
                .ToList();

            Dictionary<ElementId, List<CurveElement>> elementsByStyle = allCurveElements
                .GroupBy(ce => ((GraphicsStyle)ce.LineStyle).GraphicsStyleCategory.Id)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (VM_LineStyle vm in viewModels)
                vm.InstanceCount = elementsByStyle.TryGetValue(vm.Category.Id, out var list) ? list.Count : 0;

            // Built-in styles are tagged for display/reporting (Status column,
            // logs) and are never deletable (enforced later, at the deletion
            // decision in Step 6) — but their instances are NOT automatically
            // held back from reassignment. A built-in style behaves like an
            // ordinary custom style for consolidation purposes; the user's own
            // Retain Instances checkbox is what decides whether ITS instances get
            // reassigned, same as for any other style.
            foreach (VM_LineStyle vm in viewModels)
                if (vm.IsBuiltIn) vm.HeldBackReasons.Add("Built-in");

            // Filled-region boundary lines: FilledRegion.GetDependentElements(filter)
            // reliably returns the region's own sketch/boundary CurveElements — this
            // replaces the earlier FilledRegionType-parameter guesswork, which
            // couldn't be confirmed against the live API.
            Dictionary<ElementId, ElementId> filledRegionLineOwners = BuildFilledRegionLineOwners(doc);

            List<string> groupLogEntries = new List<string>();
            List<string> filledRegionLogEntries = new List<string>();

            // Track exactly which CurveElement Ids are non-editable (grouped or a
            // filled-region boundary line) per style, so Step 6 below only ever
            // reassigns the truly editable ones — everything else is left alone
            // regardless of the user's Retain Instances choice, since the API
            // simply cannot touch them.
            Dictionary<ElementId, HashSet<ElementId>> nonEditableInstanceIdsByStyle = new Dictionary<ElementId, HashSet<ElementId>>();

            void MarkNonEditable(ElementId styleCategoryId, ElementId curveElementId)
            {
                if (!nonEditableInstanceIdsByStyle.TryGetValue(styleCategoryId, out HashSet<ElementId> set))
                {
                    set = new HashSet<ElementId>();
                    nonEditableInstanceIdsByStyle[styleCategoryId] = set;
                }
                set.Add(curveElementId);
            }

            foreach (List<VM_LineStyle> group in duplicateGroups)
            {
                foreach (VM_LineStyle vm in group)
                {
                    // Built-in styles still need their per-instance group/filled-
                    // region detection — any style, built-in or not, can now be
                    // picked as survivor or have its editable instances reassigned,
                    // so we still need to know which of ITS instances are editable.
                    if (!elementsByStyle.TryGetValue(vm.Category.Id, out List<CurveElement> elems))
                        continue;

                    List<CurveElement> groupedElems = elems.Where(e => e.GroupId != ElementId.InvalidElementId).ToList();
                    if (groupedElems.Count > 0)
                    {
                        vm.HeldBackReasons.Add("In Group");
                        groupLogEntries.Add($"Line style \"{vm.Name}\" — {groupedElems.Count} instance(s) inside a group (cannot be changed):");

                        foreach (CurveElement ce in groupedElems)
                        {
                            MarkNonEditable(vm.Category.Id, ce.Id);

                            Group grp = doc.GetElement(ce.GroupId) as Group;

                            // grp?.Category only guards against grp itself being
                            // null — it does NOT guard against the .Category
                            // property getter throwing internally, which some Group
                            // instances have been observed to do ("Category is
                            // unexpectedly NULL"). Wrapped so one such group can't
                            // take down the whole command; falls back to labeling
                            // it "Detail Group" (the more common case) and noting
                            // the failure in the log instead.
                            bool isModelGroup = false;
                            try
                            {
                                isModelGroup = grp?.Category != null && grp.Category.Id.Value == (long)BuiltInCategory.OST_IOSModelGroups;
                            }
                            catch (Exception ex)
                            {
                                groupLogEntries.Add(
                                    $"    NOTE: could not determine Model/Detail kind for Group Id {ce.GroupId.Value} ({ex.Message}) — defaulting to \"Detail Group\" label below.");
                            }

                            string groupKind = isModelGroup ? "Model Group" : "Detail Group";

                            string viewInfo = "";
                            if (!isModelGroup && grp != null)
                            {
                                try
                                {
                                    View ownerView = doc.GetElement(grp.OwnerViewId) as View;
                                    if (ownerView is ViewSheet sheet)
                                        viewInfo = $"  Sheet: \"{sheet.SheetNumber} - {sheet.Name}\"";
                                    else if (ownerView != null)
                                        viewInfo = $"  View: \"{ownerView.Name}\"";
                                }
                                catch
                                {
                                    // Non-essential — leave viewInfo blank rather than
                                    // letting a view-lookup failure abort the command.
                                }
                            }

                            groupLogEntries.Add(
                                $"    Element Id: {ce.Id.Value}   Group Id: {ce.GroupId.Value}   {groupKind}{viewInfo}");
                        }
                    }

                    List<CurveElement> filledRegionElems = elems.Where(e => filledRegionLineOwners.ContainsKey(e.Id)).ToList();
                    if (filledRegionElems.Count > 0)
                    {
                        vm.HeldBackReasons.Add("In Filled Region");
                        filledRegionLogEntries.Add($"Line style \"{vm.Name}\" — {filledRegionElems.Count} instance(s) belong to a filled region boundary (cannot be changed):");

                        foreach (CurveElement ce in filledRegionElems)
                        {
                            MarkNonEditable(vm.Category.Id, ce.Id);

                            ElementId frId = filledRegionLineOwners[ce.Id];
                            FilledRegion fr = doc.GetElement(frId) as FilledRegion;
                            string viewInfo = "";
                            if (fr != null)
                            {
                                try
                                {
                                    View ownerView = doc.GetElement(fr.OwnerViewId) as View;
                                    if (ownerView is ViewSheet sheet)
                                        viewInfo = $"  Sheet: \"{sheet.SheetNumber} - {sheet.Name}\"";
                                    else if (ownerView != null)
                                        viewInfo = $"  View: \"{ownerView.Name}\"";
                                }
                                catch
                                {
                                    // Non-essential — leave viewInfo blank rather than
                                    // letting a view-lookup failure abort the command.
                                }
                            }

                            filledRegionLogEntries.Add(
                                $"    Element Id: {ce.Id.Value}   FilledRegion Id: {frId.Value}{viewInfo}");
                        }
                    }
                }
            }

            // ── Step 6: show every group (survivor pick is mandatory, Retain
            // Instances is a free per-style choice) and consolidate accordingly ──

            int typesDeleted = 0;
            int reassigned = 0;
            List<string> consolidationLogEntries = new List<string>();

            Consolidate_LineStyle dialog = new Consolidate_LineStyle(duplicateGroups, credit);
            if (dialog.ShowDialog() == true)
            {
                Dictionary<VM_LineStyle, VM_LineStyle> survivorMap = dialog.SurvivorSelections;
                List<VM_LineStyle> retainedStyles = dialog.RetainedStyles;

                using (Transaction t = new Transaction(doc, "LW_Consolidate Line Styles"))
                {
                    t.Start();
                    try
                    {
                        foreach (var kvp in survivorMap)
                        {
                            VM_LineStyle redundant = kvp.Key;
                            VM_LineStyle survivor = kvp.Value;

                            string redundantName = redundant.Name;
                            string survivorName = survivor.Name;

                            nonEditableInstanceIdsByStyle.TryGetValue(redundant.Category.Id, out HashSet<ElementId> nonEditableIds);
                            nonEditableIds = nonEditableIds ?? new HashSet<ElementId>();

                            elementsByStyle.TryGetValue(redundant.Category.Id, out List<CurveElement> allInstances);
                            allInstances = allInstances ?? new List<CurveElement>();

                            List<CurveElement> editableInstances = allInstances.Where(ce => !nonEditableIds.Contains(ce.Id)).ToList();

                            foreach (CurveElement ce in editableInstances)
                            {
                                ce.LineStyle = survivor.GraphicsStyle;
                                reassigned++;
                            }

                            // Only delete if nothing is left that can't be touched,
                            // and it's not a built-in/reserved style (never
                            // deletable via the API regardless).
                            bool canDelete = !redundant.IsBuiltIn && !redundant.HasBlockingInstances;
                            string outcome;

                            if (canDelete)
                            {
                                doc.Delete(redundant.Category.Id);
                                typesDeleted++;
                                outcome = "DELETED";
                            }
                            else if (redundant.IsBuiltIn)
                            {
                                outcome = "KEPT (built-in, cannot be deleted)";
                            }
                            else
                            {
                                outcome = "KEPT (instance(s) remain in a group or filled region)";
                            }

                            consolidationLogEntries.Add(
                                $"  \u2705 {redundantName}  \u2192  {survivorName}  ({editableInstances.Count} of {allInstances.Count} reassigned)  \u2014 {outcome}");
                        }

                        t.Commit();
                    }
                    catch
                    {
                        if (t.HasStarted() && !t.HasEnded())
                            t.RollBack();
                        throw;
                    }
                }

                if (retainedStyles.Count > 0)
                {
                    log.Add("Retained (Retain Instances checked — left completely untouched, not deleted):");
                    foreach (VM_LineStyle vm in retainedStyles.OrderBy(v => v.Name))
                        log.Add($"  {vm.Name} (Id {vm.Category.Id.Value}) — Instances: {vm.InstanceCount}");
                    log.Add("");
                }
            }

            // ── Step 7: write the combined log ─────────────────────────────────

            log.Add("Line Style Consolidation — Log");
            log.Add($"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.Add($"Project: {doc.Title}");
            log.Add(new string('-', 70));
            log.Add($"{duplicateGroups.Count} duplicate group(s) found ({duplicateGroups.Sum(g => g.Count)} styles).");
            log.Add($"{typesDeleted} style(s) consolidated (deleted), {reassigned} instance(s) reassigned.");
            log.Add("");

            if (consolidationLogEntries.Count > 0)
            {
                log.Add("Consolidated:");
                log.AddRange(consolidationLogEntries);
                log.Add("");
            }

            log.Add("All duplicate-group candidates, for reference and next-step planning (Status shows why a style may not be fully consolidatable, if at all):");
            int groupNumber = 0;
            foreach (List<VM_LineStyle> group in duplicateGroups)
            {
                groupNumber++;
                log.Add($"Group {groupNumber} — Weight/Colour/Pattern key: {group.First().ComparisonKey}");
                foreach (VM_LineStyle vm in group.OrderBy(v => v.Name))
                {
                    log.Add($"    \"{vm.Name}\"  (Id {vm.Category.Id.Value})   Instances: {vm.InstanceCount}   Status: {vm.StatusText}");
                }
            }
            log.Add("");

            if (groupLogEntries.Count > 0)
            {
                log.Add("Instances inside a group (cannot be changed via the API):");
                log.AddRange(groupLogEntries);
                log.Add("");
            }

            if (filledRegionLogEntries.Count > 0)
            {
                log.Add("Instances belonging to a filled region boundary (cannot be changed via the API):");
                log.AddRange(filledRegionLogEntries);
                log.Add("");
            }

            if (unreadableStyleEntries.Count > 0)
            {
                log.Add($"{unreadableStyleEntries.Count} style(s) could not be read and were excluded from comparison entirely:");
                log.AddRange(unreadableStyleEntries);
                log.Add("");
            }

            string finalLogPath = WriteLog(log, "LineStyleConsolidation");

            string summary =
                $"{typesDeleted} line style(s) consolidated.\n" +
                $"{reassigned} instance(s) reassigned.\n\n" +
                "See log for full detail, including every duplicate-group candidate and why any were kept.";

            new WarningLarge("Line Style Consolidation Complete", summary, credit,
                revealPath: finalLogPath).ShowDialog();

            return Result.Succeeded;
        }

        // Maps each filled region's own boundary/sketch CurveElement Id to the
        // FilledRegion it belongs to, via FilledRegion.GetDependentElements(filter) —
        // the confirmed way to retrieve a region's dependent sketch lines, rather
        // than guessing at a FilledRegionType parameter.
        private static Dictionary<ElementId, ElementId> BuildFilledRegionLineOwners(Document doc)
        {
            Dictionary<ElementId, ElementId> owners = new Dictionary<ElementId, ElementId>();

            List<FilledRegion> filledRegions = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegion))
                .Cast<FilledRegion>()
                .ToList();

            ElementClassFilter curveFilter = new ElementClassFilter(typeof(CurveElement));

            foreach (FilledRegion fr in filledRegions)
            {
                ICollection<ElementId> dependentLineIds;
                try
                {
                    dependentLineIds = fr.GetDependentElements(curveFilter);
                }
                catch
                {
                    continue; // skip this one region rather than aborting the whole scan
                }

                foreach (ElementId lineId in dependentLineIds)
                    owners[lineId] = fr.Id;
            }

            return owners;
        }

        private static string WriteLog(List<string> lines, string fileNamePrefix)
        {
            try
            {
                string tempFolder = Path.GetTempPath();
                string fileName = $"{fileNamePrefix}_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt";
                string logFilePath = Path.Combine(tempFolder, fileName);
                File.WriteAllLines(logFilePath, lines);
                return logFilePath;
            }
            catch
            {
                return null;
            }
        }

        // Category.Name and Category.Id have both been observed to throw for
        // certain subcategories in this project (not just the GraphicsStyle/
        // LineWeight/Colour/LinePattern reads that VM_LineStyle already guards) —
        // these two helpers keep Phase 1's raw-Category handling (before any
        // VM_LineStyle even exists) from crashing the whole command before a log
        // can be written.
        private static string SafeCategoryName(Category c)
        {
            try { return c.Name; }
            catch { return $"(name unavailable, Id {SafeCategoryIdString(c)})"; }
        }

        private static string SafeCategoryIdString(Category c)
        {
            try { return c.Id.Value.ToString(); }
            catch { return "unknown"; }
        }

        // Conservative on failure: if Id itself can't be read, this can't confirm
        // built-in status either way — treated as NOT built-in so a deletion is at
        // least attempted (and any resulting failure gets caught and logged as
        // usual) rather than silently skipped.
        private static bool SafeCategoryIsBuiltIn(Category c)
        {
            try { return c.Id.Value < 0; }
            catch { return false; }
        }

        // "Type B" built-in line styles: functional elements first, graphical line
        // styles only incidentally (e.g. Room Separation, Area Boundary) — these
        // are excluded entirely from all three phases, per explicit instruction.
        // Matched by name (case-insensitive, brackets stripped) rather than
        // BuiltInCategory enum values — the exact BuiltInCategory each of these
        // specific Lines-subcategories resolves to isn't something confirmed
        // against the live API, whereas the displayed names are exactly what was
        // specified. "Type A" built-in styles (e.g. <Beyond>, <Hidden>, <Wide
        // Lines>) are NOT in this list — they're treated like ordinary custom
        // styles for consolidation purposes (reassignable, just never deletable),
        // which VM_LineStyle.IsBuiltIn already handles.
        private static readonly HashSet<string> TypeBExcludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Area Based Load Boundary",
            "Area Boundary",
            "Axis of Rotation",
            "Fabric Envelope",
            "Fabric Sheets",
            "Insulation Batting Lines",
            "Room Separation",
            "Sketch",
            "Space Separation",
        };

        private static bool IsTypeBExcluded(Category c)
        {
            string name = SafeCategoryName(c).Trim().TrimStart('<').TrimEnd('>').Trim();
            return TypeBExcludedNames.Contains(name);
        }
    }
}
