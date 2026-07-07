using Autodesk.Revit.DB;

namespace Brand_25
{
    public class VM_ElevType
    {
        public ViewFamilyType ElevType { get; }
        public string Name => ElevType.Name;

        public VM_ElevType(ViewFamilyType elevType)
        {
            ElevType = elevType;
        }
    }
}