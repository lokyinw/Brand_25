using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Brand_25
{
    public enum ElementTreeLevel
    {
        Root,       // "Categories" — the single top-level wrapper
        Category,   // e.g. "Doors", "Walls", "Furniture" — any Category.CategoryType == Model
        Family,     // ElementType.FamilyName (works for both loadable AND system families)
        Type,       // ElementType.Name
        Instance
    }

    // A single row in the element-selection tree (Selection_ElementTree.xaml).
    // Originally built for Doors only as a trial; generalized to every
    // CategoryType.Model category in the project — see Selection_ElementTree's
    // GetFamilyName/GetTypeName for why ElementType.FamilyName/Name (rather than
    // FamilyInstance.Symbol.Family/.Symbol) is what makes a single Family/Type
    // grouping scheme work uniformly across BOTH loadable families (Doors,
    // Furniture, ...) and system families (Walls, Floors, Roofs, ...), which have
    // no Family object at all.
    //
    // Each node owns its own checked state, cascades a user's explicit
    // check/uncheck down to every descendant, and re-derives its own tri-state
    // (true / false / null = "partially checked") from its children whenever one
    // of them changes — the standard WPF hierarchical-checkbox pattern.
    //
    // Indeterminate is a purely DERIVED display state here — the CheckBox bound to
    // IsChecked in Selection_ElementTree.xaml is set IsThreeState="False" at every
    // level, which (per WPF's own ToggleButton.OnToggle logic) guarantees a click
    // always lands on a definite true/false, at any tier, even when the checkbox is
    // currently showing the indeterminate glyph — it just can't be produced or held
    // by a click.
    //
    // Only Instance-level nodes carry a real Element reference; Root/Category/
    // Family/Type nodes exist purely for grouping.
    //
    // NOTE: this class's own IsChecked bookkeeping only reflects what's checked
    // AMONG WHATEVER ELEMENTS ARE CURRENTLY IN THE TREE (i.e. within the current
    // scope). The window keeps a separate, persistent HashSet<ElementId> of every
    // checked element across scope/view changes (Selection_ElementTree._checkedIds)
    // — THAT set, not anything read back from this tree, is what actually gets
    // pushed to Revit's selection.
    public class VM_ElementTreeNode : INotifyPropertyChanged
    {
        public string Name { get; }
        public ElementTreeLevel Level { get; }
        public Element Element { get; }   // only set for Level == Instance
        public VM_ElementTreeNode Parent { get; }
        public ObservableCollection<VM_ElementTreeNode> Children { get; } = new ObservableCollection<VM_ElementTreeNode>();

        // Total number of Instance-level descendants under this node — 1 for a
        // leaf, the sum of children's counts otherwise. Computed once per tree
        // build (see RefreshAggregateRecursive), not recalculated on every
        // binding read, since the tree can now be large (every model category).
        public int InstanceCount { get; private set; }

        // Instance-level only: the fields the search box matches against (Mark,
        // Family name, Type name, Id, Keynote), computed once when the node is
        // built (Selection_ElementTree.BuildSearchFields) rather than re-derived
        // from Revit parameters on every keystroke.
        public string[] SearchFields { get; set; }

        // Whether this node currently passes the active search filter — true for
        // every node when there's no search text. Bound (via RowVisibility, not
        // directly) to each TreeViewItem's Visibility, so a non-matching row is
        // fully removed from layout rather than merely dimmed.
        private bool _matchesSearch = true;
        public bool MatchesSearch
        {
            get => _matchesSearch;
            set
            {
                if (_matchesSearch == value) return;
                _matchesSearch = value;
                OnPropertyChanged(nameof(MatchesSearch));
                OnPropertyChanged(nameof(RowVisibility));
            }
        }

        // Fully-qualified because Autodesk.Revit.DB (imported for Element/
        // ElementId elsewhere in this class) also declares its own Visibility
        // enum — the plain type name is ambiguous once both namespaces are in
        // scope.
        public System.Windows.Visibility RowVisibility =>
            MatchesSearch ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }

        private bool? _isChecked = false;
        public bool? IsChecked
        {
            get => _isChecked;
            set => SetIsChecked(value, true, true);
        }

        public VM_ElementTreeNode(string name, ElementTreeLevel level, VM_ElementTreeNode parent = null, Element element = null)
        {
            Name = name;
            Level = level;
            Parent = parent;
            Element = element;
        }

        // updateChildren: push this new value down to every descendant (only ever
        // done for a definite true/false — the tri-state null is a DISPLAY-only
        // state derived from children, never something pushed down onto them).
        // updateParent: ask the parent to recompute its own tri-state afterward.
        private void SetIsChecked(bool? value, bool updateChildren, bool updateParent)
        {
            if (value == _isChecked) return;

            _isChecked = value;

            if (updateChildren && _isChecked.HasValue)
            {
                foreach (VM_ElementTreeNode child in Children)
                    child.SetIsChecked(_isChecked, true, false);
            }

            if (updateParent)
                Parent?.RecomputeFromChildren();

            OnPropertyChanged(nameof(IsChecked));
        }

        // Re-derives this node's own tri-state from its immediate children: all
        // checked -> true, all unchecked -> false, anything mixed -> null.
        private void RecomputeFromChildren()
        {
            bool? state = Children.Count > 0 ? Children[0].IsChecked : false;
            for (int i = 1; i < Children.Count; i++)
            {
                if (Children[i].IsChecked != state)
                {
                    state = null;
                    break;
                }
            }
            SetIsChecked(state, false, true);
        }

        // Sets every node in the subtree to the same explicit state — used by the
        // dialog's "Select All" / "Select None" buttons on the root node.
        // Cascades through the normal SetIsChecked path, so every leaf's
        // PropertyChanged still fires and the live-selection callback still runs.
        // Only affects nodes currently in the tree — i.e. within the current
        // scope; elements checked from a different scope/view are untouched.
        public void SetAll(bool value) => SetIsChecked(value, true, true);

        // Sets the backing field directly with no cascade/parent/notify side
        // effects — used only when constructing a leaf node with its preserved
        // checked state during a tree rebuild, before that node's own live-selection
        // PropertyChanged subscription has been wired up.
        public void SetInitialCheckedQuiet(bool value) => _isChecked = value;

        // Recomputes this node's aggregate tri-state AND InstanceCount bottom-up
        // from its children, recursively. Called once, on the root, right after a
        // full tree rebuild — deliberately bypasses SetIsChecked's cascade-down/
        // notify-parent behavior, since children were just constructed with
        // already-correct values and nothing is bound to the UI yet at this point.
        public void RefreshAggregateRecursive()
        {
            if (Children.Count == 0)
            {
                InstanceCount = 1; // a leaf represents exactly one element
                return; // checked state was set directly at construction
            }

            int totalInstances = 0;
            bool? state = null;
            for (int i = 0; i < Children.Count; i++)
            {
                VM_ElementTreeNode child = Children[i];
                child.RefreshAggregateRecursive();
                totalInstances += child.InstanceCount;

                if (i == 0) state = child.IsChecked;
                else if (child.IsChecked != state) state = null;
            }

            InstanceCount = totalInstances;
            _isChecked = state;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
