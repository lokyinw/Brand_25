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
    public class Text_ConsolidateTypes : IExternalCommand
    {
        private const string credit = "Last Modified by Lok on 2026-07-16. Beta 0.91";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            try
            {
                return RunConsolidation(doc);
            }
            catch (Exception ex)
            {
                // Any unexpected failure (locked elements, worksharing conflicts, etc.)
                // surfaces as a branded warning instead of Revit's raw error dialog.
                new Warning("Unexpected Error",
                    $"Text Note Type consolidation could not complete:\n\n{ex.Message}",
                    credit).ShowDialog();
                message = ex.Message;
                return Result.Failed;
            }
        }

        private Result RunConsolidation(Document doc)
        {
            // ── Step 1: Collect all TextNoteTypes ────────────────────────────

            List<TextNoteType> allTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .WhereElementIsElementType()
                .Cast<TextNoteType>()
                .OrderBy(t => t.Name)
                .ToList();

            if (!allTypes.Any())
            {
                new Warning("No Types Found", "No Text Note Types found in the project.", credit).ShowDialog();
                return Result.Cancelled;
            }

            // ── Step 2: Dialog 1 — user picks which attributes to compare ─────
            // Text Font and Text Size are included here (pre-selected) so the user sees
            // the full picture rather than having them silently hardcoded as "always
            // compared" — every attribute, checked or not, is on equal footing now.

            List<string> selectedAttributes;
            while (true)
            {
                var attributePicker = new BulletPointSelector(
                    title: "Compare Text Note Types",
                    instruction: "Select the attributes to compare.\nMatching Text Note Types will be grouped for consolidation.\nUnchecked attributes are ignored, meaning types will be consolidated even if those specific values differ.",
                    bulletPoints: TextNoteAttributeCatalog.DisplayNames,
                    credit: credit,
                    showCustomInput: false,
                    singleSelectionMode: false,
                    preSelectedItems: TextNoteAttributeCatalog.DefaultSelectedNames);

                if (attributePicker.ShowDialog() != true)
                    return Result.Cancelled;

                selectedAttributes = attributePicker.SelectedItems;

                if (selectedAttributes.Count > 0)
                    break;

                new Warning("No Attributes Selected",
                    "Please select at least one attribute to compare.", credit).ShowDialog();
            }

            // ── Step 3: Build VMs and comparison keys ─────────────────────────

            List<VM_TextNoteType> viewModels = allTypes
                .Select(t => new VM_TextNoteType(t))
                .ToList();

            foreach (var vm in viewModels)
                vm.ComparisonKey = BuildComparisonKey(vm, selectedAttributes);

            // ── Step 4: Find duplicate groups ─────────────────────────────────

            List<List<VM_TextNoteType>> duplicateGroups = viewModels
                .GroupBy(vm => vm.ComparisonKey)
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();

            if (!duplicateGroups.Any())
            {
                new Warning("No Duplicates Found",
                    "No Text Note Types share identical values for the selected attributes.",
                    credit).ShowDialog();
                return Result.Succeeded;
            }

            // ── Step 5: Populate instance counts ──────────────────────────────

            List<TextNote> allNotes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TextNotes)
                .WhereElementIsNotElementType()
                .Cast<TextNote>()
                .ToList();

            var notesByTypeId = allNotes
                .GroupBy(n => n.GetTypeId())
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var group in duplicateGroups)
            {
                foreach (var vm in group)
                {
                    if (notesByTypeId.TryGetValue(vm.TextNoteType.Id, out var notes))
                    {
                        vm.InstanceCount = notes.Count;
                        vm.InstancesInGroups = notes.Count(n => n.GroupId != ElementId.InvalidElementId);
                    }
                }
            }

            // ── Step 6: Dialog — combined duplicate overview + survivor picker ─
            // Previously this was two dialogs (an overview grid, then a per-group
            // picker). They showed the same data, so now it's one table listing
            // every candidate across every group, with a per-group survivor radio
            // column — see Consolidate_TextNoteType for the merged behavior.

            var consolidateDialog = new Consolidate_TextNoteType(duplicateGroups, selectedAttributes, credit);
            if (consolidateDialog.ShowDialog() != true)
                return Result.Succeeded;

            Dictionary<TextNoteType, TextNoteType> survivorMap = consolidateDialog.SurvivorSelections;

            // ── Step 7: Location helper for the log ─────────────────────────────
            // NOTE: any redundant type that has EVEN ONE instance inside a group is
            // entirely excluded from consolidation (not just its grouped instances).
            // Revit's API does not allow safely reassigning/deleting a type that has
            // members inside a group without risking corruption of the group, so we
            // leave those types completely untouched and flag them instead.
            //
            // A TextNote's OwnerViewId is the view it's actually drawn in. Only
            // report "Sheet:" when that owner view IS a sheet (the note/group sits
            // directly on the sheet). If it's in a regular view — even one that's
            // later placed on a sheet via a viewport — report "View:" instead.

            string DescribeLocation(TextNote note)
            {
                View ownerView = doc.GetElement(note.OwnerViewId) as View;

                if (ownerView is ViewSheet directSheet)
                    return $"Sheet: \"{directSheet.SheetNumber} - {directSheet.Name}\"";

                return $"View: \"{(ownerView?.Name ?? "Unknown View")}\"";
            }

            // ── Step 8: Execute — single transaction ──────────────────────────

            int reassigned = 0;
            var skippedTypeNames = new List<string>();
            var logEntries = new List<string>();

            using (Transaction t = new Transaction(doc, "LW_Consolidate Text Note Types"))
            {
                t.Start();

                try
                {
                    foreach (var kvp in survivorMap)
                    {
                        TextNoteType redundant = kvp.Key;
                        TextNoteType survivor = kvp.Value;

                        // Capture names up front — once doc.Delete() runs on redundant,
                        // the object is invalid and reading .Name off it throws
                        // InvalidObjectException, even just for logging afterward.
                        string redundantName = redundant.Name;
                        string survivorName = survivor.Name;

                        if (!notesByTypeId.TryGetValue(redundant.Id, out var affectedNotes))
                        {
                            doc.Delete(redundant.Id);
                            logEntries.Add($"  \u2705 {redundantName}  \u2192  {survivorName}  (0 instances)");
                            continue;
                        }

                        var groupedNotes = affectedNotes
                            .Where(n => n.GroupId != ElementId.InvalidElementId)
                            .ToList();

                        if (groupedNotes.Any())
                        {
                            // Revit's API cannot safely reassign or delete a type that has
                            // members inside a group — doing so has been observed to silently
                            // corrupt/erase the group's content. Leave the type fully intact.
                            skippedTypeNames.Add(redundantName);
                            logEntries.Add(
                                $"  \u26A0 {redundantName}  \u2192  {survivorName}  (None reassigned \u2014 instance(s) inside a group; type left untouched)");

                            // List every skipped instance individually — this is the log's
                            // primary purpose, since these are the notes the add-in could not
                            // touch (Revit's API doesn't allow editing group members) and the
                            // user may need to track down and handle manually.
                            foreach (TextNote groupedNote in groupedNotes)
                            {
                                logEntries.Add(
                                    $"      TextNote ID: {groupedNote.Id.Value}   Group ID: {groupedNote.GroupId.Value}   {DescribeLocation(groupedNote)}");
                            }

                            continue;
                        }

                        // No grouped instances — safe to consolidate normally
                        foreach (TextNote note in affectedNotes)
                        {
                            note.ChangeTypeId(survivor.Id);
                            reassigned++;
                        }

                        doc.Delete(redundant.Id);
                        logEntries.Add($"  \u2705 {redundantName}  \u2192  {survivorName}  ({affectedNotes.Count} reassigned)");
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

            int typesDeleted = survivorMap.Count - skippedTypeNames.Count;

            // ── Step 8b: Write log — always, so there's always somewhere the user
            // can find the full per-type breakdown that used to live in the dialogs.

            string logFilePath = WriteLog(doc, selectedAttributes, typesDeleted, skippedTypeNames,
                                           reassigned, logEntries);

            // ── Step 9: Dialog — completion report (kept to the essentials;
            // everything else is in the log) ──────────────────────────────────

            string completionMessage =
                $"\u25CF  {typesDeleted} Text Note Type(s) deleted.\n" +
                $"\u25CF  {skippedTypeNames.Distinct().Count()} Text Note Type(s) left untouched (There are instances inside some groups).\n" +
                $"\u25CF  {reassigned} instance(s) reassigned in total.\n\n" +
                (logFilePath != null
                    ? $"Full details logged to:\n   {logFilePath}"
                    : "The log file could not be written.");

            new WarningLarge("Consolidation Complete", completionMessage, credit, revealPath: logFilePath).ShowDialog();

            return Result.Succeeded;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string WriteLog(Document doc, List<string> selectedAttributes, int typesDeleted,
                                        List<string> skippedTypeNames, int reassigned, List<string> logEntries)
        {
            try
            {
                string documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string fileName = $"TextNoteType_Consolidation_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt";
                string logFilePath = Path.Combine(documentsFolder, fileName);

                var logLines = new List<string>
                {
                    "Text Note Type Consolidation — Log",
                    $"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Project: {doc.Title}",
                    $"Attributes compared: {string.Join(", ", selectedAttributes)}",
                    new string('-', 70),
                    $"{typesDeleted} type(s) deleted.",
                    $"{skippedTypeNames.Distinct().Count()} type(s) left untouched (instance(s) inside a group).",
                    $"{reassigned} instance(s) reassigned in total.",
                    "",
                    "Per-type detail:",
                    ""
                };
                logLines.AddRange(logEntries);

                File.WriteAllLines(logFilePath, logLines);
                return logFilePath;
            }
            catch
            {
                // If the log can't be written, the completion dialog says so — still
                // proceed, since the consolidation itself already committed.
                return null;
            }
        }

        private static string BuildComparisonKey(VM_TextNoteType vm, List<string> selectedAttributes)
        {
            return string.Join("|", selectedAttributes.Select(a => TextNoteAttributeCatalog.Find(a)?.Getter(vm) ?? ""));
        }
    }
}
