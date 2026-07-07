using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace Brand_25
{
    [Transaction(TransactionMode.Manual)]
    public class Text_ConsolidateTypes : IExternalCommand
    {
        private const string credit = "Text_ConsolidateTypes v1.0";

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

            int totalReassign = 0;
            int totalInGroups = 0;
            int totalDelete = survivorMap.Count;
            var skippedDetails = new List<string>();
            var summaryLines = new List<string>();

            foreach (var kvp in survivorMap)
            {
                TextNoteType redundant = kvp.Key;
                TextNoteType survivor = kvp.Value;

                if (notesByTypeId.TryGetValue(redundant.Id, out var affectedNotes))
                {
                    int free = affectedNotes.Count(n => n.GroupId == ElementId.InvalidElementId);
                    int inGroups = affectedNotes.Count - free;
                    totalReassign += free;
                    totalInGroups += inGroups;

                    summaryLines.Add($"  \u2705 {redundant.Name}  \u2192  {survivor.Name}  ({free} reassigned)");

                    if (inGroups > 0)
                        skippedDetails.Add($"  \u26A0 {inGroups} \u00D7 \"{redundant.Name}\" inside groups \u2014 manual review needed");
                }
                else
                {
                    summaryLines.Add($"  \u2705 {redundant.Name}  \u2192  {survivor.Name}  (0 instances)");
                }
            }

            string confirmMessage =
                $"Ready to consolidate:\n\n" +
                string.Join("\n", summaryLines) +
                $"\n\n  \uD83D\uDDD1 {totalDelete} type(s) will be deleted." +
                (skippedDetails.Any()
                    ? "\n\nSkipped (inside groups \u2014 resolve manually):\n" + string.Join("\n", skippedDetails)
                    : "\n\nNo instances inside groups.");

            var confirmDialog = new Warning("Confirm Consolidation", confirmMessage, credit);
            if (confirmDialog.ShowDialog() != true)
                return Result.Succeeded;

            // ── Step 9: Execute — single transaction ──────────────────────────

            int reassigned = 0;
            var actualSkipped = new List<string>();

            using (Transaction t = new Transaction(doc, "Consolidate Text Note Types"))
            {
                t.Start();

                foreach (var kvp in survivorMap)
                {
                    TextNoteType redundant = kvp.Key;
                    TextNoteType survivor = kvp.Value;

                    if (!notesByTypeId.TryGetValue(redundant.Id, out var affectedNotes))
                    {
                        // No instances — delete immediately
                        doc.Delete(redundant.Id);
                        continue;
                    }

                    // 1. Reassign all free instances
                    foreach (TextNote note in affectedNotes)
                    {
                        if (note.GroupId == ElementId.InvalidElementId)
                        {
                            note.ChangeTypeId(survivor.Id);
                            reassigned++;
                        }
                        else
                        {
                            actualSkipped.Add($"{redundant.Name} (in group)");
                        }
                    }

                    // 2. Delete the redundant type — Revit will refuse if instances remain,
                    //    which only happens for the grouped ones; skip delete in that case
                    try
                    {
                        doc.Delete(redundant.Id);
                    }
                    catch
                    {
                        // Type still has instances in groups — leave it, report it
                    }
                }

                t.Commit();
            }

            // ── Step 10: Dialog 5 — completion report ─────────────────────────

            string completionMessage =
                $"Consolidation complete.\n\n" +
                $"  \u2705 {reassigned} TextNote(s) reassigned.\n" +
                $"  \uD83D\uDDD1 {totalDelete} type(s) processed for deletion." +
                (actualSkipped.Any()
                    ? $"\n\n  \u26A0 Skipped \u2014 inside groups (resolve manually):\n" +
                      string.Join("\n", actualSkipped.Distinct().Select(s => $"      {s}"))
                    : "\n\n  No instances were skipped.");

            new Warning("Consolidation Complete", completionMessage, credit).ShowDialog();

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