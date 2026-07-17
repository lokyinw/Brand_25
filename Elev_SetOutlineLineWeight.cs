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
    // Overrides the projection line weight on every element visible in each internal
    // elevation view of the user-selected ViewFamilyType. Ported from a Dynamo Python
    // graph that looped views matching a substring on "Type Name" and set overrides on
    // each element found in that view under BuiltInCategory.OST_Viewers.
    //
    // Differences from the original Python graph:
    //   - View-type matching is now an exact ViewFamilyType.Id match against a value
    //     picked from a dropdown (Input_LineWeight), rather than a substring match on
    //     a "Type Name" parameter string — this can't accidentally pick up an unrelated
    //     view whose type name happens to contain the same text.
    //   - Line weight is constrained to the valid 1-16 range by the dialog itself
    //     (a dropdown of exactly those values), so no separate validation is needed here.
    //   - One transaction wraps the whole operation instead of one transaction per view.
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Elev_SetOutlineLineWeight : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2026-07-17. Beta 0.1";

            StringBuilder log = new StringBuilder();
            List<string> issues = new List<string>();

            try
            {
                // Step 1: collect elevation view family types for the dialog dropdown.
                List<ViewFamilyType> elevationTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .Where(vft => vft.ViewFamily == ViewFamily.Elevation)
                    .ToList();

                if (elevationTypes.Count == 0)
                {
                    new Warning("Oops...", "No elevation view types found in the document.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Step 2: gather user input (view type + line weight).
                Input_LineWeight inputWindow = new Input_LineWeight(elevationTypes, credit);
                if (inputWindow.ShowDialog() != true)
                {
                    return Result.Cancelled;
                }

                ViewFamilyType selectedType = inputWindow.SelectedViewFamilyType;
                int lineWeight = inputWindow.LineWeight;
                bool isRestore = inputWindow.IsRestore;

                // Step 3: collect elevation views of the selected type (exact type-id
                // match, rather than the original script's substring match on a
                // "Type Name" parameter string).
                List<View> matchingViews = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Views)
                    .WhereElementIsNotElementType()
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.GetTypeId() == selectedType.Id)
                    .ToList();

                if (matchingViews.Count == 0)
                {
                    new Warning("Oops...", "No views found matching the selected elevation type.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                log.AppendLine($"Found {matchingViews.Count} view(s) of type '{selectedType.Name}':");
                foreach (View v in matchingViews) log.AppendLine($"  {v.Name}");

                // Step 4: build the override once and apply it to every element found
                // in each matching view. An empty OverrideGraphicSettings clears any
                // existing overrides on that element in that view — that's how Restore
                // resets things back to the view's default graphics.
                OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                if (!isRestore)
                {
                    ogs.SetProjectionLineWeight(lineWeight);
                }

                int viewsEdited = 0;
                List<View> viewsWithNoElements = new List<View>();

                string transactionName = isRestore ? "LW_Restore Elevation Outline Overrides" : "LW_Set Elevation Outline Line Weight";
                using (Transaction trans = new Transaction(doc, transactionName))
                {
                    trans.Start();

                    foreach (View vw in matchingViews)
                    {
                        List<Element> viewers = new FilteredElementCollector(doc, vw.Id)
                            .OfCategory(BuiltInCategory.OST_Viewers)
                            .ToList();

                        if (viewers.Count == 0)
                        {
                            viewsWithNoElements.Add(vw);
                            string issue = $"View '{vw.Name}': no elements found to override (nothing to do).";
                            log.AppendLine($"Warning: {issue}");
                            issues.Add(issue);
                            continue;
                        }

                        foreach (Element e in viewers)
                        {
                            vw.SetElementOverrides(e.Id, ogs);
                        }
                        viewsEdited++;
                    }

                    trans.Commit();
                }

                // Step 5: show summary.
                string summary = isRestore
                    ? $"Overrides restored to default on {viewsEdited} of {matchingViews.Count} view(s)."
                    : $"Line weight {lineWeight} applied to {viewsEdited} of {matchingViews.Count} view(s).";
                if (viewsWithNoElements.Count > 0)
                {
                    summary += $"\n\n{viewsWithNoElements.Count} view(s) had nothing to override — see the log for details.";
                }
                new Warning("Success", summary, credit).ShowDialog();

                log.AppendLine();
                log.AppendLine(summary);
                if (issues.Count > 0)
                {
                    log.AppendLine();
                    log.AppendLine($"=== {issues.Count} Issue(s) ===");
                    foreach (string issue in issues) log.AppendLine(issue);
                }

                string logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ElevSetOutlineLineWeightLog.txt");
                try
                {
                    File.WriteAllText(logFilePath, log.ToString());
                }
                catch
                {
                    // Non-critical — don't fail the whole command over a log file write issue.
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}\n\nDebug Log:\n{log}");
                return Result.Failed;
            }
        }
    }
}
