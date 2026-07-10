using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Brand_25
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Text_HideTemp : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2025-08-01. Beta 0.94";

            try
            {
                // Check for existing filters first
                string filterNamePattern = "Texts and Dimensions hidden at";
                List<SelectionFilterElement> existingFilters = new FilteredElementCollector(doc)
                    .OfClass(typeof(SelectionFilterElement))
                    .Cast<SelectionFilterElement>()
                    .Where(f => f.Name.Contains(filterNamePattern))
                    .ToList();

                if (existingFilters.Count > 0)
                {
                    // Markup is already hidden — correct the split button icon in case it's out of sync.
                    App.TextSplitButton.CurrentButton = App.UnhideMarkupsButton;
                    new Warning("Oops...", "...seems you are hiding something.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                List<string> bulletPoints = new List<string> { "markup", "temp", "model", "draft" };
                BulletPointSelector selector = new BulletPointSelector("Hide Mark-ups", "Keywords in the annotation family you want to hide:", bulletPoints, credit, true, false);

                if (selector.ShowDialog() == false)
                {
                    return Result.Cancelled;
                }

                List<string> keywords = selector.SelectedItems;

                List<string> viewChoice = new List<string> { "Current View / Viewsheet", "I would like to have more control" };
                BulletPointSelector viewSelector = new BulletPointSelector("Just the active view?", "You may choose to hide the annotation in the current view/sheet or select your preferred sheets to proceed", viewChoice, credit, false, true);

                if (viewSelector.ShowDialog() == false)
                {
                    return Result.Cancelled;
                }

                // Get views to process based on user selection
                List<View> viewsToProcess = new List<View>();
                View activeView = doc.ActiveView;

                if (viewSelector.SelectedItems.Contains("Current View / Viewsheet"))
                {
                    viewsToProcess = GetViewsFromCurrentView(doc, activeView);
                }
                else
                {
                    // Let user select sheets
                    List<ViewSheet> allSheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .OrderBy(x => x.SheetNumber)
                        .ToList();

                    Selection_Sheet sheetSelector = new Selection_Sheet(allSheets, credit);
                    if (sheetSelector.ShowDialog() == true && sheetSelector.SelectedSheets != null)
                    {
                        foreach (ViewSheet sheet in sheetSelector.SelectedSheets)
                        {
                            viewsToProcess.AddRange(GetViewsFromSheet(doc, sheet));
                            viewsToProcess.Add(sheet); // Include the sheet itself
                        }
                    }
                    else
                    {
                        return Result.Cancelled;
                    }
                }

                viewsToProcess = viewsToProcess.GroupBy(v => v.Id).Select(g => g.First()).ToList();

                if (viewsToProcess.Count == 0)
                {
                    new Warning("Oops...", "No views were selected for processing.", credit).ShowDialog();
                    return Result.Cancelled;
                }

                // Collect elements to hide
                List<ElementId> elementsToHide = CollectElementsToHide(doc, viewsToProcess, keywords);

                if (elementsToHide.Count == 0)
                {
                    new Warning("Oops...", "No markup were found.", credit).ShowDialog();
                    return Result.Succeeded;
                }

                // Create and apply filter
                CreateAndApplyFilter(doc, viewsToProcess, elementsToHide, filterNamePattern, credit);

                // Markup is now hidden — show "Unhide" as the next logical action.
                App.TextSplitButton.CurrentButton = App.UnhideMarkupsButton;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private List<View> GetViewsFromCurrentView(Document doc, View activeView)
        {
            var views = new List<View>();

            if (activeView is ViewSheet sheet)
            {
                views.AddRange(GetViewsFromSheet(doc, sheet));
                views.Add(sheet); // Include the sheet itself
            }
            else
            {
                views.Add(activeView);
            }

            return views;
        }

        private List<View> GetViewsFromSheet(Document doc, ViewSheet sheet)
        {
            var views = new List<View>();

            foreach (ElementId viewportId in sheet.GetAllViewports())
            {
                if (doc.GetElement(viewportId) is Viewport viewport)
                {
                    if (doc.GetElement(viewport.ViewId) is View view)
                    {
                        views.Add(view);
                    }
                }
            }

            return views;
        }

        private List<ElementId> CollectElementsToHide(Document doc, List<View> viewsToProcess, List<string> keywords)
        {
            var elementsToHide = new List<ElementId>();

            foreach (View view in viewsToProcess)
            {
                // Text notes
                IEnumerable<TextNote> textNotes = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(TextNote))
                    .WhereElementIsNotElementType()
                    .Cast<TextNote>()
                    .Where(t => ContainsKeywords(doc.GetElement(t.GetTypeId())?.Name, keywords));

                elementsToHide.AddRange(textNotes.Select(t => t.Id));

                // Dimensions
                IEnumerable<Dimension> dimensions = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(Dimension))
                    .WhereElementIsNotElementType()
                    .Cast<Dimension>()
                    .Where(d => ContainsKeywords(doc.GetElement(d.GetTypeId())?.Name, keywords));

                elementsToHide.AddRange(dimensions.Select(d => d.Id));
            }

            return elementsToHide;
        }

        private void CreateAndApplyFilter(Document doc, List<View> viewsToProcess, List<ElementId> elementsToHide, string filterNamePattern, string credit)
        {
            using (Transaction trans = new Transaction(doc, "LW_Hide Temporary Text and Dimensions"))
            {
                trans.Start();

                string filterName = $"{filterNamePattern} {DateTime.Now.ToString("yyyy-MM-dd HHmmss")}";
                SelectionFilterElement filter = SelectionFilterElement.Create(doc, filterName);

                filter.SetElementIds(new HashSet<ElementId>(elementsToHide));

                foreach (View view in viewsToProcess)
                {
                    if (!view.IsTemplate)
                    {
                        view.AddFilter(filter.Id);
                        view.SetFilterVisibility(filter.Id, false);
                    }
                }

                trans.Commit();

                new Warning("Hide Markup", $"Hidden {elementsToHide.Count} elements in {viewsToProcess.Count} views.\n\n" +
                    "Remember to unhide them before you go further.", credit).ShowDialog();
            }
        }

        private bool ContainsKeywords(string typeName, List<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return false;

            string lowerName = typeName.ToLower();
            return keywords.Any(keyword => lowerName.Contains(keyword.ToLower()));
        }
    }
}