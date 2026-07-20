using Autodesk.Revit.DB;

namespace Brand_25
{
    // Wraps a FamilySymbol (tag type) for display in the tag-type combo boxes.
    public class VM_TagType
    {
        public FamilySymbol Symbol { get; }
        public string Name => $"{Symbol.Family.Name}: {Symbol.Name}";

        public VM_TagType(FamilySymbol symbol)
        {
            Symbol = symbol;
        }
    }
}
