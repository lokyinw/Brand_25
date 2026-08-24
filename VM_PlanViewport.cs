using Autodesk.Revit.DB;
using System.ComponentModel;

namespace Brand_25
{
    // Wraps a single plan Viewport (FloorPlan / CeilingPlan / AreaPlan / StructuralPlan)
    // placed on a sheet, for display in both Selection_PlanAlign's source combo box
    // (as a flattened "Sheet : View" entry) and its target tree (grouped under a
    // VM_PlanSheetGroup node). Kept separate from VM_Sheet since that VM represents a
    // whole sheet with no notion of "which viewport", which this command needs.
    public class VM_PlanViewport : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public ViewSheet Sheet { get; }
        public Viewport Viewport { get; }
        public View PlanView { get; }

        public string SheetNumber => Sheet.SheetNumber;
        public string SheetName => Sheet.Name;
        public string ViewName => PlanView.Name;

        // Display label used by both the source ComboBox and the tree's child rows.
        public string DisplayLabel => $"{ViewName}";
        public string ComboLabel => $"{SheetNumber} - {SheetName} : {ViewName}";

        // Paper-space area (view.Scale-normalized crop width x height), used only to
        // pick the "largest plan on this sheet" default for the source combo — never
        // shown to the user.
        public double PaperAreaSqFt { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        // True when this viewport is currently chosen as the source plan in the
        // dialog's combo box — disables (and clears) its checkbox in the target tree,
        // since a plan can't be aligned to itself. Updated by the dialog's code-behind
        // whenever the source combo selection changes.
        private bool _isSourceExcluded;
        public bool IsSourceExcluded
        {
            get => _isSourceExcluded;
            set
            {
                if (_isSourceExcluded == value) return;
                _isSourceExcluded = value;
                if (_isSourceExcluded) IsSelected = false;
                OnPropertyChanged(nameof(IsSourceExcluded));
            }
        }

        public VM_PlanViewport(ViewSheet sheet, Viewport viewport, View planView)
        {
            Sheet = sheet;
            Viewport = viewport;
            PlanView = planView;

            BoundingBoxXYZ cropBox = planView.CropBox;
            double scale = planView.Scale <= 0 ? 1 : planView.Scale;
            double widthFt = (cropBox.Max.X - cropBox.Min.X) / scale;
            double heightFt = (cropBox.Max.Y - cropBox.Min.Y) / scale;
            PaperAreaSqFt = widthFt * heightFt;
        }
    }

    // Groups a sheet's plan viewports together for the target TreeView. The sheet
    // header row itself is not selectable — only its VM_PlanViewport children are.
    public class VM_PlanSheetGroup
    {
        public ViewSheet Sheet { get; }
        public string HeaderText => $"{Sheet.SheetNumber} - {Sheet.Name}";
        public System.Collections.Generic.List<VM_PlanViewport> Plans { get; }

        public VM_PlanSheetGroup(ViewSheet sheet, System.Collections.Generic.List<VM_PlanViewport> plans)
        {
            Sheet = sheet;
            Plans = plans;
        }
    }
}
