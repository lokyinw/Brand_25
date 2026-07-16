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
        private const string credit = "Last Modified by Lok on 2026-03-30. Beta 0.90";

        // All attribute display names available for comparison
        private static readonly List<string> AvailableAttributes = new List<string>
        {
            "Bold",
            "Italic",
            "Underline",
            "Width Factor",
            "Background",
            "Show Border",
            "Line Weight",
            "Leader Arrowhead",
            "Leader Offset",
            "Tab Size",
            "Colour"
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

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

            // ── Step 2: Dialog 1 — user picks which extra attributes to compare

            var attributePicker = new BulletPointSelector(
                title: "Compare Text Note Types",
                instruction: "Type Name, Text Font and Text Size are always compared.\nSelect any additional attributes to include:",
                bulletPoints: AvailableAttributes,
                credit: credit,
                showCustomInput: false,
                singleSelectionMode: false);

            if (attributePicker.ShowDialog() != true)
                return Result.Cancelled;

            List<string> extraAttributes = attributePicker.SelectedItems;

            // ── Step 3: Build VMs and comparison keys ─────────────────────────

            List<VM_TextNoteType> viewModels = allTypes
                .Select(t => new VM_TextNoteType(t))
                .ToList();

            foreach (var vm in viewModels)
                vm.ComparisonKey = BuildComparisonKey(vm, extraAttributes);

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

            // Build a lookup: TypeId → all TextNote instances
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

            // ── Step 6: Dialog 2 — duplicate report ───────────────────────────

            var reportDialog = new Report_DupTextNoteType(duplicateGroups, extraAttributes, credit);
            reportDialog.ShowDialog();

            if (!reportDialog.ProceedWithConsolidation)
                return Result.Succeeded;

            // ── Step 7: Dialog 3 — pick survivor per group ────────────────────

            var consolidateDialog = new Consolidate_TextNoteType(duplicateGroups, credit);
            if (consolidateDialog.ShowDialog() != true)
                return Result.Succeeded;

            Dictionary<TextNoteType, TextNoteType> survivorMap = consolidateDialog.SurvivorSelections;

            // ── Step 8: Dialog 4 — confirmation summary ───────────────────────
            //
            // NOTE: any redundant type that has EVEN ONE instance inside a group is
            // entirely excluded from consolidation (not just its grouped instances).
            // Revit's API does not allow safely reassigning/deleting a type that has
            // members inside a group without risking corruption of the group, so we
            // leave those types completely untouched and flag them instead — reported
            // as "None reassigned" rather than a count, since no work happens on them.

            int totalDeletePreview = 0;
            int totalSkipPreview = 0;
            var summaryLines = new List<string>();

            foreach (var kvp in survivorMap)
            {
                TextNoteType redundant = kvp.Key;
                TextNoteType survivor = kvp.Value;

                if (notesByTypeId.TryGetValue(redundant.Id, out var affectedNotes))
                {
                    bool hasGroupedInstances = affectedNotes.Any(n => n.GroupId != ElementId.InvalidElementId);

                    if (hasGroupedInstances)
                    {
                        totalSkipPreview++;
                        summaryLines.Add(
                            $"  \u26A0 {redundant.Name}  \u2192  {survivor.Name}  (None reassigned \u2014 instance(s) inside a group; type left untouched)");
                        continue;
                    }

                    totalDeletePreview++;
                    summaryLines.Add($"  \u2705 {redundant.Name}  \u2192  {survivor.Name}  ({affectedNotes.Count} reassigned)");
                }
                else
                {
                    totalDeletePreview++;
                    summaryLines.Add($"  \u2705 {redundant.Name}  \u2192  {survivor.Name}  (0 instances)");
                }
            }

            string confirmMessage =
                $"Ready to consolidate:\n\n" +
                string.Join("\n", summaryLines) +
                $"\n\n  \uD83D\uDDD1 {totalDeletePreview} type(s) will be deleted." +
                (totalSkipPreview > 0
                    ? $"\n  \u26A0 {totalSkipPreview} type(s) will be left untouched \u2014 see \"None reassigned\" above."
                    : "\n  No types will be skipped.");

            var confirmDialog = new WarningLarge("Confirm Consolidation", confirmMessage, credit);
            if (confirmDialog.ShowDialog() != true)
                return Result.Succeeded;

            // ── Step 8b: Location helper for the glitch log ───────────────────
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

            // ── Step 9: Execute — single transaction ──────────────────────────

            int reassigned = 0;
            var skippedTypeNames = new List<string>();
            var glitchLogEntries = new List<string>();
            var resultLines = new List<string>();

            using (Transaction t = new Transaction(doc, "LW_Consolidate Text Note Types"))
            {
                t.Start();

                foreach (var kvp in survivorMap)
                {
                    TextNoteType redundant = kvp.Key;
                    TextNoteType survivor = kvp.Value;

                    // Capture names up front — once doc.Delete() runs on redundant,
                    // the object is invalid and reading .Name off it throws
                    // InvalidObjectException, even just for logging purposes afterward.
                    string redundantName = redundant.Name;
                    string survivorName = survivor.Name;

                    if (!notesByTypeId.TryGetValue(redundant.Id, out var affectedNotes))
                    {
                        // No instances — delete immediately
                        doc.Delete(redundant.Id);
                        resultLines.Add($"  \u2705 {redundantName}  \u2192  {survivorName}  (0 instances)");
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
                        resultLines.Add(
                            $"  \u26A0 {redundantName}  \u2192  {survivorName}  (None reassigned \u2014 instance(s) inside a group; type left untouched)");

                        var groupInfo = groupedNotes
                            .Select(n => new
                            {
                                GroupId = n.GroupId,
                                Location = DescribeLocation(n)
                            })
                            .Distinct();

                        foreach (var gi in groupInfo)
                        {
                            glitchLogEntries.Add(
                                $"Type: \"{redundantName}\"   Group ID: {gi.GroupId.Value}   {gi.Location}");
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
                    resultLines.Add($"  \u2705 {redundantName}  \u2192  {survivorName}  ({affectedNotes.Count} reassigned)");
                }

                t.Commit();
            }

            // ── Step 9b: Write glitch log (if anything was skipped) ───────────

            string logFilePath = null;

            if (glitchLogEntries.Any())
            {
                try
                {
                    string documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string fileName = $"TextNoteType_Consolidation_Glitches_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt";
                    logFilePath = Path.Combine(documentsFolder, fileName);

                    var logLines = new List<string>
                    {
                        "Text Note Type Consolidation — Glitch Log",
                        $"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                        $"Project: {doc.Title}",
                        new string('-', 70),
                        $"{skippedTypeNames.Distinct().Count()} type(s) skipped because they have instances inside a group:",
                        ""
                    };
                    logLines.AddRange(glitchLogEntries);

                    File.WriteAllLines(logFilePath, logLines);
                }
                catch
                {
                    // If the log can't be written, still proceed — the in-app warning
                    // below still tells the user which types were skipped.
                    logFilePath = null;
                }
            }

            // ── Step 10: Dialog 5 — completion report ─────────────────────────

            string completionMessage =
                $"Consolidation complete.\n\n" +
                string.Join("\n", resultLines) +
                $"\n\n  \u2705 {reassigned} TextNote(s) reassigned in total.\n" +
                $"  \uD83D\uDDD1 {survivorMap.Count - skippedTypeNames.Count} type(s) deleted.";

            if (skippedTypeNames.Any())
            {
                completionMessage +=
                    $"\n\n  \u26A0 {skippedTypeNames.Distinct().Count()} type(s) left untouched (None reassigned) \u2014 details logged to:\n" +
                    (logFilePath != null ? $"      {logFilePath}" : "      (log could not be written)");
            }
            else
            {
                completionMessage += "\n\n  No types were skipped.";
            }

            new WarningLarge("Consolidation Complete", completionMessage, credit).ShowDialog();

            return Result.Succeeded;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string BuildComparisonKey(VM_TextNoteType vm, List<string> extraAttributes)
        {
            // Always compare font and size; round size to avoid floating-point mismatches
            var parts = new List<string>
            {
                vm.TextFont,
                RoundedSize(vm.TextSize)
            };

            foreach (string attr in extraAttributes)
            {
                switch (attr)
                {
                    case "Bold": parts.Add(vm.Bold); break;
                    case "Italic": parts.Add(vm.Italic); break;
                    case "Underline": parts.Add(vm.Underline); break;
                    case "Width Factor": parts.Add(vm.WidthFactor); break;
                    case "Background": parts.Add(vm.Background); break;
                    case "Show Border": parts.Add(vm.ShowBorder); break;
                    case "Line Weight": parts.Add(vm.LineWeight); break;
                    case "Leader Arrowhead": parts.Add(vm.LeaderArrowhead); break;
                    case "Leader Offset": parts.Add(vm.LeaderOffset); break;
                    case "Tab Size": parts.Add(vm.TabSize); break;
                    case "Colour": parts.Add(vm.Colour); break;
                }
            }

            return string.Join("|", parts);
        }

        // Strip the " mm" suffix and round to 2dp so floating-point noise doesn't
        // produce false non-matches between types with the same nominal size
        private static string RoundedSize(string textSize)
        {
            string numeric = textSize.Replace(" mm", "").Trim();
            if (double.TryParse(numeric, out double val))
                return val.ToString("F2");
            return textSize;
        }
    }
}
