using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Brand_25
{
    [Transaction(TransactionMode.Manual)]
    public class Test_FilledRegionModifier : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            // 1. Get the current UI and Document contexts
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null) return Result.Cancelled;

            Document doc = uiDoc.Document;

            try
            {
                // 2. Prompt user to select a single filled region
                ISelectionFilter regionFilter = new FilledRegionSelectionFilter();
                Reference pickedRef = uiDoc.Selection.PickObject(
                    ObjectType.Element,
                    regionFilter,
                    "Select a Filled Region to update its <Thin Lines> segments.");

                FilledRegion filledRegion = doc.GetElement(pickedRef) as FilledRegion;
                if (filledRegion == null) return Result.Failed;

                // 3. Retrieve the GraphicStyle IDs for both "<Thin Lines>" and "<Lines>"
                ElementId thinLinesStyleId = FindLineStyleByName(doc, "<Thin Lines>");
                ElementId targetLinesStyleId = FindLineStyleByName(doc, "<Lines>");

                if (thinLinesStyleId == ElementId.InvalidElementId || targetLinesStyleId == ElementId.InvalidElementId)
                {
                    message = "Could not locate either '<Thin Lines>' or '<Lines>' style in this project.";
                    return Result.Failed;
                }

                // 4. FIX: Query the sub-components directly from the FilledRegion instance.
                // In Revit, the individual boundary loops are child CurveElements dependent on the region.
                ElementClassFilter curveFilter = new ElementClassFilter(typeof(CurveElement));
                ICollection<ElementId> dependentIds = filledRegion.GetDependentElements(curveFilter);

                int modifiedCount = 0;

                // 5. Execute modification within a transaction block
                using (Transaction tx = new Transaction(doc, "Modify Selective Filled Region Lines"))
                {
                    tx.Start();

                    foreach (ElementId curveId in dependentIds)
                    {
                        if (doc.GetElement(curveId) is CurveElement curveElem)
                        {
                            // Validate if the individual segment matches the "<Thin Lines>" style
                            if (curveElem.LineStyle.Id == thinLinesStyleId)
                            {
                                // Assign the new style cleanly to just this individual curve component
                                curveElem.LineStyle = doc.GetElement(targetLinesStyleId);
                                modifiedCount++;
                            }
                        }
                    }

                    tx.Commit();
                }

                TaskDialog.Show("Success", $"Updated {modifiedCount} boundary segment(s) from <Thin Lines> to <Lines>.");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled; // Gracefully catch ESC button clicks
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Safely retrieves a specific GraphicsStyle Id matching the UI text name.
        /// </summary>
        private ElementId FindLineStyleByName(Document doc, string styleName)
        {
            Category lineCategory = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            if (lineCategory != null)
            {
                CategoryNameMap subCategories = lineCategory.SubCategories;
                foreach (Category subCat in subCategories)
                {
                    if (subCat.Name.Equals(styleName, StringComparison.OrdinalIgnoreCase))
                    {
                        GraphicsStyle gStyle = subCat.GetGraphicsStyle(GraphicsStyleType.Projection);
                        if (gStyle != null) return gStyle.Id;
                    }
                }
            }
            return ElementId.InvalidElementId;
        }
    }

    /// <summary>
    /// Selection filter implementation to only allow FilledRegion selections in the UI.
    /// </summary>
    public class FilledRegionSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is FilledRegion;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
