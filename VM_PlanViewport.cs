using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Brand_25
{
    // A plan viewport placed on a sheet, shown in Selection_PlanAlign's source combo
    // and (under a VM_PlanSheetGroup) in its target tree.
    public class VM_PlanViewport : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public ViewSheet Sheet { get; }
        // Settable only so the command can swap in the new element if a type change replaces it.
        public Viewport Viewport { get; internal set; }
        public View PlanView { get; }

        public string SheetNumber => Sheet.SheetNumber;
        public string SheetName => Sheet.Name;
        public string ViewName => PlanView.Name;
        public int Scale => PlanView.Scale;

        public string DisplayLabel => ViewName;
        public string ComboLabel => $"{SheetNumber} - {SheetName} : {ViewName}";

        // Paper-space crop area; only used to order the plans on a sheet largest-first.
        public double PaperAreaSqFt { get; }

        // Sheet-space direction of model +X, set by the command; used for the rotation
        // comparison. Null when the plan could not be measured.
        public XYZ RotationDirection { get; set; }

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

        // True while this viewport is the chosen source — a plan can't be aligned to itself.
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
                OnPropertyChanged(nameof(CanSelect));
            }
        }

        // Why this plan can't be aligned to the current source (different scale or
        // rotation); null when it can.
        private string _incompatibleReason;
        public string IncompatibleReason
        {
            get => _incompatibleReason;
            set
            {
                if (_incompatibleReason == value) return;
                _incompatibleReason = value;
                if (!string.IsNullOrEmpty(_incompatibleReason)) IsSelected = false;
                OnPropertyChanged(nameof(IncompatibleReason));
                OnPropertyChanged(nameof(CanSelect));
            }
        }

        public bool CanSelect => !IsSourceExcluded && string.IsNullOrEmpty(IncompatibleReason);

        public VM_PlanViewport(ViewSheet sheet, Viewport viewport, View planView)
        {
            Sheet = sheet;
            Viewport = viewport;
            PlanView = planView;

            BoundingBoxXYZ cropBox = planView.CropBox;
            double scale = planView.Scale <= 0 ? 1 : planView.Scale;
            PaperAreaSqFt = (cropBox.Max.X - cropBox.Min.X) / scale * ((cropBox.Max.Y - cropBox.Min.Y) / scale);
        }

        // Returns the reason this plan can't be aligned to the given source, or null if it can.
        public string GetIncompatibility(VM_PlanViewport source)
        {
            if (Scale != source.Scale)
                return $"Scale 1:{Scale} differs from the source (1:{source.Scale})";
            if (source.RotationDirection == null)
                return "The source plan's orientation could not be measured";
            if (RotationDirection == null)
                return "This plan's orientation could not be measured";
            if (Math.Abs(source.RotationDirection.DotProduct(RotationDirection)) < Plan_AlignOnSheets.RotationAlignedDotThreshold)
                return "Rotation differs from the source";
            return null;
        }
    }

    // Groups a sheet's plan viewports under a non-selectable header row in the target tree.
    public class VM_PlanSheetGroup : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public ViewSheet Sheet { get; }
        public string HeaderText => $"{Sheet.SheetNumber} - {Sheet.Name}";
        public List<VM_PlanViewport> Plans { get; }

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public VM_PlanSheetGroup(ViewSheet sheet, List<VM_PlanViewport> plans)
        {
            Sheet = sheet;
            Plans = plans;
        }

        // Collapses the sheet when none of its plans can be selected, expands it otherwise.
        public void RefreshExpansion() => IsExpanded = Plans.Any(p => p.CanSelect);
    }
}
