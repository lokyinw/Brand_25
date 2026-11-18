using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Brand_25
{
    // Creates one UNPLACED Room (phase "New Construction") for every Mass instance
    // whose BA_Material_AreaGroup has a value, then copies six parameters across:
    //
    //   Mass                    ->  Room
    //   BA_Required Area        ->  BA_BriefedArea
    //   BA_Space Type           ->  BA_Space Type
    //   BA_Space Subtype        ->  BA_Space Subtype
    //   BA_SDT_PartNumber       ->  BA_SDT_PartNumber
    //   BA_Briefed Area Name    ->  Name            (blank -> "unnamed room")
    //   BA_TeachingSpace        ->  BA_TeachingSpace
    //
    // Mass parameters (including family parameters such as BA_TeachingSpace) are looked
    // up on the instance first, then on the mass's type — see GetMassParam.
    //
    // Order of work (nothing in the model is touched until every check has passed):
    //   1. Collect masses with BA_Material_AreaGroup populated.
    //   2. Per-mass check: any mass missing one of the six params is logged + skipped.
    //   3. Category-level check on Room (via doc.ParameterBindings, so it also works when
    //      the model has no rooms yet). A param missing from Room but available as a
    //      project parameter is queued to be bound to Room; a param that exists nowhere
    //      is a failure.
    //   4. Data-type check, mass param vs room param, for every remaining mass. Any
    //      failure (step 3 or 4) -> warning + log + quit, model untouched.
    //   5. Rooms that share a name with a room already in the model are flagged (warning
    //      + log); new rooms are still created, as requested.
    //   6. TransactionGroup: bind missing project params to Room, then create the rooms
    //      and copy values. Any exception rolls the whole group back.
    //
    // Design option: Revit creates new rooms in whichever design option is active; this
    // API call can't choose one.
    [Transaction(TransactionMode.Manual)]
    public class Room_FromMass : IExternalCommand
    {
        private const string credit = "Last Modified by Lok on 2026-11-18. Beta 0.10";

        private const string TargetPhaseName = "New Construction";
        private const string AreaGroupParam = "BA_Material_AreaGroup";
        private const string RoomNameParam = "Name";
        private const string BlankNameReplacement = "unnamed room";

        // Mass parameter -> Room parameter. Order is the order they're copied/logged in.
        private static readonly (string MassParam, string RoomParam)[] ParamMap =
        {
            ("BA_Required Area",     "BA_BriefedArea"),
            ("BA_Space Type",        "BA_Space Type"),
            ("BA_Space Subtype",     "BA_Space Subtype"),
            ("BA_SDT_PartNumber",    "BA_SDT_PartNumber"),
            ("BA_Briefed Area Name", RoomNameParam),
            ("BA_TeachingSpace",     "BA_TeachingSpace"),
        };

        private const string BriefedAreaNameMassParam = "BA_Briefed Area Name";

        // What we know about one target parameter on the Room category.
        private class RoomParamInfo
        {
            public string Name;
            public ForgeTypeId DataType;       // null if FailureReason is set
            public bool IsRoomName;            // built-in ROOM_NAME, no binding involved
            public bool NeedsBinding;          // project param exists, but not yet applied to Room
            public Definition Definition;      // only for NeedsBinding
            public ElementBinding Binding;     // only for NeedsBinding
            public string FailureReason;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            try
            {
                return Run(doc);
            }
            catch (Exception ex)
            {
                new Warning("Unexpected Error",
                    $"Room From Mass could not complete:\n\n{ex.Message}",
                    credit, 16).ShowDialog();
                message = ex.Message;
                return Result.Failed;
            }
        }

        private Result Run(Document doc)
        {
            List<string> log = new List<string>
            {
                "Room From Mass — Log",
                $"Run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Project: {doc.Title}",
                new string('-', 70)
            };
            List<string> issues = new List<string>();

            // Writes the log and shows a large dialog, for every "quit without doing
            // anything" exit. The log button is always offered here.
            Result Abort(string title, string dialogMessage)
            {
                string path = WriteLog(log);
                new WarningLarge(title, dialogMessage + "\n\nNothing was changed in the model. See log for details.",
                    credit, 18, revealPath: path).ShowDialog();
                return Result.Cancelled;
            }

            // ── Phase ─────────────────────────────────────────────────────────

            Phase phase = doc.Phases.Cast<Phase>()
                .FirstOrDefault(p => string.Equals(p.Name, TargetPhaseName, StringComparison.OrdinalIgnoreCase));
            if (phase == null)
            {
                log.Add($"Phase \"{TargetPhaseName}\" does not exist in this project.");
                return Abort("Phase Not Found", $"Phase \"{TargetPhaseName}\" does not exist in this project.");
            }

            // ── Step 1: collect masses with BA_Material_AreaGroup populated ───

            List<Element> masses = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Mass)
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(HasAreaGroupValue)
                .ToList();

            log.Add($"Masses with a value in \"{AreaGroupParam}\": {masses.Count}");

            if (masses.Count == 0)
            {
                new Warning("No Masses", $"No masses with a value in \"{AreaGroupParam}\" were found.", credit, 18).ShowDialog();
                return Result.Succeeded;
            }

            // ── Step 2: per-mass check for the six source params ──────────────

            List<Element> validMasses = new List<Element>();
            int skippedMasses = 0;

            foreach (Element mass in masses)
            {
                List<string> missing = ParamMap
                    .Where(m => GetMassParam(mass, m.MassParam) == null)
                    .Select(m => m.MassParam)
                    .ToList();

                if (missing.Count > 0)
                {
                    skippedMasses++;
                    string entry = $"[Skipped] {Describe(mass)}: missing parameter(s): {string.Join(", ", missing.Select(n => $"\"{n}\""))}";
                    log.Add(entry);
                    issues.Add(entry);
                }
                else
                {
                    validMasses.Add(mass);
                }
            }

            if (validMasses.Count == 0)
            {
                log.Add("No mass has all six required parameters — nothing to create.");
                return Abort("No Usable Masses", "None of the masses have all six required parameters.");
            }

            // ── Step 3: does Room have each target param (or can it get it)? ──

            Category roomCategory = Category.GetCategory(doc, BuiltInCategory.OST_Rooms);

            // Snapshot every project-parameter binding once.
            List<(Definition Def, ElementBinding Binding)> allBindings = new List<(Definition, ElementBinding)>();
            DefinitionBindingMapIterator bindingIterator = doc.ParameterBindings.ForwardIterator();
            while (bindingIterator.MoveNext())
            {
                allBindings.Add((bindingIterator.Key, bindingIterator.Current as ElementBinding));
            }

            Dictionary<string, RoomParamInfo> roomInfos = new Dictionary<string, RoomParamInfo>();
            foreach (var map in ParamMap)
            {
                roomInfos[map.RoomParam] = ResolveRoomParam(map.RoomParam, allBindings, roomCategory);
            }

            // ── Step 4: data-type check, mass param vs room param ─────────────

            List<string> failureLines = new List<string>();

            foreach (RoomParamInfo info in roomInfos.Values.Where(i => i.FailureReason != null))
                failureLines.Add($"Room parameter \"{info.Name}\": {info.FailureReason}");

            // Grouped so a mismatch repeated across hundreds of masses is one dialog line.
            Dictionary<string, List<long>> mismatchGroups = new Dictionary<string, List<long>>();

            foreach (Element mass in validMasses)
            {
                foreach (var map in ParamMap)
                {
                    RoomParamInfo info = roomInfos[map.RoomParam];
                    if (info.FailureReason != null) continue; // already reported

                    Parameter massParam = GetMassParam(mass, map.MassParam);
                    ForgeTypeId massType = massParam.Definition.GetDataType();

                    // Room "Name" is built-in text; compare by storage type so that a
                    // Text or Multiline Text mass param are both acceptable sources.
                    bool agrees = info.IsRoomName
                        ? massParam.StorageType == StorageType.String
                        : massType.Equals(info.DataType);

                    if (!agrees)
                    {
                        string roomTypeLabel = info.IsRoomName ? "Text" : TypeLabel(info.DataType);
                        string key = $"Mass \"{map.MassParam}\" is {TypeLabel(massType)}, but Room \"{map.RoomParam}\" is {roomTypeLabel}";
                        if (!mismatchGroups.TryGetValue(key, out List<long> ids))
                        {
                            ids = new List<long>();
                            mismatchGroups[key] = ids;
                        }
                        ids.Add(mass.Id.Value);
                    }
                }
            }

            foreach (var kvp in mismatchGroups)
            {
                failureLines.Add($"{kvp.Key} ({kvp.Value.Count} mass(es))");
                log.Add($"[Type mismatch] {kvp.Key} — Mass Id(s): {string.Join(", ", kvp.Value)}");
            }

            if (failureLines.Count > 0)
            {
                log.Add("");
                log.Add("Parameter check FAILED:");
                foreach (string line in failureLines) log.Add("  " + line);

                return Abort("Parameter Check Failed",
                    "The Room category and the masses do not agree:\n\n" +
                    string.Join("\n", failureLines.Take(8).Select(l => "\u25CF  " + l)) +
                    (failureLines.Count > 8 ? $"\n...and {failureLines.Count - 8} more." : ""));
            }

            // ── Step 5: flag names that already exist among rooms in the model ─

            HashSet<string> existingRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Element existingRoom in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType())
            {
                string existingName = existingRoom.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                if (!string.IsNullOrWhiteSpace(existingName)) existingRoomNames.Add(existingName.Trim());
            }

            int duplicateNameCount = 0;
            foreach (Element mass in validMasses)
            {
                string finalName = GetFinalRoomName(mass);
                if (existingRoomNames.Contains(finalName.Trim()))
                {
                    duplicateNameCount++;
                    string entry = $"[Duplicate name] {Describe(mass)}: a room named \"{finalName}\" already exists in the model — a new unplaced room will still be created.";
                    log.Add(entry);
                    issues.Add(entry);
                }
            }

            if (duplicateNameCount > 0)
            {
                // Warning's OK sets DialogResult = true; its title-bar X leaves it null.
                bool? proceed = new Warning("Room name(s) already exist",
                    $"{duplicateNameCount} of {validMasses.Count} new room(s) share a name with a room already in the model.\n\n" +
                    "New unplaced rooms will still be created. OK to continue; close this window to cancel.",
                    credit, 16).ShowDialog();

                if (proceed != true)
                {
                    log.Add("User cancelled at the duplicate-name warning. Nothing was changed.");
                    WriteLog(log);
                    return Result.Cancelled;
                }
            }

            // ── Step 6: write (bind params, create rooms, copy values) ────────

            List<string> boundParamNames = new List<string>();
            int created = 0;

            log.Add("");
            log.Add($"Creating unplaced rooms in phase \"{phase.Name}\":");

            using (TransactionGroup group = new TransactionGroup(doc, "LW_Rooms From Masses"))
            {
                group.Start();

                try
                {
                    // 6a. Bind any missing project parameters to the Room category. Kept
                    // in its own committed transaction so the new bindings are definitely
                    // visible on rooms before the next transaction creates any.
                    List<RoomParamInfo> toBind = roomInfos.Values.Where(i => i.NeedsBinding).ToList();
                    if (toBind.Count > 0)
                    {
                        using (Transaction bindTx = new Transaction(doc, "LW_Bind Parameters to Room"))
                        {
                            bindTx.Start();

                            foreach (RoomParamInfo info in toBind)
                            {
                                CategorySet categories = doc.Application.Create.NewCategorySet();
                                foreach (Category c in info.Binding.Categories) categories.Insert(c);
                                categories.Insert(roomCategory);

                                InstanceBinding newBinding = doc.Application.Create.NewInstanceBinding(categories);
                                if (!doc.ParameterBindings.ReInsert(info.Definition, newBinding))
                                    throw new InvalidOperationException($"Could not apply project parameter \"{info.Name}\" to the Room category.");

                                boundParamNames.Add(info.Name);
                                log.Add($"  [Bound] Project parameter \"{info.Name}\" applied to the Room category.");
                            }

                            bindTx.Commit();
                        }
                    }

                    // 6b. Create the rooms and copy the values.
                    using (Transaction roomTx = new Transaction(doc, "LW_Create Rooms From Masses"))
                    {
                        roomTx.Start();

                        foreach (Element mass in validMasses)
                        {
                            Room room = doc.Create.NewRoom(phase);
                            if (room == null)
                            {
                                string issue = $"{Describe(mass)}: Revit did not create a room.";
                                log.Add($"  [Error] {issue}");
                                issues.Add(issue);
                                continue;
                            }
                            created++;

                            string finalName = GetFinalRoomName(mass);
                            int copyProblems = 0;

                            foreach (var map in ParamMap)
                            {
                                Parameter source = GetMassParam(mass, map.MassParam);
                                Parameter target = map.RoomParam == RoomNameParam
                                    ? room.get_Parameter(BuiltInParameter.ROOM_NAME)
                                    : room.LookupParameter(map.RoomParam);

                                string problem = CopyValue(source, target, map.RoomParam == RoomNameParam ? finalName : null);
                                if (problem != null)
                                {
                                    copyProblems++;
                                    string issue = $"{Describe(mass)} -> Room {room.Id.Value}: \"{map.MassParam}\" -> \"{map.RoomParam}\": {problem}";
                                    log.Add($"  [Issue] {issue}");
                                    issues.Add(issue);
                                }
                            }

                            // Read the Name straight back off the room: if Revit accepted the
                            // Set but the room still shows something else, this exposes it.
                            string nameReadBack = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                            if (!string.Equals(nameReadBack, finalName, StringComparison.Ordinal))
                            {
                                copyProblems++;
                                string issue = $"{Describe(mass)} -> Room {room.Id.Value}: Name should be \"{finalName}\" but reads back as \"{nameReadBack ?? "<null>"}\".";
                                log.Add($"  [Issue] {issue}");
                                issues.Add(issue);
                            }

                            log.Add($"  [Created] Room Id {room.Id.Value} (Number {room.Number}) <- {Describe(mass)}, Name \"{finalName}\"" +
                                    (copyProblems > 0 ? $"  ({copyProblems} parameter issue(s))" : ""));
                            log.Add($"      Name source: {DescribeNameSource(mass)}");
                        }

                        roomTx.Commit();
                    }

                    group.Assimilate();
                }
                catch
                {
                    if (group.GetStatus() == TransactionStatus.Started)
                        group.RollBack();
                    throw;
                }
            }

            // ── Summary ───────────────────────────────────────────────────────

            log.Add("");
            log.Add($"{created} room(s) created, {skippedMasses} mass(es) skipped, {duplicateNameCount} duplicate name(s) flagged, " +
                    $"{boundParamNames.Count} parameter(s) bound to Room.");

            string logPath = WriteLog(log);
            bool hasIssues = issues.Count > 0;

            string summary =
                $"{created} unplaced room(s) created in \"{phase.Name}\".\n" +
                (skippedMasses > 0 ? $"{skippedMasses} mass(es) skipped.\n" : "") +
                (duplicateNameCount > 0 ? $"{duplicateNameCount} duplicate room name(s).\n" : "") +
                (boundParamNames.Count > 0 ? $"{boundParamNames.Count} parameter(s) applied to Room.\n" : "") +
                (hasIssues ? $"\n{issues.Count} issues found - see log" : "") +
                (logPath == null ? "\n\nThe log file could not be written." : "");

            new Warning("Room From Mass", summary, credit, 16,
                revealPath: (logPath != null && hasIssues) ? logPath : null).ShowDialog();

            return Result.Succeeded;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool HasAreaGroupValue(Element e)
        {
            Parameter p = GetMassParam(e, AreaGroupParam);
            if (p == null || !p.HasValue) return false;
            return p.StorageType == StorageType.String
                ? !string.IsNullOrWhiteSpace(p.AsString())
                : true;
        }

        private static string GetFinalRoomName(Element mass)
        {
            string name = GetMassParam(mass, BriefedAreaNameMassParam)?.AsString();
            return string.IsNullOrWhiteSpace(name) ? BlankNameReplacement : name;
        }

        // Diagnostic: where BA_Briefed Area Name was found on the mass and what it holds.
        private static string DescribeNameSource(Element mass)
        {
            Parameter sp = GetMassParam(mass, BriefedAreaNameMassParam);
            if (sp == null) return "parameter not found";

            string where = sp.Element != null && sp.Element.Id == mass.Id ? "instance" : "type";
            string raw;
            try { raw = sp.AsString(); }
            catch (Exception ex) { raw = $"<error: {ex.Message}>"; }

            return $"found on {where}, storage {sp.StorageType}, HasValue {sp.HasValue}, AsString \"{raw ?? "<null>"}\", AsValueString \"{sp.AsValueString() ?? "<null>"}\"";
        }

        // Finds a mass parameter by name. A family parameter (or any parameter) can live
        // on the instance OR on the mass's type (FamilySymbol), so look on the instance
        // first and fall back to the type. Returns null only if neither has it.
        // For in-place masses the "type" is the in-place family's own symbol, which this
        // handles the same way.
        private static Parameter GetMassParam(Element mass, string paramName)
        {
            Parameter p = mass.LookupParameter(paramName);
            if (p != null) return p;

            ElementId typeId = mass.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;

            Element massType = mass.Document.GetElement(typeId);
            return massType?.LookupParameter(paramName);
        }

        private static string Describe(Element mass)
        {
            string name;
            try { name = mass.Name; }
            catch { name = "?"; }
            return $"Mass Id {mass.Id.Value} ({name})";
        }

        private static string TypeLabel(ForgeTypeId id)
        {
            try { return LabelUtils.GetLabelForSpec(id); }
            catch { return id?.TypeId ?? "unknown"; }
        }

        // Works out what the Room category offers for one target parameter name.
        private static RoomParamInfo ResolveRoomParam(string name,
            List<(Definition Def, ElementBinding Binding)> allBindings, Category roomCategory)
        {
            RoomParamInfo info = new RoomParamInfo { Name = name };

            if (name == RoomNameParam)
            {
                info.IsRoomName = true;
                info.DataType = SpecTypeId.String.Text;
                return info;
            }

            var candidates = allBindings.Where(b => b.Def.Name == name).ToList();

            // Already applied to Room?
            var alreadyOnRoom = candidates.FirstOrDefault(c => c.Binding != null && c.Binding.Categories.Contains(roomCategory));
            if (alreadyOnRoom.Def != null)
            {
                info.DataType = alreadyOnRoom.Def.GetDataType();
                return info;
            }

            // A project parameter exists, just not on Room — usable only if it is an
            // instance parameter (rooms have no types).
            var instanceCandidate = candidates.FirstOrDefault(c => c.Binding is InstanceBinding);
            if (instanceCandidate.Def != null)
            {
                info.NeedsBinding = true;
                info.Definition = instanceCandidate.Def;
                info.Binding = instanceCandidate.Binding;
                info.DataType = instanceCandidate.Def.GetDataType();
                return info;
            }

            info.FailureReason = candidates.Count > 0
                ? "a project parameter with this name exists but is a Type parameter, which cannot be applied to rooms."
                : "not found on the Room category, and no project parameter with this name exists in the model.";
            return info;
        }

        // Copies one value. Returns null on success, or a short problem description.
        // nameOverride (used only for Room Name) replaces the source string, so a blank
        // BA_Briefed Area Name becomes "unnamed room".
        private static string CopyValue(Parameter source, Parameter target, string nameOverride)
        {
            if (source == null) return "source parameter not found.";
            if (target == null) return "parameter not found on the new room.";
            if (target.IsReadOnly) return "parameter is read-only on the room.";

            try
            {
                if (nameOverride != null)
                {
                    return target.Set(nameOverride) ? null : "Revit rejected the value.";
                }

                if (source.StorageType != target.StorageType)
                    return $"storage type differs ({source.StorageType} vs {target.StorageType}).";

                switch (source.StorageType)
                {
                    case StorageType.String:
                        // Blank source is copied as blank.
                        return target.Set(source.AsString() ?? "") ? null : "Revit rejected the value.";

                    case StorageType.Double:
                        if (!source.HasValue) return "mass value is empty; left at default.";
                        return target.Set(source.AsDouble()) ? null : "Revit rejected the value.";

                    case StorageType.Integer:
                        if (!source.HasValue) return "mass value is empty; left at default.";
                        return target.Set(source.AsInteger()) ? null : "Revit rejected the value.";

                    case StorageType.ElementId:
                        if (!source.HasValue) return "mass value is empty; left at default.";
                        return target.Set(source.AsElementId()) ? null : "Revit rejected the value.";

                    default:
                        return $"unsupported storage type {source.StorageType}.";
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string WriteLog(List<string> lines)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), $"RoomFromMass_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");
                File.WriteAllLines(path, lines);
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
