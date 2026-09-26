using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Brand_25
{
    public enum ElementTreeScope
    {
        EntireProject,
        ActiveView,
        CurrentSelection
    }

    // Modeless — shown via Show(), not ShowDialog(). Originally built for Doors
    // only as a trial; generalized to EVERY category where Category.CategoryType
    // == Model (Walls, Furniture, Doors, Windows, Structural Framing, ...) — see
    // GetModelCategoryFilter/CollectAllModelElements. Stays open alongside Revit,
    // stays pinned above Revit's own window specifically (see the
    // WindowInteropHelper.Owner assignment in the constructor), and stays in sync
    // with the model in both directions:
    //   - Checking a box updates a persistent checked-id set and pushes ALL of it
    //     to Revit's selection via an ExternalEvent — including elements checked
    //     earlier under a different scope/view that aren't currently displayed in
    //     the tree at all. Checking does NOT by itself move the view.
    //   - Highlighting (selecting) a single Instance row — independent of its
    //     checkbox — zooms the active view to that element, via a SEPARATE
    //     ExternalEvent/handler (ElementTreeZoomHandler). Highlighting a Family/
    //     Type/Category/Root row does nothing, since there's no single element to
    //     zoom to.
    //   - Deleting/retyping an element, or switching the active view (while scope
    //     is Active View), triggers a live rebuild via Application.DocumentChanged
    //     / UIApplication.ViewActivated respectively.
    //   - The "Show:" dropdown rescans under a new scope; see SetScope for how
    //     that differs from the two live-rebuild paths above.
    //
    // Grouping: Root ("Categories") -> Category -> Family -> Type -> Instance.
    // Family/Type come from ElementType.FamilyName / ElementType.Name rather than
    // FamilyInstance.Symbol.Family / .Symbol — the FamilyInstance-specific path
    // only exists for loadable-family categories (Doors, Furniture, ...); system
    // families (Walls, Floors, Roofs, ...) have no Family object at all, but
    // EVERY ElementType exposes FamilyName/Name uniformly across both, which is
    // what makes one grouping scheme work for the whole project.
    //
    // NOTE: this deliberately departs from the rest of Brand_25's Selection_*
    // dialogs, which are all modal ShowDialog() pickers with an OK/Cancel commit
    // step. There's no "commit" here — every checkbox change is already live.
    public partial class Selection_ElementTree : Window
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly ElementTreeSelectionHandler _selectionHandler;
        private readonly ExternalEvent _selectionEvent;
        private readonly ElementTreeZoomHandler _zoomHandler;
        private readonly ExternalEvent _zoomEvent;

        // THE authoritative record of "what's checked" — spans every scope/view
        // the user has visited in this session, not just whatever's currently
        // displayed. A tree rebuild only ever REMOVES an id from here when the
        // underlying element has genuinely been deleted from the project; merely
        // falling outside the current scope leaves it untouched. This is what
        // actually gets pushed to Revit's selection (see PushSelectionUpdate) —
        // never anything derived by walking the visible tree, since the visible
        // tree may not even contain every checked element.
        private readonly HashSet<ElementId> _checkedIds = new HashSet<ElementId>();

        // Source of truth for "what's expanded", keyed by a stable path string
        // (e.g. "Categories/Doors/ACME Door/36\"x84\"") rather than by node
        // reference, for the same reason — the whole tree is rebuilt from scratch
        // on every rescan, discarding the old VM_ElementTreeNode objects entirely.
        private readonly HashSet<string> _expandedPaths = new HashSet<string>();

        // Current scope + its snapshot (only one of the two snapshot fields is
        // meaningful at a time, depending on _currentScope).
        private ElementTreeScope _currentScope = ElementTreeScope.EntireProject;
        private ElementId _scopeViewId = ElementId.InvalidElementId;
        private HashSet<ElementId> _scopeSelectionIds = new HashSet<ElementId>();

        // Guards ScopeCombo_SelectionChanged from firing its own rescan while the
        // constructor is setting ScopeCombo.SelectedItem to reflect the initial scope.
        private bool _isInitializing;

        private VM_ElementTreeNode _root;

        // Debounces the search box: filtering re-walks the whole tree, which for
        // a large "Entire Project" scan could feel laggy if re-run on every single
        // keystroke — this waits for a short pause in typing before actually
        // applying the filter.
        private readonly DispatcherTimer _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };

        public Selection_ElementTree(Document doc, UIDocument uidoc,
            ElementTreeSelectionHandler selectionHandler, ExternalEvent selectionEvent,
            ElementTreeZoomHandler zoomHandler, ExternalEvent zoomEvent,
            ElementTreeScope initialScope, string credit = "Selection_ElementTree Default")
        {
            InitializeComponent();

            _doc = doc;
            _uidoc = uidoc;
            _selectionHandler = selectionHandler;
            _selectionEvent = selectionEvent;
            _zoomHandler = zoomHandler;
            _zoomEvent = zoomEvent;

            FooterText.Text = credit;

            minimizeImage.Source = LoadEmbeddedImage("minimize_32.png");
            maximizeImage.Source = LoadEmbeddedImage("maximize_32.png");
            closeImage.Source = LoadEmbeddedImage("close_32.png");
            brandLogo.Source = LoadEmbeddedImage("Brand_logo.png");
            icon.Source = LoadEmbeddedImage("B_icon_32.png");

            // Stay above Revit's own main window specifically (a Win32 "owned"
            // window) rather than a blanket Topmost=true, which would also float
            // above unrelated applications — this is scoped to "on top of Revit".
            try
            {
                IntPtr revitHandle = Process.GetCurrentProcess().MainWindowHandle;
                if (revitHandle != IntPtr.Zero)
                    new WindowInteropHelper(this).Owner = revitHandle;
            }
            catch
            {
                // Non-essential — worst case the window behaves like an ordinary
                // top-level window instead of staying pinned above Revit.
            }

            _expandedPaths.Add("Categories"); // default: only the root starts expanded

            _searchDebounceTimer.Tick += (s, e) =>
            {
                _searchDebounceTimer.Stop();
                ApplySearch(SearchBox.Text);
            };

            _isInitializing = true;
            SelectComboItemForScope(initialScope);
            _isInitializing = false;

            SetScope(initialScope); // establishes the snapshot (if any) and builds the first tree
        }

        // ── Scope handling ───────────────────────────────────────────────────

        private void ScopeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (!(ScopeCombo.SelectedItem is ComboBoxItem item) || !(item.Tag is ElementTreeScope scope)) return;

            SetScope(scope);
        }

        private void SelectComboItemForScope(ElementTreeScope scope)
        {
            foreach (ComboBoxItem item in ScopeCombo.Items)
            {
                if (item.Tag is ElementTreeScope tagScope && tagScope == scope)
                {
                    ScopeCombo.SelectedItem = item;
                    return;
                }
            }
        }

        // Establishes a NEW scope (manual dropdown change, or initial construction):
        // captures whatever snapshot that scope needs, then rescans and rebuilds.
        // Checked state is re-seeded from Revit's ACTUAL current selection at this
        // exact moment (intersected with the new scope's elements) — a deliberate
        // "fresh start" action, unlike the two live-rebuild paths (RebuildTree)
        // which preserve _checkedIds across a scope/view change instead.
        private void SetScope(ElementTreeScope scope)
        {
            _currentScope = scope;

            switch (scope)
            {
                case ElementTreeScope.ActiveView:
                    _scopeViewId = _uidoc.ActiveView?.Id ?? ElementId.InvalidElementId;
                    break;
                case ElementTreeScope.CurrentSelection:
                    _scopeSelectionIds = new HashSet<ElementId>(_uidoc.Selection.GetElementIds());
                    break;
                case ElementTreeScope.EntireProject:
                default:
                    break; // no snapshot needed — always re-queries the whole project
            }

            List<Element> scopedElements = GetScopedElements();

            ICollection<ElementId> liveSelection = _uidoc.Selection.GetElementIds();
            _checkedIds.Clear();
            foreach (Element elem in scopedElements)
                if (liveSelection.Contains(elem.Id)) _checkedIds.Add(elem.Id);

            LoadTree(scopedElements);
        }

        // Re-derives the current scope's element list from live document data.
        // For ActiveView/CurrentSelection this is filtered against the snapshot
        // taken in SetScope (or, for ActiveView, refreshed live by
        // OnViewActivated); for EntireProject there's nothing to filter against.
        private List<Element> GetScopedElements()
        {
            switch (_currentScope)
            {
                case ElementTreeScope.ActiveView:
                    if (_scopeViewId == ElementId.InvalidElementId) return new List<Element>();
                    if (!(_doc.GetElement(_scopeViewId) is View view) || !view.IsValidObject)
                        return new List<Element>(); // the tracked view was itself deleted

                    return new FilteredElementCollector(_doc, view.Id)
                        .WhereElementIsNotElementType()
                        .Where(IsIncludedModelElement)
                        .ToList();

                case ElementTreeScope.CurrentSelection:
                    return CollectAllModelElements(_doc).Where(e => _scopeSelectionIds.Contains(e.Id)).ToList();

                case ElementTreeScope.EntireProject:
                default:
                    return CollectAllModelElements(_doc);
            }
        }

        private static string ScopeDisplayName(ElementTreeScope scope)
        {
            switch (scope)
            {
                case ElementTreeScope.ActiveView: return "Active View";
                case ElementTreeScope.CurrentSelection: return "Current Selection";
                case ElementTreeScope.EntireProject:
                default: return "Entire Project";
            }
        }

        // ── Tree construction ────────────────────────────────────────────────

        private void LoadTree(List<Element> elements)
        {
            _root = BuildTree(elements);
            ElementTreeView.ItemsSource = new List<VM_ElementTreeNode> { _root };
            TitleText.Text = $"Select Elements — {elements.Count} instance(s) in {ScopeDisplayName(_currentScope)}, live";

            // A tree rebuild discards every old node, which would otherwise
            // silently drop whatever search filter was active — reapply it
            // immediately against the fresh tree.
            ApplySearch(SearchBox.Text);
        }

        // Root ("Categories") -> Category -> Family -> Type -> Instance — EXCEPT
        // for CAD imports/links (ImportInstance) and linked Revit models
        // (RevitLinkInstance), where the Family level is skipped entirely:
        // Category -> (one node per file) -> Instance. Both element kinds report
        // a FamilyName that's generically identical across every file (e.g. every
        // CAD import might report something like "Import Symbol" regardless of
        // which DWG it is) — the distinction that actually matters (which file)
        // already lives at the Type level (an ImportInstance's type is the
        // CADLinkType, and a RevitLinkInstance's type is the RevitLinkType, both
        // of whose Name is the imported/linked file's own name). Carrying the
        // uninformative Family level for these would just be one more click to
        // get through for no benefit.
        private VM_ElementTreeNode BuildTree(List<Element> elements)
        {
            const string rootPath = "Categories";
            VM_ElementTreeNode rootNode = CreateNode("Categories", ElementTreeLevel.Root, null, rootPath);

            var byCategory = elements
                .GroupBy(GetCategoryGroupName)
                .OrderBy(g => g.Key);

            foreach (var categoryGroup in byCategory)
            {
                string categoryPath = $"{rootPath}/{categoryGroup.Key}";
                VM_ElementTreeNode categoryNode = CreateNode(categoryGroup.Key, ElementTreeLevel.Category, rootNode, categoryPath);
                rootNode.Children.Add(categoryNode);

                bool skipFamilyLevel = categoryGroup.All(e => e is ImportInstance || e is RevitLinkInstance);

                if (skipFamilyLevel)
                {
                    var byFile = categoryGroup
                        .GroupBy(GetTypeName)
                        .OrderBy(g => g.Key);

                    foreach (var fileGroup in byFile)
                    {
                        string filePath = $"{categoryPath}/{fileGroup.Key}";
                        VM_ElementTreeNode fileNode = CreateNode(fileGroup.Key, ElementTreeLevel.Type, categoryNode, filePath);
                        categoryNode.Children.Add(fileNode);

                        AddInstanceNodes(fileGroup, fileNode, filePath);
                    }

                    continue;
                }

                var byFamily = categoryGroup
                    .GroupBy(GetFamilyName)
                    .OrderBy(g => g.Key);

                foreach (var familyGroup in byFamily)
                {
                    string familyPath = $"{categoryPath}/{familyGroup.Key}";
                    VM_ElementTreeNode familyNode = CreateNode(familyGroup.Key, ElementTreeLevel.Family, categoryNode, familyPath);
                    categoryNode.Children.Add(familyNode);

                    var byType = familyGroup
                        .GroupBy(GetTypeName)
                        .OrderBy(g => g.Key);

                    foreach (var typeGroup in byType)
                    {
                        string typePath = $"{familyPath}/{typeGroup.Key}";
                        VM_ElementTreeNode typeNode = CreateNode(typeGroup.Key, ElementTreeLevel.Type, familyNode, typePath);
                        familyNode.Children.Add(typeNode);

                        AddInstanceNodes(typeGroup, typeNode, typePath);
                    }
                }
            }

            // Bottom-up pass computes InstanceCount and each non-leaf node's
            // tri-state icon in one recursive sweep, without relying on cascaded
            // notifications that only fire in response to a live user click.
            rootNode.RefreshAggregateRecursive();
            return rootNode;
        }

        // Shared leaf-building step for both branches above — one Instance node
        // per element, seeded from the persistent checked-id set, wired to push
        // live selection updates, and tagged with the fields the search box
        // matches against.
        private void AddInstanceNodes(IEnumerable<Element> elems, VM_ElementTreeNode parentNode, string parentPath)
        {
            foreach (Element elem in elems.OrderBy(GetInstanceLabel))
            {
                string instancePath = $"{parentPath}/id:{elem.Id.Value}";
                VM_ElementTreeNode instanceNode = CreateNode(
                    GetInstanceLabel(elem), ElementTreeLevel.Instance, parentNode, instancePath, elem);

                // Seed from the persistent checked-id set, quietly (no
                // cascade/notify — nothing is subscribed to this node yet).
                instanceNode.SetInitialCheckedQuiet(_checkedIds.Contains(elem.Id));

                // From here on, any check/uncheck of THIS node (directly, or via
                // an ancestor cascading down to it) updates _checkedIds and
                // pushes the new selection to Revit.
                instanceNode.PropertyChanged += InstanceNode_PropertyChanged;

                instanceNode.SearchFields = BuildSearchFields(elem);

                parentNode.Children.Add(instanceNode);
            }
        }

        // Constructs a node, seeds its IsExpanded from the persisted path->expanded
        // set, and wires a subscription that keeps that set updated whenever the
        // user expands/collapses it — this is what makes expand state survive a
        // full tree rebuild despite every VM_ElementTreeNode being discarded and
        // recreated each time.
        private VM_ElementTreeNode CreateNode(string name, ElementTreeLevel level, VM_ElementTreeNode parent, string path, Element element = null)
        {
            VM_ElementTreeNode node = new VM_ElementTreeNode(name, level, parent, element);
            node.IsExpanded = _expandedPaths.Contains(path);

            node.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(VM_ElementTreeNode.IsExpanded)) return;
                if (node.IsExpanded) _expandedPaths.Add(path);
                else _expandedPaths.Remove(path);
            };

            return node;
        }

        // ── Element collection / grouping helpers ───────────────────────────

        // Filters by the element's ACTUAL Category.CategoryType rather than
        // building a closed BuiltInCategory allow-list (the previous approach,
        // via ElementMulticategoryFilter) — that approach silently excluded
        // anything whose Category reports BuiltInCategory.INVALID, which includes
        // CAD import layers and other dynamically-created categories. Checking
        // CategoryType directly per element catches those too, at the cost of a
        // heavier upfront query on very large projects (this now has to look at
        // every element in the relevant scope, not just a pre-filtered category
        // set) — an accepted trade-off, and also what makes it straightforward to
        // widen this later (e.g. to include Levels/Grids/Tags) by adjusting a
        // single condition rather than restructuring how elements are collected.
        // Categories excluded outright regardless of CategoryType — a mix of
        // Settings/annotation-adjacent categories that still surfaced through the
        // broad CategoryType.Model scan (Sheets, Project Information), Model
        // categories no one would ever want to select/tag as a real element
        // (Materials, Material Assets, Sun Path, Areas), and internal
        // sketch/geometry placeholders (<Sketch>, <Stair/Ramp Sketch>, Railing
        // Rail Path Extension Lines). Matched by display name (case-insensitive)
        // rather than BuiltInCategory, mirroring Line_ConsolidateStyles.cs's own
        // excluded-names list — the exact BuiltInCategory each of these resolves
        // to isn't confirmed against the live API, whereas the displayed names
        // are exactly what was specified.
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

        // Prefix-matched (case-insensitive) rather than exact — "<Stair/Ramp
        // Sketch>" turned out not to be a single category at all, but four
        // distinct sub-category names sharing one family: "<Stair/Ramp Sketch:
        // Boundary>", ": Riser>", ": Run>", ": Stair Path>". An exact-match entry
        // for the bare "<Stair/Ramp Sketch>" string never matched any of them.
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

        private static List<Element> CollectAllModelElements(Document doc)
        {
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(IsIncludedModelElement)
                .ToList();
        }

        private static string SafeCategoryName(Element elem)
        {
            try { return elem.Category?.Name ?? "(no category)"; }
            catch { return "(no category)"; }
        }

        // Top-level grouping key. For everything except CAD imports/links this is
        // just the element's real Revit Category (e.g. "Doors", "Floors"). CAD
        // imports are the deliberate exception: Revit gives each imported file its
        // OWN pseudo-category (literally named after the file), so grouping by
        // real Category would put every different DWG at the same top level as
        // Doors/Floors/etc. — exactly the clutter this collapses. Instead, EVERY
        // ImportInstance is routed into one of two synthetic buckets based on
        // IsLinked, regardless of which file or real category it actually has:
        // "Linked CAD" for CAD links, "Imported CAD" for embedded CAD imports.
        // The per-file distinction isn't lost — it still shows up one level down,
        // at the Type level (see BuildTree's skipFamilyLevel branch).
        private static string GetCategoryGroupName(Element elem)
        {
            if (elem is ImportInstance importInstance)
            {
                try
                {
                    return importInstance.IsLinked ? "Linked CAD" : "Imported CAD";
                }
                catch
                {
                    return "Imported CAD"; // conservative fallback if IsLinked itself throws
                }
            }

            return SafeCategoryName(elem);
        }

        // Works uniformly across loadable families (Doors, Furniture, ...) AND
        // system families (Walls, Floors, Roofs, ...) — ElementType.FamilyName
        // is defined generically on the base ElementType class for exactly this
        // purpose, unlike FamilyInstance.Symbol.Family, which only exists for
        // loadable-family-based elements.
        private string GetFamilyName(Element elem)
        {
            try
            {
                ElementId typeId = elem.GetTypeId();
                if (typeId == ElementId.InvalidElementId) return "(no family)";
                ElementType type = _doc.GetElement(typeId) as ElementType;
                return string.IsNullOrWhiteSpace(type?.FamilyName) ? "(no family)" : type.FamilyName;
            }
            catch
            {
                return "(no family)";
            }
        }

        private string GetTypeName(Element elem)
        {
            try
            {
                ElementId typeId = elem.GetTypeId();
                if (typeId == ElementId.InvalidElementId) return "(no type)";
                Element type = _doc.GetElement(typeId);
                return string.IsNullOrWhiteSpace(type?.Name) ? "(no type)" : type.Name;
            }
            catch
            {
                return "(no type)";
            }
        }

        // "Mark (Id 123456)" when the element has a Mark set, otherwise just
        // "Id 123456" — Mark isn't universal across every category, hence the
        // Id-only fallback.
        private static string GetInstanceLabel(Element elem)
        {
            Parameter markParam = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            string mark = markParam != null && markParam.HasValue ? markParam.AsString() : null;

            return string.IsNullOrWhiteSpace(mark)
                ? $"Id {elem.Id.Value}"
                : $"{mark} (Id {elem.Id.Value})";
        }

        private static string GetMarkValue(Element elem)
        {
            try
            {
                Parameter p = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                return p != null && p.HasValue ? p.AsString() ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        // Tries the instance's own Keynote first, then falls back to the
        // element's TYPE — for a lot of categories, Keynote is actually a
        // type-level parameter (set once on the Type, shared by every instance
        // of it) rather than an instance-level one, so checking only the
        // instance was silently missing every match that came from a type's
        // keynote rather than an instance override.
        private string GetKeynoteValue(Element elem)
        {
            string instanceKeynote = TryGetKeynoteFromElement(elem);
            if (!string.IsNullOrWhiteSpace(instanceKeynote)) return instanceKeynote;

            try
            {
                ElementId typeId = elem.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    Element type = _doc.GetElement(typeId);
                    if (type != null)
                    {
                        string typeKeynote = TryGetKeynoteFromElement(type);
                        if (!string.IsNullOrWhiteSpace(typeKeynote)) return typeKeynote;
                    }
                }
            }
            catch
            {
                // Fall through — no keynote found on the type either.
            }

            return "";
        }

        // Tries the standard Keynote built-in parameter first; falls back to a
        // name lookup since not every category necessarily exposes it the same
        // way (mirrors the defensive get_Parameter-then-LookupParameter pattern
        // Mat_FindDupKeynote.cs already uses for material keynotes). Shared by
        // GetKeynoteValue above for both the instance and (if needed) its type.
        private static string TryGetKeynoteFromElement(Element e)
        {
            try
            {
                Parameter p = e.get_Parameter(BuiltInParameter.KEYNOTE_PARAM);
                if (p != null && p.HasValue)
                {
                    string v = p.AsValueString() ?? p.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            catch
            {
                // BuiltInParameter.KEYNOTE_PARAM may not apply to this element —
                // fall through to the named lookup below.
            }

            try
            {
                Parameter p = e.LookupParameter("Keynote");
                if (p != null && p.HasValue)
                {
                    string v = p.AsValueString() ?? p.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            catch
            {
                // Give up quietly — no keynote here either, which is a valid outcome.
            }

            return "";
        }

        // The exact set of fields the search box matches against: Mark, Family
        // name, Type name, Id, Keynote — kept as separate strings (rather than one
        // concatenated blob) so a search can't spuriously match across the
        // boundary between two unrelated fields.
        private string[] BuildSearchFields(Element elem) => new[]
        {
            GetMarkValue(elem),
            GetFamilyName(elem),
            GetTypeName(elem),
            elem.Id.Value.ToString(),
            GetKeynoteValue(elem)
        };

        // ── Search (narrows the tree; does not re-query Revit) ──────────────

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        // Clearing search is a deliberate, final action (unlike a mid-typing
        // keystroke), so apply it immediately rather than waiting out the
        // debounce delay. Setting SearchBox.Text would also fire
        // SearchBox_TextChanged on its own, but stopping the timer and calling
        // ApplySearch directly here avoids a brief, pointless delay before the
        // tree actually reverts to fully visible.
        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            _searchDebounceTimer.Stop();
            ApplySearch("");
        }

        private void ApplySearch(string query)
        {
            if (_root == null) return;

            string trimmed = query?.Trim() ?? "";

            // A 1-character query matches almost anything in any real dataset.
            // "id" specifically turned out to coincidentally match a huge
            // fraction of elements too — it's a common substring in perfectly
            // ordinary family/type names ("Grid", "Slide", "Solid", "Wide", ...),
            // not anything to do with the literal word "Id". Both defeat the
            // purpose of narrowing the tree, so treat them the same as an empty
            // query: no filter applied at all, rather than "matches everything".
            if (trimmed.Length < 2 || trimmed.Equals("id", StringComparison.OrdinalIgnoreCase))
                trimmed = "";

            ApplySearchFilterRecursive(_root, trimmed);
        }

        // Returns true if this node (or, for a non-leaf, any descendant) matches
        // the query, and sets MatchesSearch on every node in the subtree
        // accordingly — that's what drives each row's Visibility in XAML
        // (VM_ElementTreeNode.RowVisibility). An empty query means "no filter":
        // everything becomes visible again.
        //
        // Non-leaf nodes whose subtree contains a match are also force-expanded
        // (IsExpanded = true), since otherwise a match buried under a Family/Type
        // that defaults to collapsed would be invisible despite "matching". This
        // does persist into _expandedPaths the same way a manual expand would
        // (see CreateNode), so a branch revealed by a search stays expanded even
        // after the search is cleared — a deliberate, simple trade-off rather
        // than tracking a second "temporarily forced open" state to unwind later.
        private static bool ApplySearchFilterRecursive(VM_ElementTreeNode node, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                node.MatchesSearch = true;
                foreach (VM_ElementTreeNode child in node.Children)
                    ApplySearchFilterRecursive(child, query);
                return true;
            }

            if (node.Level == ElementTreeLevel.Instance)
            {
                bool isMatch = NodeMatchesSearch(node, query);
                node.MatchesSearch = isMatch;
                return isMatch;
            }

            bool anyChildMatches = false;
            foreach (VM_ElementTreeNode child in node.Children)
            {
                if (ApplySearchFilterRecursive(child, query)) anyChildMatches = true;
            }

            node.MatchesSearch = anyChildMatches;
            if (anyChildMatches) node.IsExpanded = true;

            return anyChildMatches;
        }

        private static bool NodeMatchesSearch(VM_ElementTreeNode node, string query)
        {
            if (node.SearchFields == null) return false;
            foreach (string field in node.SearchFields)
            {
                if (!string.IsNullOrEmpty(field) && field.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // ── Live selection push (Dialog -> Revit) ───────────────────────────

        private void InstanceNode_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(VM_ElementTreeNode.IsChecked)) return;
            if (!(sender is VM_ElementTreeNode node) || node.Element == null) return;

            if (node.IsChecked == true) _checkedIds.Add(node.Element.Id);
            else _checkedIds.Remove(node.Element.Id);

            PushSelectionUpdate();
        }

        // Pushes the FULL persistent _checkedIds set — deliberately NOT derived by
        // walking _root, since the visible tree may not contain every checked
        // element (e.g. one checked under a different Active View). This is what
        // makes an element checked while looking at View A stay selected in Revit
        // after switching to View B, even though View B's tree can't show it.
        private void PushSelectionUpdate()
        {
            _selectionHandler.PendingIds = _checkedIds.ToList();
            _selectionEvent.Raise();
        }

        // ── Zoom (Tree row highlight -> Revit view) ─────────────────────────

        // Fires on a plain row click/keyboard-navigation highlight — the
        // TreeView's own single-selection concept, entirely independent of the
        // checkbox. Only zooms for a single Instance-level row; a Family/Type/
        // Category/Root row represents more than one (or zero) elements, so
        // there's no single extent to zoom to.
        private void ElementTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!(e.NewValue is VM_ElementTreeNode node) || node.Level != ElementTreeLevel.Instance || node.Element == null)
                return;

            _zoomHandler.PendingZoomId = node.Element.Id;
            _zoomEvent.Raise();
        }

        // ── Live rebuild (Revit -> Dialog) ──────────────────────────────────

        // Subscribed by Element_SelectByTree.cs against Application.DocumentChanged.
        public void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!ReferenceEquals(e.GetDocument(), _doc)) return; // a different open document changed, not this one
            RebuildTree();
        }

        // Subscribed by Element_SelectByTree.cs against UIApplication.ViewActivated
        // — only meaningful while scope is ActiveView; updates the tracked view and
        // rebuilds through RebuildTree (preserving _checkedIds across the switch,
        // just re-filtering what's DISPLAYED), rather than through SetScope, since
        // a plain view switch shouldn't reseed checked state the way an explicit
        // dropdown change does.
        public void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            if (_currentScope != ElementTreeScope.ActiveView) return;
            if (e.CurrentActiveView == null) return;

            // No Document-identity check here — this tool only ever tracks one
            // document anyway (see the class-level LIMITATION note in
            // Element_SelectByTree.cs); in the rare multi-document case where a
            // different document's view gets activated, GetScopedElements()
            // harmlessly finds no matching View in _doc for that view's Id and
            // just shows an empty tree until the user switches back.
            _scopeViewId = e.CurrentActiveView.Id;
            RebuildTree();
        }

        // Subscribed against Application.DocumentClosing — if the specific document
        // this window is tracking is closing, close the window with it rather than
        // leaving it dangling against an invalid Document reference.
        public void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            if (e.Document != null && !ReferenceEquals(e.Document, _doc)) return;
            Close();
        }

        // Rebuilds the DISPLAYED tree for the current scope, and prunes
        // _checkedIds ONLY for elements that no longer exist ANYWHERE in the
        // project — never for elements that simply fell outside the current
        // scope (a different view, or a narrower "Show:" filter). That
        // distinction is the whole point: Revit's selection should survive a
        // scope/view change even for items the tree itself can no longer display.
        private void RebuildTree()
        {
            if (_doc == null || !_doc.IsValidObject) return;

            List<Element> scopedElements;
            List<Element> allElements;
            try
            {
                allElements = CollectAllModelElements(_doc);
                scopedElements = GetScopedElements();
            }
            catch
            {
                return; // document in a transient state; the next change will retry
            }

            HashSet<ElementId> allExistingIds = new HashSet<ElementId>(allElements.Select(e => e.Id));
            _checkedIds.RemoveWhere(id => !allExistingIds.Contains(id));

            LoadTree(scopedElements);

            // A checked element may have just been deleted — push the corrected
            // (full, cross-scope) selection so Revit never shows a stale/
            // vaporized element as "selected".
            PushSelectionUpdate();
        }

        // ── Buttons ──────────────────────────────────────────────────────────

        // Only affects elements currently DISPLAYED (i.e. within the current
        // scope) — elements checked earlier under a different scope/view are
        // untouched, same principle as the live-rebuild paths above.
        private void SelectAll_Click(object sender, RoutedEventArgs e) => _root.SetAll(true);
        private void SelectNone_Click(object sender, RoutedEventArgs e) => _root.SetAll(false);

        // Collapses every node except the root, returning to the same default
        // landing view as a fresh tree build (Categories expanded, each Category
        // itself collapsed). Setting IsExpanded here goes through each node's own
        // property setter, which — via the subscription wired in CreateNode —
        // updates _expandedPaths automatically, so this also correctly "forgets"
        // whatever branches the user (or a search) had expanded.
        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            if (_root == null) return;
            CollapseAllRecursive(_root, isRoot: true);
        }

        private static void CollapseAllRecursive(VM_ElementTreeNode node, bool isRoot)
        {
            node.IsExpanded = isRoot;
            foreach (VM_ElementTreeNode child in node.Children)
                CollapseAllRecursive(child, isRoot: false);
        }

        // ── Boilerplate (window chrome, embedded images) ────────────────────

        private BitmapImage LoadEmbeddedImage(string imageName)
        {
            string resourcePath = $"Brand_25.Resources.Images.{imageName}";
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePath))
            {
                if (stream == null) throw new Exception($"Embedded resource not found: {resourcePath}");
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                return image;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
        private void buttonMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void buttonMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            maximizeImage.Source = LoadEmbeddedImage(
                WindowState == WindowState.Maximized ? "restore_32.png" : "maximize_32.png");
        }

        // No DialogResult here — this window is shown via Show(), not ShowDialog(),
        // and setting DialogResult on a non-modal window throws.
        private void buttonClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
