using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Brand_25
{
    // Gathers EVERY element in the project whose Category.CategoryType == Model
    // (Doors, Walls, Furniture, Structural Framing, Rooms, ... — physical/spatial
    // elements, as opposed to annotations, tags, or internal/settings categories)
    // into a Categories -> Category -> Family -> Type -> Instance tree, in a
    // MODELESS window that stays open, stays pinned above Revit, and stays in
    // sync with the model:
    //   - Checking any box updates a persistent checked-id set and pushes ALL of
    //     it to Revit's selection via an ExternalEvent — elements checked under a
    //     different scope/view remain selected even once they're no longer
    //     displayed in the tree. A modeless window cannot call Revit API methods
    //     directly and safely from its own event handlers, hence the ExternalEvent.
    //   - Highlighting (not checking) a single Instance row zooms the active view
    //     to that element, via a SEPARATE ExternalEvent (ElementTreeZoomHandler) —
    //     entirely independent of the checkbox/selection-push machinery.
    //   - Deleting/retyping an element, or switching the active view (while scope
    //     is Active View), triggers a live rebuild via Application.DocumentChanged
    //     / UIApplication.ViewActivated respectively.
    //   - A "Show:" dropdown scopes the dataset to the Entire Project, the Active
    //     View, or the Current Selection (see ElementTreeScope). Defaults to
    //     Current Selection if anything is already selected when the command
    //     runs, otherwise Active View.
    //
    // Only one instance of the window is kept per Revit session — clicking the
    // ribbon button again just brings the existing window to the front instead of
    // creating a duplicate (and duplicate event subscriptions).
    //
    // LIMITATION: tracks exactly one Document — the one active when the window was
    // opened. If the user switches to a different open document, events from that
    // other document are ignored (see the ReferenceEquals checks in
    // Selection_ElementTree's OnDocumentChanged/OnDocumentClosing).
    //
    // NOTE on scale: "Entire Project" now spans every model category, not just
    // Doors — on a large project this can be a genuinely large tree (tens of
    // thousands of elements). The tree only eagerly renders the top-level Category
    // rows (everything below a Category defaults to collapsed) and uses recycling
    // virtualization throughout, but "Active View" is still the better default for
    // a sluggish-feeling large project.
    [Transaction(TransactionMode.ReadOnly)]
    public class Element_SelectByTree : IExternalCommand
    {
        private const string credit = "Last Modified by Lok on 2026-09-08. Beta 0.60 - generalized from Doors trial to all model categories";

        private static Selection_ElementTree _openWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uidoc = uiApp.ActiveUIDocument;
            Document doc = uidoc.Document;
            Autodesk.Revit.ApplicationServices.Application app = uiApp.Application;

            // Already open from an earlier click this session — bring it forward
            // rather than spinning up a second live tree against the same document.
            if (_openWindow != null && _openWindow.IsLoaded)
            {
                _openWindow.Activate();
                if (_openWindow.WindowState == WindowState.Minimized)
                    _openWindow.WindowState = WindowState.Normal;
                return Result.Succeeded;
            }

            List<Element> allElements = CollectAllModelElements(doc);
            if (allElements.Count == 0)
            {
                new Warning("No Elements", "No model elements were found in the project.", credit).ShowDialog();
                return Result.Cancelled;
            }

            // Default scope: if the user already had something selected before
            // launching this tool, assume they want to work with that; otherwise
            // default to whatever's visible in the view they're currently looking at.
            ElementTreeScope initialScope = uidoc.Selection.GetElementIds().Count > 0
                ? ElementTreeScope.CurrentSelection
                : ElementTreeScope.ActiveView;

            ElementTreeSelectionHandler selectionHandler = new ElementTreeSelectionHandler(uidoc);
            ExternalEvent selectionEvent = ExternalEvent.Create(selectionHandler);

            ElementTreeZoomHandler zoomHandler = new ElementTreeZoomHandler(uidoc);
            ExternalEvent zoomEvent = ExternalEvent.Create(zoomHandler);

            Selection_ElementTree window = new Selection_ElementTree(
                doc, uidoc, selectionHandler, selectionEvent, zoomHandler, zoomEvent, initialScope, credit);

            // DocumentChanged/DocumentClosing live on Application; ViewActivated
            // lives on UIApplication — two different objects, both subscribed here.
            app.DocumentChanged += window.OnDocumentChanged;
            app.DocumentClosing += window.OnDocumentClosing;
            uiApp.ViewActivated += window.OnViewActivated;

            // Guaranteed cleanup: whenever/however the window closes, unsubscribe
            // everything so the NEXT ribbon click starts fresh instead of finding
            // a stale, closed window.
            //
            // The actual unsubscription is DEFERRED (Dispatcher.BeginInvoke)
            // rather than run synchronously here: when the TRACKED document
            // itself closes, OnDocumentClosing calls window.Close() — which fires
            // this very Closed handler WHILE Revit is still in the middle of
            // dispatching the DocumentClosing event. Immediately unsubscribing
            // from that same event, from inside its own in-progress dispatch, is
            // exactly the kind of reentrant event-list mutation that has been
            // observed to throw. Posting the real cleanup to the next pass
            // through the UI thread's message loop lets Revit's current dispatch
            // finish first. Wrapped in try/catch too, as a safety net regardless
            // of the exact cause — a failed unsubscribe here is a minor leak at
            // worst (cleared out when Revit itself shuts down), not something
            // worth crashing the session over.
            window.Closed += (s, e) =>
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        app.DocumentChanged -= window.OnDocumentChanged;
                        app.DocumentClosing -= window.OnDocumentClosing;
                        uiApp.ViewActivated -= window.OnViewActivated;
                    }
                    catch
                    {
                        // See comment above — swallow rather than propagate.
                    }
                }), DispatcherPriority.Background);

                // Safe to clear immediately (pure CLR field assignment, no Revit
                // API involved) so a rapid re-click of the ribbon button right
                // after closing sees a clean slate rather than a stale reference.
                _openWindow = null;
            };

            _openWindow = window;
            window.Show(); // modeless — Execute() returns immediately, window stays live

            return Result.Succeeded;
        }

        // Mirrors Selection_ElementTree.IsIncludedModelElement/CollectAllModelElements
        // exactly, duplicated here only because this check needs to run BEFORE
        // the window (and therefore that helper) exists. Filters by the
        // element's ACTUAL Category.CategoryType rather than a closed
        // BuiltInCategory allow-list, so CAD import layers and other
        // dynamically-created categories (which report BuiltInCategory.INVALID)
        // are included too — then excludes a specific list of category names
        // (see ExcludedCategoryNames) regardless of CategoryType.
        private static List<Element> CollectAllModelElements(Document doc)
        {
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(IsIncludedModelElement)
                .ToList();
        }

        // Kept identical to Selection_ElementTree.ExcludedCategoryNames — see
        // that copy's comment for why these are matched by display name rather
        // than BuiltInCategory.
        private static readonly HashSet<string> ExcludedCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Material",
            "Materials",
            "Material Assets",
            "Sun Path",
            "Sheets",
            "Railing Rail Path Extension Lines",
            "Project Information",
            "Legend Components",
            "Areas",
            "<Sketch>",
        };

        // Prefix-matched — see Selection_ElementTree.ExcludedCategoryNamePrefixes
        // for why "<Stair/Ramp Sketch>" needs this instead of an exact match.
        private static readonly string[] ExcludedCategoryNamePrefixes =
        {
            "<Stair/Ramp Sketch",
        };

        private static bool IsIncludedModelElement(Element e)
        {
            try
            {
                if (e.Category == null || e.Category.CategoryType != CategoryType.Model) return false;

                string name = e.Category.Name;
                if (ExcludedCategoryNames.Contains(name)) return false;

                foreach (string prefix in ExcludedCategoryNamePrefixes)
                {
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
