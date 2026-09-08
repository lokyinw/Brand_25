using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Brand_25
{
    // IExternalEventHandler that applies the element tree's checked instances as
    // the active Revit selection. A modeless WPF window's own click/property-
    // changed handlers are NOT running inside a genuine Revit API context, so
    // writes to Revit state — even a UI-only write like Selection.SetElementIds —
    // are routed through this ExternalEvent rather than called directly from the
    // window.
    //
    // Deliberately does NOT zoom the view — that's a separate, independent action
    // (tied to highlighting a tree row, not to checking a box) handled by
    // ElementTreeZoomHandler below, via its own ExternalEvent.
    public class ElementTreeSelectionHandler : IExternalEventHandler
    {
        private readonly UIDocument _uidoc;

        // Set by Selection_ElementTree just before calling ExternalEvent.Raise();
        // read back here once Revit actually invokes Execute(). If several
        // checkbox toggles happen in quick succession, Raise() coalesces them —
        // only the latest PendingIds value at the time Execute() actually runs
        // matters, so this doesn't need its own queue.
        public List<ElementId> PendingIds { get; set; } = new List<ElementId>();

        public ElementTreeSelectionHandler(UIDocument uidoc)
        {
            _uidoc = uidoc;
        }

        public void Execute(UIApplication app)
        {
            try
            {
                if (_uidoc?.Document == null || !_uidoc.Document.IsValidObject) return;
                _uidoc.Selection.SetElementIds(PendingIds);
            }
            catch
            {
                // Document may have closed, or the active view may momentarily
                // reject a selection change, between Raise() and Execute() actually
                // running — nothing useful to do here; the next toggle or the next
                // live rebuild will simply try again.
            }
        }

        public string GetName() => "Brand_25 - Element Tree Selection Update";
    }

    // IExternalEventHandler that zooms the active view to a single element's
    // extent. Kept entirely separate from ElementTreeSelectionHandler above —
    // zooming is triggered by highlighting a row in the tree
    // (TreeView.SelectedItemChanged), an action with nothing to do with the
    // checkbox/selection-push machinery, so it gets its own ExternalEvent rather
    // than piggybacking on the selection one.
    public class ElementTreeZoomHandler : IExternalEventHandler
    {
        private readonly UIDocument _uidoc;

        public ElementId PendingZoomId { get; set; } = ElementId.InvalidElementId;

        public ElementTreeZoomHandler(UIDocument uidoc)
        {
            _uidoc = uidoc;
        }

        public void Execute(UIApplication app)
        {
            try
            {
                if (_uidoc?.Document == null || !_uidoc.Document.IsValidObject) return;
                if (PendingZoomId == null || PendingZoomId == ElementId.InvalidElementId) return;

                ZoomToElement(_uidoc, PendingZoomId);
            }
            catch
            {
                // Zoom is a nice-to-have — a failure here (e.g. unsupported view
                // type, element deleted between Raise() and Execute()) shouldn't
                // be treated as a real error.
            }
        }

        // Zooms the active view to the element's bounding box, padded by ~20% of
        // its own largest dimension ("showing a bit around it") so the element
        // doesn't fill the view edge-to-edge.
        private static void ZoomToElement(UIDocument uidoc, ElementId id)
        {
            try
            {
                Document doc = uidoc.Document;
                Element elem = doc.GetElement(id);
                View activeView = doc.ActiveView;
                if (elem == null || activeView == null) return;

                BoundingBoxXYZ bbox = elem.get_BoundingBox(activeView) ?? elem.get_BoundingBox(null);
                if (bbox == null) return;

                XYZ size = bbox.Max - bbox.Min;
                double marginFt = Math.Max(size.X, Math.Max(size.Y, size.Z)) * 0.2;
                if (marginFt < 1.0) marginFt = 1.0; // floor so a paper-thin element still gets sensible padding
                XYZ marginVec = new XYZ(marginFt, marginFt, marginFt);

                UIView uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == activeView.Id);
                uiView?.ZoomAndCenterRectangle(bbox.Min - marginVec, bbox.Max + marginVec);
            }
            catch
            {
                // See the comment in Execute() above — non-essential.
            }
        }

        public string GetName() => "Brand_25 - Element Tree Zoom To Highlighted Row";
    }
}
