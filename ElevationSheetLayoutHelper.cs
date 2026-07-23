using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Brand_25
{
    // Shared, type-agnostic pieces of the "place elevations on a sheet" workflow —
    // extracted out of Elev_PlaceOnSheets.cs so that Win_PlaceElevOnSheets.cs (window/
    // door elevations) can reuse the exact same paper-space math, row-packing/sheet-
    // overflow algorithm, sheet-creation logic, and level-line cleanup, instead of a
    // second, drifting copy of ~300 lines. Nothing in here is aware of Rooms, Windows,
    // or Doors — callers resolve their own "which View, which Level" pairing (Room-based
    // for internal elevations, host-wall/door-based for window/door elevations) and pass
    // the results in.
    public static class ElevationSheetLayoutHelper
    {
        // 1 ft = 304.8 mm.
        public const double MmToFeet = 0.0032808;
        public const double FeetToMm = 304.8;

        // --- Empirical compensation constants ------------------------------------
        // Viewport.SetBoxCenter() positions the crop+title envelope, not the crop
        // itself, leaving a small residual constant offset (title/label padding)
        // even after the ComputeLayout fix. These two are that residual, read
        // directly off a clean test: top margin measured 12.8mm against an intended
        // 15mm (2.2mm short), left margin measured 23.25mm against an intended 25mm
        // (1.75mm short). If a future test shows drift, adjust these two directly.
        private const double TopMarginCompensationMm = -2.2;
        private const double LeftMarginCompensationMm = 1.75;
        // ---------------------------------------------------------------------------

        // Extra paper-space extension given to the bubble end of a row-ending view,
        // on top of LevelExtensionMm, so there's room to read the level bubble/text.
        private const double RowEndBubbleExtraMm = 40.0;

        public class LayoutResult
        {
            public List<double> PlaceX = new List<double>();
            public List<double> PlaceY = new List<double>();
            public List<int> PlaceSheetIndex = new List<int>();
            public List<bool> IsRowEnd = new List<bool>();
            public int MaxSheetIndex;
        }

        // Derives the content area's four edges (paper space, feet) from paper size +
        // frame margin (paper edge -> title frame) + content margin (title frame ->
        // drawing area), exactly as Elev_PlaceOnSheets originally computed them.
        // ContentMarginRightMm doubles as the "Drawing Information Area" width, since
        // that's what actually bounds the drawing area on the right for this title
        // block. Changing to a different title block or paper size only ever means
        // changing the Selection_SheetLayout dialog's inputs — nothing here changes.
        public static (double leftEdgeFt, double rightEdgeFt, double topOfSheetFt, double lowerEdgeFt, double xSpacingFt, double ySpacingFt)
            ComputeContentEdges(Selection_SheetLayout inputWindow)
        {
            double leftEdgeFt = (inputWindow.FrameMarginLeftMm + inputWindow.ContentMarginLeftMm + LeftMarginCompensationMm) * MmToFeet;
            double rightEdgeFt = (inputWindow.PaperWidthMm - inputWindow.FrameMarginRightMm - inputWindow.ContentMarginRightMm) * MmToFeet;
            double topOfSheetFt = (inputWindow.PaperHeightMm - inputWindow.FrameMarginTopMm - inputWindow.ContentMarginTopMm + TopMarginCompensationMm) * MmToFeet;
            double lowerEdgeFt = (inputWindow.FrameMarginBottomMm + inputWindow.ContentMarginBottomMm) * MmToFeet;

            double xSpacingFt = inputWindow.XSpacingMm * MmToFeet;
            double ySpacingFt = inputWindow.YSpacingMm * MmToFeet;

            return (leftEdgeFt, rightEdgeFt, topOfSheetFt, lowerEdgeFt, xSpacingFt, ySpacingFt);
        }

        // Exact type + phase match (rather than a substring match on a parameter
        // string), so this can't accidentally pick up an unrelated view whose type or
        // phase name happens to contain the same text. Used identically by internal
        // elevations and window/door elevations — in this project the two always use
        // different ViewFamilyTypes, so no further disambiguation is needed here.
        public static List<View> FindMatchingElevationViews(Document doc, ViewFamilyType elevationType, Phase phase)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.ViewType == ViewType.Elevation
                            && !v.IsTemplate
                            && v.GetTypeId() == elevationType.Id
                            && v.get_Parameter(BuiltInParameter.VIEW_PHASE) != null
                            && v.get_Parameter(BuiltInParameter.VIEW_PHASE).AsElementId() == phase.Id)
                .ToList();
        }

        // Splits a name into digit / non-digit runs and pads digit runs to a fixed width,
        // so an ordinary string sort on the result produces natural numeric ordering
        // (e.g. "9" sorts before "10", "W2.1" before "W10.1").
        public static string NaturalSortKey(string name)
        {
            return Regex.Replace(name, "[0-9]+", match => match.Value.PadLeft(10, '0'));
        }

        public static ViewSheet FindSheetByNumber(Document doc, string number)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Sheets)
                .WhereElementIsNotElementType()
                .Cast<ViewSheet>()
                .FirstOrDefault(s => s.get_Parameter(BuiltInParameter.SHEET_NUMBER).AsString() == number);
        }

        // Reads a view's crop box (paper space, via CropBox / view.Scale) and the
        // given Level's line position within that view, producing the width/height/
        // dist inputs ComputeLayout needs. Returns false (dist/levelZ left at 0) if
        // the level is null or has no curve in this view — callers decide how to log
        // that (message wording differs slightly between the Room-based and
        // host-element-based callers). errorMessage is set only if GetCurvesInView
        // itself threw.
        public static bool TryMeasureViewAlignment(View v, Level level,
            out double widthFt, out double heightFt, out double dist, out double levelZ, out string errorMessage)
        {
            errorMessage = null;
            dist = 0;
            levelZ = 0;

            BoundingBoxXYZ cropBox = v.CropBox;
            double cropWidth = cropBox.Max.X - cropBox.Min.X;   // model space
            double cropHeight = cropBox.Max.Y - cropBox.Min.Y;  // model space
            double viewScale = v.Scale;

            widthFt = cropWidth / viewScale;
            heightFt = cropHeight / viewScale;

            if (level == null) return false;

            // View.CropBox is in the view's own LOCAL coordinate system, where Y is vertical.
            double cropCenterY = (cropBox.Min.Y + cropBox.Max.Y) / 2.0;
            try
            {
                IList<Curve> curves = level.GetCurvesInView(DatumExtentType.Model, v);
                if (curves != null && curves.Count > 0)
                {
                    levelZ = curves[0].GetEndPoint(0).Z;
                    dist = (cropCenterY - levelZ) / viewScale;
                    return true;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
            }
            return false;
        }

        // Packs views into rows left-to-right using an EXPLICIT half-width formula
        // (half of the current view + spacing + half of the next view), so
        // xSpacingFt has its literal meaning: the paper-space gap between two
        // viewports' crop edges. A row wraps to a new one when the next view's
        // right edge would cross rightEdgeFt.
        //
        // Once a row is complete, it's aligned so every view's Level line lines up
        // (each item's own dist offset from a shared per-row baseline). That baseline
        // is placed so the row's HIGHEST point (topOffset = dist + height/2, the
        // largest such value in the row) touches placeY exactly — NOT so the row's
        // tallest height and largest dist independently subtract from placeY, which
        // is what this used to do. Those aren't the same thing unless every item in
        // every row happens to share an identical dist-to-height ratio, and they
        // don't: the size of the resulting error tracked directly with each row's
        // own crop heights, which is why margins/gaps came out a different amount
        // "too generous" on every row instead of a single fixed offset.
        //
        // If that aligned position would put the row's LOWEST point (bottomOffset =
        // dist - height/2, the smallest such value in the row) below lowerEdgeFt, the
        // row instead starts a new sheet from topOfSheetFt.
        public static LayoutResult ComputeLayout(List<double> widthsFt, List<double> heightsFt, List<double> distsFt,
            double leftEdgeFt, double xSpacingFt, double ySpacingFt, double rightEdgeFt, double topOfSheetFt, double lowerEdgeFt)
        {
            LayoutResult result = new LayoutResult();
            for (int i = 0; i < widthsFt.Count; i++)
            {
                result.PlaceX.Add(0);
                result.PlaceY.Add(0);
                result.PlaceSheetIndex.Add(0);
                result.IsRowEnd.Add(false);
            }

            double placeX = leftEdgeFt;
            double placeY = topOfSheetFt;
            double prevHalfWidth = 0;
            bool rowHasItems = false;
            int sheetIndex = 0;
            int rowStart = 0;

            // topOffset = how far this item's crop TOP sits above the row's shared
            // level-line baseline; bottomOffset = how far its crop BOTTOM sits below
            // (or above, if negative) that same baseline.
            List<double> rowTopOffsets = new List<double>();
            List<double> rowBottomOffsets = new List<double>();

            void FinalizeRow(int rowStartIndex, int rowEndIndex)
            {
                double alignY = placeY - rowTopOffsets.Max();

                if (alignY + rowBottomOffsets.Min() < lowerEdgeFt)
                {
                    sheetIndex++;
                    placeY = topOfSheetFt;
                    alignY = placeY - rowTopOffsets.Max();
                }

                for (int i = rowStartIndex; i <= rowEndIndex; i++)
                {
                    result.PlaceY[i] = alignY + distsFt[i];
                    result.PlaceSheetIndex[i] = sheetIndex;
                }
                result.IsRowEnd[rowEndIndex] = true;

                // Next row starts below whichever view in THIS row extends furthest
                // down — i.e. the row's true lowest point, not just its last item.
                placeY = alignY + rowBottomOffsets.Min() - ySpacingFt;

                rowTopOffsets.Clear();
                rowBottomOffsets.Clear();
            }

            for (int i = 0; i < widthsFt.Count; i++)
            {
                double halfWidth = widthsFt[i] / 2.0;

                placeX = rowHasItems
                    ? placeX + prevHalfWidth + xSpacingFt + halfWidth  // half-current + gap + half-next
                    : leftEdgeFt + halfWidth;                          // first item in row: flush to left edge

                if (rowHasItems && placeX + halfWidth > rightEdgeFt)
                {
                    // Doesn't fit — finalize the row accumulated so far, then start a
                    // new row with the current view.
                    FinalizeRow(rowStart, i - 1);
                    rowStart = i;
                    rowHasItems = false;
                    placeX = leftEdgeFt + halfWidth;
                }

                result.PlaceX[i] = placeX;
                rowTopOffsets.Add(distsFt[i] + heightsFt[i] / 2.0);
                rowBottomOffsets.Add(distsFt[i] - heightsFt[i] / 2.0);
                prevHalfWidth = halfWidth;
                rowHasItems = true;

                if (i == widthsFt.Count - 1)
                {
                    FinalizeRow(rowStart, i);
                }
            }

            result.MaxSheetIndex = sheetIndex;
            return result;
        }

        // Creates every additional sheet the layout needs (by duplicating the
        // starting sheet's title block, incrementing sheet number/name, copying
        // BA_SheetSeries, setting BA_SRT_Level 01), in its own transaction — matching
        // Elev_PlaceOnSheets' original Step 7 exactly. Returns the full sheet list,
        // starting sheet included at index 0.
        public static List<ViewSheet> CreateAdditionalSheets(Document doc, ViewSheet startingSheet, int maxSheetIndex,
            StringBuilder log, List<string> issues)
        {
            List<ViewSheet> sheetList = new List<ViewSheet> { startingSheet };

            using (Transaction sheetTrans = new Transaction(doc, "LW_Create Additional Sheets"))
            {
                sheetTrans.Start();

                // Check if the starting sheet name needs the " 01" suffix applied
                string currentName = startingSheet.get_Parameter(BuiltInParameter.SHEET_NAME).AsString();
                if (!TryIncrementLastDigit(currentName, out _))
                {
                    startingSheet.get_Parameter(BuiltInParameter.SHEET_NAME).Set($"{currentName} 1");
                    log.AppendLine($"Renamed initial sheet to: {startingSheet.SheetNumber} - {currentName} 1");
                }

                ViewSheet current = startingSheet;
                for (int i = 1; i <= maxSheetIndex; i++)
                {
                    ViewSheet nextSheet = CreateNextSheet(doc, current, log, issues);
                    if (nextSheet == null)
                    {
                        string issue = "Failed to create an additional sheet; remaining views will be skipped.";
                        log.AppendLine($"Error: {issue}");
                        issues.Add(issue);
                        break;
                    }
                    sheetList.Add(nextSheet);
                    current = nextSheet;
                }

                sheetTrans.Commit();
            }

            return sheetList;
        }

        // Places every view at its computed position (its own transaction), logging
        // both the target and the actual resulting geometry post-placement — matching
        // Elev_PlaceOnSheets' original Step 8 exactly.
        public static (int placedCount, List<int> placedIndices) PlaceViewportsOnSheets(Document doc, List<View> elevationViews,
            LayoutResult layout, List<ViewSheet> sheetList, StringBuilder log, List<string> issues)
        {
            int placedCount = 0;
            List<int> placedIndices = new List<int>();

            log.AppendLine();
            log.AppendLine("=== Placement Data (tab-separated; paste into Excel) ===");
            log.AppendLine(string.Join("\t",
                "ViewName", "SheetIndex", "SheetNumber", "IsRowEnd",
                "TargetX_mm", "TargetY_mm",
                "EnvelopeMinX_mm", "EnvelopeMaxX_mm", "EnvelopeMinY_mm", "EnvelopeMaxY_mm",
                "LabelMinX_mm", "LabelMaxX_mm", "LabelMinY_mm", "LabelMaxY_mm"));

            using (Transaction placeTrans = new Transaction(doc, "LW_Place Elevations on Sheet"))
            {
                placeTrans.Start();

                for (int i = 0; i < elevationViews.Count; i++)
                {
                    int sheetIndex = layout.PlaceSheetIndex[i];
                    if (sheetIndex >= sheetList.Count)
                    {
                        string issue = $"View '{elevationViews[i].Name}': target sheet could not be created; skipped.";
                        log.AppendLine($"Error: {issue}");
                        issues.Add(issue);
                        continue;
                    }

                    Viewport vp = Viewport.Create(doc, sheetList[sheetIndex].Id, elevationViews[i].Id, XYZ.Zero);
                    if (vp == null)
                    {
                        string issue = $"View '{elevationViews[i].Name}': failed to place on sheet.";
                        log.AppendLine($"Error: {issue}");
                        issues.Add(issue);
                        continue;
                    }

                    vp.SetBoxCenter(new XYZ(layout.PlaceX[i], layout.PlaceY[i], 0));
                    placedCount++;
                    placedIndices.Add(i);

                    // Log the ACTUAL resulting geometry, post-placement, in sheet
                    // (paper) space — this is the ground truth to compare against
                    // physical sheet measurements.
                    Outline placedEnvelope = vp.GetBoxOutline();
                    Outline placedLabel = vp.GetLabelOutline();
                    log.AppendLine(string.Join("\t",
                        elevationViews[i].Name, sheetIndex.ToString(), sheetList[sheetIndex].SheetNumber, layout.IsRowEnd[i].ToString(),
                        (layout.PlaceX[i] * FeetToMm).ToString("F3"), (layout.PlaceY[i] * FeetToMm).ToString("F3"),
                        (placedEnvelope.MinimumPoint.X * FeetToMm).ToString("F3"), (placedEnvelope.MaximumPoint.X * FeetToMm).ToString("F3"),
                        (placedEnvelope.MinimumPoint.Y * FeetToMm).ToString("F3"), (placedEnvelope.MaximumPoint.Y * FeetToMm).ToString("F3"),
                        placedLabel != null ? (placedLabel.MinimumPoint.X * FeetToMm).ToString("F3") : "N/A",
                        placedLabel != null ? (placedLabel.MaximumPoint.X * FeetToMm).ToString("F3") : "N/A",
                        placedLabel != null ? (placedLabel.MinimumPoint.Y * FeetToMm).ToString("F3") : "N/A",
                        placedLabel != null ? (placedLabel.MaximumPoint.Y * FeetToMm).ToString("F3") : "N/A"));
                }

                placeTrans.Commit();
            }

            return (placedCount, placedIndices);
        }

        // Hides the bubble at both ends of every level visible in the view, and
        // repositions each level line's endpoints so exactly levelExtensionMm of
        // paper-space extension remains beyond the crop edge on each side (plus
        // RowEndBubbleExtraMm on the bubble side, only for the view that ends its
        // row) — targets an absolute remaining length rather than a relative trim,
        // since each level line's untrimmed overhang past the crop isn't the same on
        // every view. Type-agnostic: works the same whether the view came from an
        // internal (room) elevation or a window/door elevation.
        public static void UpdateLevelDisplay(Document doc, View view, bool isRowEnd, double levelExtensionMm, StringBuilder log, List<string> issues)
        {
            List<Level> levelsInView = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Levels)
                .Cast<Level>()
                .ToList();

            if (levelsInView.Count == 0) return;

            Transform transform = view.CropBox.Transform;
            BoundingBoxXYZ cropBox = view.CropBox;
            double viewScale = view.Scale;

            // Paper mm -> model feet: paper_mm * scale gives model mm, then * MmToFeet
            // gives model feet (matches the convention used everywhere else in this file).
            double standardExtensionFt = levelExtensionMm * viewScale * MmToFeet;
            double bubbleEndExtensionFt = (levelExtensionMm + RowEndBubbleExtraMm) * viewScale * MmToFeet;

            foreach (Level level in levelsInView)
            {
                try
                {
                    IList<Curve> curves = level.GetCurvesInView(DatumExtentType.ViewSpecific, view);
                    if (curves == null || curves.Count == 0) continue;

                    Curve curve = curves[0];
                    XYZ ep0 = curve.GetEndPoint(0);
                    XYZ ep1 = curve.GetEndPoint(1);

                    // Local (view crop) coordinates preserve length, since Transform is
                    // rigid (rotation + translation only) — so a distance measured here
                    // equals the same real-world distance, just easier to compare
                    // against the crop box, which is already expressed in this frame.
                    XYZ localP0 = transform.Inverse.OfPoint(ep0);
                    XYZ localP1 = transform.Inverse.OfPoint(ep1);
                    bool end0IsRight = localP0.X > localP1.X;
                    DatumEnds rightEnd = end0IsRight ? DatumEnds.End0 : DatumEnds.End1;
                    DatumEnds leftEnd = end0IsRight ? DatumEnds.End1 : DatumEnds.End0;

                    if (isRowEnd)
                    {
                        // Keep the right-hand bubble to mark this row's floor; hide the left.
                        if (level.IsBubbleVisibleInView(leftEnd, view)) level.HideBubbleInView(leftEnd, view);
                    }
                    else
                    {
                        if (level.IsBubbleVisibleInView(DatumEnds.End0, view)) level.HideBubbleInView(DatumEnds.End0, view);
                        if (level.IsBubbleVisibleInView(DatumEnds.End1, view)) level.HideBubbleInView(DatumEnds.End1, view);
                    }

                    // The right end gets the bubble-side allowance only when this view
                    // is a row-end (the only case where its bubble stays visible).
                    double rightExtensionFt = isRowEnd ? bubbleEndExtensionFt : standardExtensionFt;
                    double leftExtensionFt = standardExtensionFt;

                    double newRightLocalX = cropBox.Max.X + rightExtensionFt;
                    double newLeftLocalX = cropBox.Min.X - leftExtensionFt;

                    double newLocalX0 = end0IsRight ? newRightLocalX : newLeftLocalX;
                    double newLocalX1 = end0IsRight ? newLeftLocalX : newRightLocalX;

                    XYZ newEp0 = transform.OfPoint(new XYZ(newLocalX0, localP0.Y, localP0.Z));
                    XYZ newEp1 = transform.OfPoint(new XYZ(newLocalX1, localP1.Y, localP1.Z));

                    Curve newCurve = Line.CreateBound(newEp0, newEp1);
                    level.SetCurveInView(DatumExtentType.ViewSpecific, view, newCurve);
                }
                catch (Exception ex)
                {
                    string issue = $"View '{view.Name}': failed to adjust level '{level.Name}' display: {ex.Message}";
                    log.AppendLine($"Warning: {issue}");
                    issues.Add(issue);
                }
            }
        }

        // Creates the next sheet in sequence by duplicating the reference sheet's title
        // block, incrementing the sheet number and name's final digit (per team
        // confirmation, a project won't need more than 9 elevation sheets, so a
        // single-digit increment is sufficient), and copying over the BA_SheetSeries
        // shared parameter and BA_SRT_Level 01 value.
        private static ViewSheet CreateNextSheet(Document doc, ViewSheet referenceSheet, StringBuilder log, List<string> issues)
        {
            string currentNumber = referenceSheet.get_Parameter(BuiltInParameter.SHEET_NUMBER).AsString();
            string currentName = referenceSheet.get_Parameter(BuiltInParameter.SHEET_NAME).AsString();

            if (!TryIncrementLastDigit(currentNumber, out string nextNumber))
            {
                string issue = $"Sheet '{currentNumber}': number doesn't end in a digit; can't auto-increment.";
                log.AppendLine($"Error: {issue}");
                issues.Add(issue);
                return null;
            }

            // This will now successfully increment "xxx 01" to "xxx 02", "xxx 03", etc.
            if (!TryIncrementLastDigit(currentName, out string nextName))
            {
                nextName = $"{currentName} 1";
            }

            Parameter sheetSeriesParam = referenceSheet.LookupParameter("BA_SheetSeries");
            string sheetSeriesValue = sheetSeriesParam?.AsString();

            FamilyInstance titleBlock = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .FirstOrDefault(tb => tb.get_Parameter(BuiltInParameter.SHEET_NUMBER)?.AsString() == currentNumber);

            if (titleBlock == null)
            {
                string issue = $"Sheet '{currentNumber}': couldn't find its title block instance.";
                log.AppendLine($"Error: {issue}");
                issues.Add(issue);
                return null;
            }

            ViewSheet newSheet = ViewSheet.Create(doc, titleBlock.GetTypeId());
            newSheet.get_Parameter(BuiltInParameter.SHEET_NUMBER).Set(nextNumber);
            newSheet.get_Parameter(BuiltInParameter.SHEET_NAME).Set(nextName);

            if (sheetSeriesValue != null)
            {
                newSheet.LookupParameter("BA_SheetSeries")?.Set(sheetSeriesValue);
            }
            newSheet.LookupParameter("BA_SRT_Level 01")?.Set("100 Building");

            log.AppendLine($"Created additional sheet: {nextNumber} - {nextName}");
            return newSheet;
        }

        private static bool TryIncrementLastDigit(string value, out string result)
        {
            result = value;
            if (string.IsNullOrEmpty(value) || !char.IsDigit(value[value.Length - 1]))
            {
                return false;
            }

            int lastDigit = value[value.Length - 1] - '0';
            result = value.Substring(0, value.Length - 1) + (lastDigit + 1);
            return true;
        }
    }
}
