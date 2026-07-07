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
    public class Text_UnhideTemp : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            string credit = "Last Modified by Lok on 2025-03-30. Beta 0.91";

            try
            {
                // Get the active view
                View activeView = doc.ActiveView;

                // Collect all views to process
                List<View> viewsToProcess = GetViewsToProcess(doc, activeView);

                // Find all matching filters
                List<SelectionFilterElement> filters = new FilteredElementCollector(doc)
                    .OfClass(typeof(SelectionFilterElement))
                    .Cast<SelectionFilterElement>()
                    .Where(f => f.Name.Contains("Texts and Dimensions hidden at"))
                    .ToList();

                if (filters.Count == 0)
                {
                    new Warning("...um...", "You have not hidden any markup for the time being.", credit).ShowDialog();
                    //TaskDialog.Show("Info", "No hidden temp texts or dimensions were found.");
                    return Result.Succeeded;
                }

                // Process each matching filter
                using (Transaction trans = new Transaction(doc, "Unhide Temporary Text and Dimensions"))
                {
                    trans.Start();

                    foreach (SelectionFilterElement filter in filters)
                    {
                        // Unhide elements in all processed views
                        foreach (View view in viewsToProcess)
                        {
                            if (!view.IsTemplate && view.GetFilters().Contains(filter.Id))
                            {
                                view.SetFilterVisibility(filter.Id, true);
                                view.RemoveFilter(filter.Id);
                            }
                        }

                        // Delete the filter
                        doc.Delete(filter.Id);
                    }

                    trans.Commit();

                    new Warning("Unhide Markup", $"Markup unhidden. Enjoy the markup.", credit).ShowDialog();
                    //TaskDialog.Show("Success", $"Unhid elements from {filters.Count} filters and removed them from the project.");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private List<View> GetViewsToProcess(Document doc, View activeView)
        {
            List<View> viewsToProcess = new List<View>();

            if (activeView is ViewSheet sheet)
            {
                foreach (ElementId viewportId in sheet.GetAllViewports())
                {
                    if (doc.GetElement(viewportId) is Viewport viewport)
                    {
                        if (doc.GetElement(viewport.ViewId) is View view)
                        {
                            viewsToProcess.Add(view);
                        }
                    }
                }
                viewsToProcess.Add(sheet);
            }
            else
            {
                viewsToProcess.Add(activeView);
            }

            return viewsToProcess;
        }
    }
}