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
    //  3. For every non-survivor, non-retained style: its EDITABLE instances are
    //     reassigned to the survivor. An instance is non-editable only if it's
    //     inside a model/detail group, OR it's a filled region's own boundary/
    //     sketch line (found via FilledRegion.GetDependentElements(ElementFilter))
    //     AND that filled region is itself inside a group — a filled region's
    //     boundary lines are otherwise fully editable, confirmed by direct
    //     testing. Afterward, that style's subcategory is deleted only if it's
    //     not built-in AND has no remaining non-editable instances — otherwise
    //     it's kept (with the reason logged). Every non-editable instance is
    //     logged individually (element Id, owning group/region Id, group kind,
    //     and view/sheet for detail groups and grouped filled regions).
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
            // once, at the very start, guarantees that. Not logged — this is
            // routine, silent filtering, not something the user needs reported.
            allLineStyles = allLineStyles.Where(c => !IsTypeBExcluded(c)).ToList();

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

            Dictionary<ElementId, List<CurveElement>> earlyElementsByStyle = allCurveElements
                .GroupBy(ce => ((GraphicsStyle)ce.LineStyle).GraphicsStyleCategory.Id)
                .ToDictionary(g => g.Key, g => g.ToList());

            List<Category> unusedStyles = allLineStyles.Where(c => !earlyElementsByStyle.ContainsKey(c.Id)).ToList();

            // Styles that were unused at this point but that the user did NOT
            // select for deletion (either unchecked deliberately, or built-in and
            // never selectable in the first place) — if any of these later turn
            // up in a duplicate group, Retain Instances is pre-checked for them in
            // Consolidate_LineStyle, since declining to purge an unused style is
            // itself a signal the user wants it left alone, not merged away either.
            HashSet<ElementId> unusedRetainedIds = new HashSet<ElementId>();

            // ── Step 3: unused styles — let the user pick which ones to purge ──
            // Shows EVERY style (used and unused) for full context via a DataGrid;
            // only unused, non-built-in ones (VM_LineStyle.CanBeDeleted) are
            // actually selectable, pre-checked by default. Used/built-in rows are
            // greyed out and disabled, not merely unchecked.

            if (unusedStyles.Count > 0)
            {
                List<VM_LineStyle> allStyleVMsRaw = allLineStyles.Select(c => new VM_LineStyle(c, doc)).ToList();
                List<string> earlyUnreadable = allStyleVMsRaw
                    .Where(vm => vm.ConstructionError != null)
                    .Select(vm => $"  {vm.Name} (Id {vm.Category.Id.Value}): {vm.ConstructionError}")
                    .ToList();

                List<VM_LineStyle> allStyleVMs = allStyleVMsRaw.Where(vm => vm.ConstructionError == null).ToList();
                foreach (VM_LineStyle vm in allStyleVMs)
                    vm.InstanceCount = earlyElementsByStyle.TryGetValue(vm.Category.Id, out var l) ? l.Count : 0;

                // Warning.xaml's OK button explicitly sets DialogResult = true;
                // its title-bar X (there is no Cancel button) just calls Close()
                // without setting it, so ShowDialog() returns null in that case.
                // Closing the dialog without clicking OK means the user wants to
                // stop here, not silently proceed into the purge dialog.
                bool? lineworkWarningResult = new Warning("Have you used LINEWORK in this project?",
                    "Line styles employed in LINEWORK tool cannot be detected via API. Any purging or consolidation of line styles used in LINEWORK will revert the view specific graphics to <By Category>. STOP HERE IF IT MATTERS.",
                    credit).ShowDialog();

                if (lineworkWarningResult != true)
                    return Result.Cancelled;

                Selection_LineStylePurge purgeDialog = new Selection_LineStylePurge(allStyleVMs, credit);
                if (purgeDialog.ShowDialog() != true)
                    return Result.Cancelled;

                List<VM_LineStyle> toDelete = purgeDialog.SelectedForDeletion;

                foreach (VM_LineStyle vm in allStyleVMs)
                    if (!vm.IsUsed && !vm.IsSelectedForDeletion) unusedRetainedIds.Add(vm.Category.Id);

                int deletedCount = 0;
                List<string> deleteIssues = new List<string>();

                using (Transaction t = new Transaction(doc, "LW_Delete Unused Line Styles"))
                {
                    t.Start();
                    foreach (VM_LineStyle vm in toDelete)
                    {
                        try
                        {
                            doc.Delete(vm.Category.Id);
                            deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            deleteIssues.Add($"  {vm.Name} (Id {vm.Category.Id.Value}): could not be deleted — {ex.Message}");
                        }
                    }
                    t.Commit();
                }

                log.Add($"Deleted {deletedCount} of {toDelete.Count} selected unused line style(s).");
                if (deleteIssues.Count > 0)
                {
                    log.Add("Issues:");
                    log.AddRange(deleteIssues);
                }
                if (earlyUnreadable.Count > 0)
                {
                    log.Add($"{earlyUnreadable.Count} style(s) could not be read and were excluded from the purge dialog entirely:");
                    log.AddRange(earlyUnreadable);
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

            // Pre-fill Retain Instances for any style that was unused and NOT
            // selected for deletion back in the purge dialog — see
            // unusedRetainedIds above for the rationale. This is just the default
            // the user sees in Consolidate_LineStyle; they can still change it.
            foreach (VM_LineStyle vm in viewModels)
                if (unusedRetainedIds.Contains(vm.Category.Id)) vm.IsRetainInstances = true;

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
            // reliably returns the region's own sketch/boundary CurveElements.
            // Confirmed via direct testing that these CAN be reassigned via the API
            // like any ordinary CurveElement — the only real restriction is the
            // same one that applies to everything else: if the FILLED REGION
            // ITSELF sits inside a group, its boundary lines inherit that group's
            // "cannot be touched" restriction (mirroring how any grouped element
            // behaves), otherwise they're fully editable.
            Dictionary<ElementId, ElementId> filledRegionLineOwners = BuildFilledRegionLineOwners(doc, out HashSet<ElementId> groupedFilledRegionIds);

            List<string> groupLogEntries = new List<string>();
            List<string> filledRegionLogEntries = new List<string>();

            // Keyed per style rather than a flat list, so entries can be filtered
            // AFTER Step 6 runs — once a style's editable instances (including any
            // filled-region ones here) have actually been reassigned, there's
            // nothing distinctive left to report about them; only styles that
            // weren't consolidated (survivor, retained, or otherwise untouched)
            // keep their entries in the final log.
            Dictionary<VM_LineStyle, List<string>> filledRegionEditableEntriesByVm = new Dictionary<VM_LineStyle, List<string>>();

            // Track exactly which CurveElement Ids are non-editable (grouped, or a
            // filled-region boundary line whose region is itself inside a group)
            // per style, so Step 6 below only ever reassigns the truly editable
            // ones — everything else is left alone regardless of the user's
            // Retain Instances choice, since the API simply cannot touch them.
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
                        List<CurveElement> blockedByGroupedRegion = filledRegionElems
                            .Where(e => groupedFilledRegionIds.Contains(filledRegionLineOwners[e.Id]))
                            .ToList();
                        List<CurveElement> editableRegionElems = filledRegionElems
                            .Except(blockedByGroupedRegion)
                            .ToList();

                        if (blockedByGroupedRegion.Count > 0)
                        {
                            vm.HeldBackReasons.Add("In Filled Region (region is in a group)");
                            filledRegionLogEntries.Add($"Line style \"{vm.Name}\" — {blockedByGroupedRegion.Count} instance(s) belong to a filled region that is itself inside a group (cannot be changed):");

                            foreach (CurveElement ce in blockedByGroupedRegion)
                            {
                                MarkNonEditable(vm.Category.Id, ce.Id);

                                ElementId frId = filledRegionLineOwners[ce.Id];
                                FilledRegion fr = doc.GetElement(frId) as FilledRegion;
                                string groupInfo = "";
                                string viewInfo = "";
                                if (fr != null)
                                {
                                    try
                                    {
                                        Group grp = doc.GetElement(fr.GroupId) as Group;
                                        if (grp != null)
                                        {
                                            groupInfo = $"   Group Id: {fr.GroupId.Value}";
                                            View ownerView = doc.GetElement(grp.OwnerViewId) as View;
                                            if (ownerView is ViewSheet sheet)
                                                viewInfo = $"  Sheet: \"{sheet.SheetNumber} - {sheet.Name}\"";
                                            else if (ownerView != null)
                                                viewInfo = $"  View: \"{ownerView.Name}\"";
                                        }
                                    }
                                    catch
                                    {
                                        // Non-essential — leave blank rather than
                                        // letting a lookup failure abort the command.
                                    }
                                }

                                filledRegionLogEntries.Add(
                                    $"    Element Id: {ce.Id.Value}   FilledRegion Id: {frId.Value}{groupInfo}{viewInfo}");
                            }
                        }

                        if (editableRegionElems.Count > 0)
                        {
                            // NOT marked non-editable, NOT added to HeldBackReasons —
                            // these are ordinary editable instances now (the region
                            // isn't grouped). Stashed per-VM here; only kept in the
                            // final log if this style ends up NOT actually
                            // consolidated (see filtering after Step 6).
                            List<string> vmEntries = new List<string> {
                                $"Line style \"{vm.Name}\" — {editableRegionElems.Count} instance(s) belong to a filled region boundary but ARE editable (region is not in a group):"
                            };

                            foreach (CurveElement ce in editableRegionElems)
                            {
                                ElementId frId = filledRegionLineOwners[ce.Id];
                                vmEntries.Add($"    Element Id: {ce.Id.Value}   FilledRegion Id: {frId.Value}");
                            }

                            filledRegionEditableEntriesByVm[vm] = vmEntries;
                        }
                    }
                }
            }

            // ── Step 6: show every group (survivor pick is mandatory, Retain
            // Instances is a free per-style choice) and consolidate accordingly ──

            int typesDeleted = 0;
            int reassigned = 0;
            List<string> consolidationLogEntries = new List<string>();
            HashSet<VM_LineStyle> consolidatedVms = new HashSet<VM_LineStyle>();

            Consolidate_LineStyle dialog = new Consolidate_LineStyle(duplicateGroups, credit);
            if (dialog.ShowDialog() == true)
            {
                Dictionary<VM_LineStyle, VM_LineStyle> survivorMap = dialog.SurvivorSelections;
                List<VM_LineStyle> retainedStyles = dialog.RetainedStyles;

                // A group's survivor never appears as a key in survivorMap (it's
                // the destination, not something reassigned) — but it's just as
                // much a part of the consolidation as the styles merged into it,
                // so it belongs in this set too, not just the redundant side.
                consolidatedVms = new HashSet<VM_LineStyle>(survivorMap.Keys);
                foreach (List<VM_LineStyle> group in duplicateGroups)
                {
                    VM_LineStyle survivor = group.FirstOrDefault(vm => vm.IsSurvivor);
                    if (survivor != null) consolidatedVms.Add(survivor);
                }

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

            // Only keep filled-region-editable entries for styles that were NOT
            // part of an actual consolidation action — i.e. neither reassigned
            // away (redundant, non-retained) nor a group's survivor. Retained
            // styles, or styles otherwise never processed, keep their entries.
            List<string> filledRegionEditableLogEntries = new List<string>();
            foreach (var kvp in filledRegionEditableEntriesByVm)
            {
                if (consolidatedVms.Contains(kvp.Key)) continue;
                filledRegionEditableLogEntries.AddRange(kvp.Value);
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
                log.Add("Instances belonging to a filled region that is itself inside a group (cannot be changed via the API):");
                log.AddRange(filledRegionLogEntries);
                log.Add("");
            }

            if (filledRegionEditableLogEntries.Count > 0)
            {
                log.Add("Instances belonging to a filled region boundary that ARE editable (region not in a group — included in normal consolidation):");
                log.AddRange(filledRegionEditableLogEntries);
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
        // FilledRegion it belongs to, via FilledRegion.GetDependentElements(filter)
        // — the confirmed way to retrieve a region's dependent sketch lines.
        // Confirmed by direct testing that these lines' LineStyle CAN be
        // reassigned via the API. groupedFilledRegionIds separately reports which
        // filled regions are themselves inside a group — their boundary lines are
        // the only ones still treated as non-editable, mirroring how any other
        // grouped element behaves.
        private static Dictionary<ElementId, ElementId> BuildFilledRegionLineOwners(Document doc, out HashSet<ElementId> groupedFilledRegionIds)
        {
            Dictionary<ElementId, ElementId> owners = new Dictionary<ElementId, ElementId>();
            groupedFilledRegionIds = new HashSet<ElementId>();

            List<FilledRegion> filledRegions = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegion))
                .Cast<FilledRegion>()
                .ToList();

            ElementClassFilter curveFilter = new ElementClassFilter(typeof(CurveElement));

            foreach (FilledRegion fr in filledRegions)
            {
                try
                {
                    if (fr.GroupId != ElementId.InvalidElementId)
                        groupedFilledRegionIds.Add(fr.Id);
                }
                catch
                {
                    // If GroupId itself can't be read, conservatively assume NOT
                    // grouped — this only affects whether the region's lines are
                    // treated as editable; any actual reassignment failure would
                    // still surface as its own logged issue downstream.
                }

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
