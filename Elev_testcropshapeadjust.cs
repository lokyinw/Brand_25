using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;

namespace Brand_25
{
    // CONTROL TEST ONLY — not wired into the main Room_CreateIntElev workflow.
    //
    // Purpose: isolate whether SetCropShape's repeated "plane parallel to view plane" /
    // "self-intersection" rejection (seen when adjusting a crop shape immediately after
    // creating the elevation, inside the same batch transaction) also happens when the
    // exact same GetCropShape -> modify Z -> SetCropShape cycle is run as a completely
    // separate action, on a view that already exists and has already been fully
    // regenerated/committed in an earlier transaction.
    //
    // How to run: make an elevation or section view with a custom crop region shape
    // active in Revit, then run this command (e.g. via Add-In Manager, or a temporary
    // ribbon button). It lowers that view's current crop top by a fixed 2 feet and
    // reports success or the exact exception message.
    [Transaction(TransactionMode.Manual)]
    public class Elev_TestCropShapeAdjust : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (!(doc.ActiveView is ViewSection elevationView))
            {
                TaskDialog.Show("Test Crop Shape", "Make an elevation or section view active before running this test.");
                return Result.Cancelled;
            }

            try
            {
                ViewCropRegionShapeManager cropShapeManager = elevationView.GetCropRegionShapeManager();
                IList<CurveLoop> cropShapes = cropShapeManager.GetCropShape();

                if (cropShapes == null || cropShapes.Count == 0)
                {
                    TaskDialog.Show("Test Crop Shape",
                        "This view's GetCropShape() returned nothing (no custom crop region shape) — nothing to test here.");
                    return Result.Cancelled;
                }

                CurveLoop originalLoop = cropShapes[0];

                // Find the shape's current top (max world Z among all its points) —
                // same technique established earlier via diagnostic logging.
                double oldTopZ = double.MinValue;
                foreach (Curve curve in originalLoop)
                {
                    oldTopZ = Math.Max(oldTopZ, curve.GetEndPoint(0).Z);
                    oldTopZ = Math.Max(oldTopZ, curve.GetEndPoint(1).Z);
                }

                // Fixed test offset — the point here is just to see whether SetCropShape
                // succeeds at all in this context, not to hit an exact target yet.
                const double testDeltaZ = -2.0;
                double newTopZ = oldTopZ + testDeltaZ;

                const double tolerance = 0.01; // feet (~1/8")
                CurveLoop newLoop = new CurveLoop();
                foreach (Curve curve in originalLoop)
                {
                    XYZ p0 = curve.GetEndPoint(0);
                    XYZ p1 = curve.GetEndPoint(1);

                    bool p0OnTop = Math.Abs(p0.Z - oldTopZ) < tolerance;
                    bool p1OnTop = Math.Abs(p1.Z - oldTopZ) < tolerance;

                    XYZ newP0 = p0OnTop ? new XYZ(p0.X, p0.Y, newTopZ) : p0;
                    XYZ newP1 = p1OnTop ? new XYZ(p1.X, p1.Y, newTopZ) : p1;

                    newLoop.Append(Line.CreateBound(newP0, newP1));
                }

                using (Transaction trans = new Transaction(doc, "LW_Test Crop Shape Adjust"))
                {
                    trans.Start();
                    cropShapeManager.SetCropShape(newLoop);
                    trans.Commit();
                }

                TaskDialog.Show("Test Crop Shape",
                    $"Success.\n\nOld top Z: {oldTopZ}\nNew top Z: {newTopZ}\n\n" +
                    "Check the view: did the crop shrink from the top while keeping its room-fitted outline?");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Test Crop Shape", $"SetCropShape failed:\n\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}