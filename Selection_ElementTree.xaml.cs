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

                    ElementMulticategoryFilter viewFilter = GetModelCategoryFilter(_doc);
                    if (viewFilter == null) return new List<Element>();

                    return new FilteredElementCollector(_doc, view.Id)
                        .WherePasses(viewFilter)
                        .WhereElementIsNotElementType()
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
        }

        // Root ("Categories") -> Category -> Family -> Type -> Instance.
        private VM_ElementTreeNode BuildTree(List<Element> elements)
        {
            const string rootPath = "Categories";
            VM_ElementTreeNode rootNode = CreateNode("Categories", ElementTreeLevel.Root, null, rootPath);

            var byCategory = elements
                .GroupBy(SafeCategoryName)
                .OrderBy(g => g.Key);

            foreach (var categoryGroup in byCategory)
            {
                string categoryPath = $"{rootPath}/{categoryGroup.Key}";
                VM_ElementTreeNode categoryNode = CreateNode(categoryGroup.Key, ElementTreeLevel.Category, rootNode, categoryPath);
                rootNode.Children.Add(categoryNode);

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

                        foreach (Element elem in typeGroup.OrderBy(GetInstanceLabel))
                        {
                            string instancePath = $"{typePath}/id:{elem.Id.Value}";
                            VM_ElementTreeNode instanceNode = CreateNode(
                                GetInstanceLabel(elem), ElementTreeLevel.Instance, typeNode, instancePath, elem);

                            // Seed from the persistent checked-id set, quietly (no
                            // cascade/notify — nothing is subscribed to this node yet).
                            instanceNode.SetInitialCheckedQuiet(_checkedIds.Contains(elem.Id));

                            // From here on, any check/uncheck of THIS node (directly,
                            // or via a Family/Type/Category ancestor cascading down
                            // to it) updates _checkedIds and pushes the new
                            // selection to Revit.
                            instanceNode.PropertyChanged += InstanceNode_PropertyChanged;

                            typeNode.Children.Add(instanceNode);
                        }
                    }
                }
            }

            // Bottom-up pass computes InstanceCount and each non-leaf node's
            // tri-state icon in one recursive sweep, without relying on cascaded
            // notifications that only fire in response to a live user click.
            rootNode.RefreshAggregateRecursive();
            return rootNode;
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

        // Every category where CategoryType == Model — physical/spatial elements
        // (Walls, Doors, Furniture, Rooms, ...) as opposed to annotations, tags,
        // view-specific graphics, or internal/settings categories.
        private static ElementMulticategoryFilter GetModelCategoryFilter(Document doc)
        {
            List<BuiltInCategory> modelCategories = new List<BuiltInCategory>();
            foreach (Category cat in doc.Settings.Categories)
            {
                try
                {
                    if (cat.CategoryType == CategoryType.Model && cat.BuiltInCategory != BuiltInCategory.INVALID)
                        modelCategories.Add(cat.BuiltInCategory);
                }
                catch
                {
                    // Some categories can throw when queried this way (seen before
                    // with certain line-style subcategories) — skip rather than
                    // abort the whole scan over one bad category.
                }
            }

            return modelCategories.Count > 0 ? new ElementMulticategoryFilter(modelCategories) : null;
        }

        private static List<Element> CollectAllModelElements(Document doc)
        {
            ElementMulticategoryFilter filter = GetModelCategoryFilter(doc);
            if (filter == null) return new List<Element>();

            return new FilteredElementCollector(doc)
                .WherePasses(filter)
                .WhereElementIsNotElementType()
                .ToList();
        }

        private static string SafeCategoryName(Element elem)
        {
            try { return elem.Category?.Name ?? "(no category)"; }
            catch { return "(no category)"; }
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
