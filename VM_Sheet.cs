using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq; // Add this at the top of the file with other using directives
using System.Runtime.CompilerServices;

namespace Brand_25
{
    public class VM_Sheet : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public ViewSheet Sheet { get; }
        public string SheetNumber => Sheet.SheetNumber;
        public string SheetName => Sheet.Name;
        public string SheetSeries => GetParameterValue(Sheet, "BA_SheetSeries", "None");
        public string CurrentRevision => GetCurrentRevision(Sheet);

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public VM_Sheet(ViewSheet sheet)
        {
            Sheet = sheet;
            IsSelected = false;
        }

        private string GetParameterValue(ViewSheet sheet, string parameterName, string defaultValue = "None")
        {
            Parameter param = sheet.LookupParameter(parameterName);
            if (param != null && param.HasValue)
            {
                return param.AsValueString() ?? defaultValue;
            }
            return defaultValue;
        }

        private string GetCurrentRevision(ViewSheet sheet)
        {
            try
            {
                // 1. First try the per-sheet additional revisions (Revit 2023 method)
                if (sheet.GetAdditionalRevisionIds() is ICollection<ElementId> additionalRevisions && additionalRevisions.Count > 0)
                {
                    // Get the last revision in the sequence (most current)
                    ElementId lastRevId = additionalRevisions.Last();
                    if (sheet.Document.GetElement(lastRevId) is Revision revision)
                    {
                        return revision.RevisionNumber;
                    }
                }

                // 2. Check for Revision on Sheet (works for both numbering methods)
                Parameter currentRevParam = sheet.LookupParameter("Current Revision");
                if (currentRevParam?.AsElementId() is ElementId currentRevId && currentRevId != ElementId.InvalidElementId)
                {
                    if (sheet.Document.GetElement(currentRevId) is Revision currentRevision)
                    {
                        return currentRevision.RevisionNumber;
                    }
                }

                // 3. Fallback to project revisions (legacy method)
                ICollection<ElementId> allRevisions = sheet.GetAllRevisionIds();
                if (allRevisions.Count > 0)
                {
                    ElementId lastRevId = allRevisions.Last();
                    if (sheet.Document.GetElement(lastRevId) is Revision revision)
                    {
                        return revision.RevisionNumber;
                    }
                }

                return "None";
            }
            catch
            {
                return "None";
            }
        }
    }
}