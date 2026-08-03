using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Brand_25
{
    // Wraps a line-style subcategory (a Category under OST_Lines) for display in the
    // duplicate-line-style consolidation dialog. Mirrors VM_TextNoteType's shape
    // (GroupShade / DuplicateGroupKey / IsSurvivor / InstanceCount) so
    // Consolidate_LineStyle can reuse the exact same DataGrid + survivor-radio
    // pattern as Consolidate_TextNoteType.xaml.cs.
    //
    // NOTE: Category itself is not what gets assigned to a CurveElement — that's
    // done via the Category's own GraphicsStyle (Projection). GraphicsStyle is
    // captured here once so callers never have to re-resolve it.
    public class VM_LineStyle : INotifyPropertyChanged
    {
        public Category Category { get; }
        public GraphicsStyle GraphicsStyle { get; }

        public string Name { get; }
        public string LineWeight { get; }
        public string Colour { get; }
        public string LinePattern { get; }

        // Negative ElementId values are Revit's convention for built-in/reserved
        // elements — these subcategories (e.g. <Wide Lines>, <Overhead>, <Beyond>,
        // <Centerline>, etc.) can never be removed via doc.Delete(); attempting it
        // throws "Element id not found in this Document". IsBuiltIn is now
        // resolved from the officially-supported Category.BuiltInCategory
        // property (BuiltInCategory != BuiltInCategory.INVALID means built-in),
        // falling back to the negative-Id heuristic only if that property itself
        // throws for some reason.
        public bool IsBuiltIn { get; }
        public BuiltInCategory ResolvedBuiltInCategory { get; } = BuiltInCategory.INVALID;

        // For duplicate-group row shading, same convention as VM_TextNoteType.
        public bool GroupShade { get; set; }

        // Scopes the Survivor radio button's mutual exclusivity to just this row's
        // duplicate group (multiple groups can be shown in one grid at once).
        public string DuplicateGroupKey { get; set; }

        // Grouping key built from LineWeight + Colour + LinePattern — identical
        // values here is what defines "duplicate" for this tool.
        public string ComparisonKey { get; set; }

        // Set when the user picks this style as the survivor for its group.
        // Backed by a field (not auto-property) and raises PropertyChanged so
        // programmatically un-checking the rest of a group (when the user picks a
        // different survivor) reliably updates the bound RadioButton even if its
        // row container has been recycled by DataGrid virtualization.
        private bool _isSurvivor;
        public bool IsSurvivor
        {
            get => _isSurvivor;
            set
            {
                if (_isSurvivor == value) return;
                _isSurvivor = value;
                OnPropertyChanged(nameof(IsSurvivor));
                // IsRetainInstances' displayed value is derived from IsSurvivor
                // too (see below) — notify so the bound CheckBox updates even
                // though the user's own underlying choice didn't change.
                OnPropertyChanged(nameof(IsRetainInstances));
            }
        }

        // Checked by the user (per style, independent of the Survivor radio) to
        // mean "leave this style's instances exactly as they are — don't reassign
        // anything, even the editable ones." While this row IS the survivor, the
        // checkbox is disabled (nothing merges into itself) but still DISPLAYS as
        // checked, since retaining is trivially true for the row everything else
        // merges into. The user's actual underlying choice (_isRetainInstances) is
        // preserved untouched underneath so it's restored correctly if a different
        // row becomes survivor instead.
        private bool _isRetainInstances;
        public bool IsRetainInstances
        {
            get => IsSurvivor || _isRetainInstances;
            set
            {
                if (_isRetainInstances == value) return;
                _isRetainInstances = value;
                OnPropertyChanged(nameof(IsRetainInstances));
            }
        }

        // Populated by the command before showing the dialog. These are purely
        // informational tags now — the Survivor radio is never disabled, so ANY
        // style (including a built-in one) can be picked as survivor or left as a
        // redundant candidate; the user's own Retain Instances checkbox is what
        // actually decides whether a given style's editable instances get
        // reassigned. HeldBackReasons/StatusText exist so the dialog and log can
        // show WHY a style may not be fully consolidatable (e.g. it can never be
        // deleted, or some of its instances can never be touched at all).
        public List<string> HeldBackReasons { get; } = new List<string>();
        public string StatusText => HeldBackReasons.Count == 0 ? "Standard" : string.Join(", ", HeldBackReasons);

        // True if this style has at least one instance the API simply cannot
        // modify at all (inside a model/detail group, or a filled region's own
        // boundary line) — these instances are always left untouched regardless of
        // the Retain Instances checkbox, since there's no choice involved.
        public bool HasBlockingInstances => HeldBackReasons.Contains("In Group") || HeldBackReasons.Contains("In Filled Region");

        // Populated by the command before showing the dialog — how many
        // CurveElements (project-wide) currently use this line style, blocking or
        // not.
        public int InstanceCount { get; set; }

        // Set if this category could not provide a Projection graphics style (or
        // its associated weight/pattern) at all — some subcategories under
        // OST_Lines apparently don't expose one the normal way (observed as
        // "Cannot access the projection graphics style of this category" for a
        // subset of styles after a delete-unused pass; root cause not yet fully
        // pinned down). Callers should exclude any VM with this set from
        // comparison/consolidation and just report it instead of touching it.
        public string ConstructionError { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public VM_LineStyle(Category category, Document doc)
        {
            Category = category;

            // Name and Id access have NOT been ruled out as throw-capable — every
            // other Category property read in this class has already been caught
            // doing so for some subcategory or other, so these get the same
            // treatment rather than being assumed safe. This is read constantly
            // throughout the command (including in Phase 1, well before any
            // GraphicsStyle-related code runs), so a bad Name/Id here previously
            // meant an unguarded crash with no chance to log anything at all.
            string name;
            try
            {
                name = category.Name;
            }
            catch (Exception ex)
            {
                name = $"(name unavailable, Id {SafeIdString(category)})";
                ConstructionError = $"Could not read category name: {ex.Message}";
            }
            Name = name;

            bool isBuiltIn;
            BuiltInCategory builtInCategory = BuiltInCategory.INVALID;
            try
            {
                builtInCategory = category.BuiltInCategory;
                isBuiltIn = builtInCategory != BuiltInCategory.INVALID;
            }
            catch (Exception ex)
            {
                // Fall back to the negative-Id convention if BuiltInCategory
                // itself throws — this project has seen several Category
                // properties behave unexpectedly for particular subcategories.
                try { isBuiltIn = category.Id.Value < 0; }
                catch { isBuiltIn = false; }
                ConstructionError = (ConstructionError == null ? "" : ConstructionError + " | ") + $"Could not read BuiltInCategory: {ex.Message}";
            }
            IsBuiltIn = isBuiltIn;
            ResolvedBuiltInCategory = builtInCategory;

            try
            {
                GraphicsStyle = category.GetGraphicsStyle(GraphicsStyleType.Projection);
            }
            catch (Exception ex)
            {
                ConstructionError = (ConstructionError == null ? "" : ConstructionError + " | ") + $"Could not read Projection graphics style: {ex.Message}";
                LineWeight = "—";
                Colour = "—";
                LinePattern = "—";
                return; // nothing else below is safe to attempt without GraphicsStyle
            }

            try
            {
                int? weight = category.GetLineWeight(GraphicsStyleType.Projection);
                LineWeight = weight?.ToString() ?? "—";
            }
            catch (Exception ex)
            {
                LineWeight = "—";
                ConstructionError = (ConstructionError == null ? "" : ConstructionError + " | ") + $"Could not read line weight: {ex.Message}";
            }

            try
            {
                Color colour = category.LineColor;
                Colour = (colour != null && colour.IsValid)
                    ? $"R:{colour.Red} G:{colour.Green} B:{colour.Blue}"
                    : "—";
            }
            catch (Exception ex)
            {
                Colour = "—";
                ConstructionError = (ConstructionError == null ? "" : ConstructionError + " | ") + $"Could not read line colour: {ex.Message}";
            }

            try
            {
                ElementId patternId = category.GetLinePatternId(GraphicsStyleType.Projection);
                if (patternId == null || patternId == ElementId.InvalidElementId)
                {
                    LinePattern = "Solid";
                }
                else
                {
                    Element patternElemGeneric = doc.GetElement(patternId);
                    if (patternElemGeneric is LinePatternElement lpe)
                    {
                        // A real LinePatternElement exists — use its actual name.
                        LinePattern = lpe.Name;
                    }
                    else if (patternElemGeneric == null)
                    {
                        // No element resolves at all (regardless of whether the id
                        // is positive or negative) — this is exactly what a Solid
                        // pattern looks like: there's no LinePatternElement to find,
                        // since Solid isn't stored as one.
                        LinePattern = "Solid";
                    }
                    else
                    {
                        // Resolved to *something*, just not a LinePatternElement as
                        // expected — genuinely unusual, show what it actually is
                        // rather than silently collapsing to "—".
                        LinePattern = $"{patternElemGeneric.Name} (unexpected type: {patternElemGeneric.GetType().Name})";
                    }
                }
            }
            catch (Exception ex)
            {
                LinePattern = "—";
                ConstructionError = (ConstructionError == null ? "" : ConstructionError + " | ") + $"Could not read line pattern: {ex.Message}";
            }
        }

        private static string SafeIdString(Category category)
        {
            try { return category.Id.Value.ToString(); }
            catch { return "unknown"; }
        }
    }
}
