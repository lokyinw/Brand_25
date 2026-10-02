using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Brand_25
{
    /// <summary>
    /// One row in the floor type list. Thickness is model-space internal feet
    /// (no suffix, per codebase unit naming); ThicknessMillimetres is for display only.
    /// </summary>
    public class VM_FloorTypeItem
    {
        public FloorType FloorType { get; }
        public string Name { get; }
        public double Thickness { get; }
        public string DisplayLabel { get; }

        public VM_FloorTypeItem(FloorType floorType)
        {
            FloorType = floorType;
            Name = floorType.Name;

            // Compound width first; fall back to the type's default thickness
            // parameter for the odd type with no compound structure.
            double thickness = floorType.GetCompoundStructure()?.GetWidth() ?? 0.0;
            if (thickness <= 0.0)
                thickness = floorType.get_Parameter(BuiltInParameter.FLOOR_ATTR_DEFAULT_THICKNESS_PARAM)?.AsDouble() ?? 0.0;
            Thickness = thickness;

            double thicknessMillimetres = UnitUtils.ConvertFromInternalUnits(thickness, UnitTypeId.Millimeters);
            DisplayLabel = $"{Name}   ({thicknessMillimetres:0.#} mm)";
        }
    }

    /// <summary>
    /// State for Selection_FloorFinish. Survives the close/pick/reopen loop in
    /// Room_CreateFloorFinish, so the dialog can be rebuilt each round without
    /// losing the user's choices.
    /// </summary>
    public class VM_FloorFinish : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string ContextText { get; }
        public List<VM_FloorTypeItem> FloorTypes { get; }

        public List<ElementId> SelectedRoomIds { get; private set; } = new List<ElementId>();

        public string RoomSummary => SelectedRoomIds.Count == 0
            ? "No rooms selected"
            : $"{SelectedRoomIds.Count} room(s) selected";

        private VM_FloorTypeItem _selectedFloorType;
        public VM_FloorTypeItem SelectedFloorType
        {
            get => _selectedFloorType;
            set { _selectedFloorType = value; OnPropertyChanged(); }
        }

        // Kept as text so the dialog can validate it; parsed value lands in OffsetMillimetres.
        private string _offsetText = "0";
        public string OffsetText
        {
            get => _offsetText;
            set { _offsetText = value; OnPropertyChanged(); }
        }

        // Model-space millimetres as entered (negative allowed). Set by the dialog on Create.
        public double OffsetMillimetres { get; set; }

        private bool _skipExisting = true;
        public bool SkipExisting
        {
            get => _skipExisting;
            set { _skipExisting = value; OnPropertyChanged(); }
        }

        // Carried between pick rounds so the dialog reopens where the user left it.
        public string FilterText { get; set; } = "";
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;

        public VM_FloorFinish(string contextText, List<VM_FloorTypeItem> floorTypes)
        {
            ContextText = contextText;
            FloorTypes = floorTypes;
        }

        public void SetRooms(List<ElementId> roomIds)
        {
            SelectedRoomIds = roomIds ?? new List<ElementId>();
            OnPropertyChanged(nameof(SelectedRoomIds));
            OnPropertyChanged(nameof(RoomSummary));
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
