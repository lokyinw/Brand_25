using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Brand_25
{
    public class VM_Room
    {
        public Room Room { get; }
        public string Number => GetParameterValue(Room, "Number");
        public string Name => GetParameterValue(Room, "Name");
        public string PhaseName => GetPhaseName(Room);
        public string DesignOptionName => GetDesignOptionParameterValue(Room);
        public bool IsUnbounded { get; }
        public bool IsUnplaced { get; }
        public bool IsSelected { get; set; }
        public bool ShouldBeGrayedOut => IsUnbounded || IsUnplaced;

        public VM_Room(Room room)
        {
            Room = room;
            double area = room.Area;
            bool hasLocationPoint = room.Location is LocationPoint;
            IsUnbounded = area == 0 && hasLocationPoint;
            IsUnplaced = area == 0 && !hasLocationPoint;
            IsSelected = !(IsUnbounded || IsUnplaced) && false;
        }

        private string GetParameterValue(Room room, string parameterName, string defaultValue = "None")
        {
            foreach (Parameter param in room.Parameters)
            {
                if (param.Definition.Name == parameterName)
                {
                    return param.AsValueString() ?? defaultValue;
                }
            }
            return defaultValue;
        }

        private string GetPhaseName(Room room)
        {
            foreach (Parameter param in room.Parameters)
            {
                if (param.Definition.Name == "Phase")
                {
                    return param.AsValueString() ?? "None";
                }
            }
            return "None";
        }

        private string GetDesignOptionParameterValue(Room room)
        {
            foreach (Parameter param in room.Parameters)
            {
                if (param.Definition.Name == "Design Option" && param.StorageType == StorageType.String)
                {
                    return param.AsValueString() ?? "Main Model";
                }
            }
            return "Main Model";
        }
    }
}