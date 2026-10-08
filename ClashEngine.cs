using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace MEP_Check_Clash_ver1
{
    public static class ClashEngine
    {
        public const double Ft3ToCm3 = 28316.846592;
        public static readonly string[] MepCategories = { "OST_PipeCurves", "OST_PipeFitting", "OST_PipeAccessory", "OST_DuctCurves", "OST_DuctFitting", "OST_DuctAccessory", "OST_CableTray", "OST_CableTrayFitting", "OST_Conduit", "OST_ConduitFitting", "OST_MechanicalEquipment", "OST_ElectricalEquipment" };
        public static readonly string[] ArchitectureCategories = { "OST_Walls", "OST_Floors", "OST_Ceilings", "OST_Roofs", "OST_StructuralFraming", "OST_StructuralColumns", "OST_Columns", "OST_GenericModel", "OST_Doors", "OST_Windows" };
        public static HashSet<long> CategoryIds(IEnumerable<string> names)
        {
            return new HashSet<long>(names.Select(n => (long)(BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), n)));
        }
        public static List<ModelSource> Sources(Document document)
        {
            var result = new List<ModelSource> { new ModelSource { Label="Current model", Key="HOST", Document=document, Transform=Transform.Identity } };
            foreach (var link in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().OrderBy(x => x.Name))
            {
                var linked = link.GetLinkDocument();
                if (linked != null) result.Add(new ModelSource { Label=link.Name + " [ID " + Ids.Value(link.Id) + "]", Key="LINK:"+link.UniqueId, Document=linked, Transform=link.GetTotalTransform(), Link=link });
            }
            return result;
        }
        public static List<Choice> Categories(ModelSource source)
        {
            var found = new Dictionary<long,string>();
            using (var options=new Options { DetailLevel=ViewDetailLevel.Fine,IncludeNonVisibleObjects=true,ComputeReferences=false })
            foreach (var element in new FilteredElementCollector(source.Document).WhereElementIsNotElementType())
            {
                var category = element.Category;
                if (category==null || category.CategoryType!=CategoryType.Model || found.ContainsKey(Ids.Value(category.Id)) || !PhysicalElement(element)) continue;
                try
                {
                    if (element.get_BoundingBox(null)==null) continue;
                    using (var geometry=element.get_Geometry(options))
                        if (HasSolid(geometry)) found[Ids.Value(category.Id)]=category.Name;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }
            // Always offer physical MEP categories, even when the selected model
            // currently has no instances (notably Cable Trays / Cable Tray Fittings).
            foreach (var name in MepCategories)
            {
                var builtin=(BuiltInCategory)Enum.Parse(typeof(BuiltInCategory),name);
                var category=Category.GetCategory(source.Document,builtin);
                if (category!=null) found[Ids.Value(category.Id)]=category.Name;
            }
            return found.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase).Select(p => new Choice { Id=p.Key, Name=p.Value }).ToList();
        }
        private static bool PhysicalElement(Element element)
        {
            return !(element is MEPSystem) && !(element is Group) && !(element is RevitLinkInstance) && !(element is SpatialElement) && !(element is AssemblyInstance);
        }
        private static bool HasSolid(GeometryElement geometry)
        {
            if (geometry==null) return false;
            foreach (var item in geometry)
            {
                var solid=item as Solid;
                if (solid!=null && solid.Faces.Size>0 && Math.Abs(solid.Volume)>1e-12) return true;
                var instance=item as GeometryInstance;
                if (instance!=null) using (var nested=instance.GetInstanceGeometry()) if (HasSolid(nested)) return true;
            }
            return false;
        }
        public static void ValidateSource(ModelSource source, Document host)
        {
            if (source == null || !source.Document.IsValidObject) throw new InvalidOperationException("The model source is no longer available. Refresh sources.");
            if (source.Link != null)
            {
                if (!source.Link.IsValidObject || host.GetElement(source.Link.Id) == null || !DocumentIdentity.Same(source.Link.GetLinkDocument(), source.Document))
                    throw new InvalidOperationException("A Revit link was unloaded or replaced. Refresh sources.");
                source.Transform = source.Link.GetTotalTransform();
            }
        }
        private static IEnumerable<Element> Collect(ModelSource source, List<long> categories, CheckScope scope)
        {
            if (categories.Count == 0) return Enumerable.Empty<Element>();
            if (scope?.Kind == "active_view" && source.Link != null && scope.VisibleLinks != null && !scope.VisibleLinks.Contains(Ids.Value(source.Link.Id))) return Enumerable.Empty<Element>();
            var collector = scope?.Kind == "active_view" && source.Link == null
                ? new FilteredElementCollector(source.Document, scope.ViewId) : new FilteredElementCollector(source.Document);
            return collector.WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(categories.Select(Ids.Create).ToList()));
        }
        public static string ParameterText(Document doc, Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue) return "";
            var formatted = parameter.AsValueString();
            if (!string.IsNullOrWhiteSpace(formatted)) return formatted.Trim();
            switch (parameter.StorageType)
            {
                case StorageType.String: return (parameter.AsString() ?? "").Trim();
                case StorageType.Integer: return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double: return parameter.AsDouble().ToString(CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    var id = parameter.AsElementId();
                    return doc.GetElement(id)?.Name ?? Ids.Value(id).ToString(CultureInfo.InvariantCulture);
                default: return "";
            }
        }
        private static IEnumerable<Element> ParameterTargets(Element element)
        {
            yield return element;
            var type = element.Document.GetElement(element.GetTypeId());
            if (type != null) yield return type;
        }
        public static Dictionary<string,string> Parameters(Element element)
        {
            var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in ParameterTargets(element))
            foreach (Parameter parameter in target.Parameters)
            {
                try
                {
                    var name = parameter.Definition.Name.Trim();
                    var value = ParameterText(element.Document, parameter);
                    if (name.Length > 0 && value.Length > 0 && !result.ContainsKey(name)) result.Add(name, value);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }
            return result;
        }
        public static Dictionary<string,List<string>> ParameterMetadata(ModelSource source, List<long> categories)
        {
            var result = new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in Collect(source,categories,null).Take(5000))
            foreach (var pair in Parameters(element))
            {
                if (!result.ContainsKey(pair.Key)) result[pair.Key] = new HashSet<string>();
                if (result[pair.Key].Count < 500) result[pair.Key].Add(pair.Value);
            }
            return result.ToDictionary(p => p.Key, p => p.Value.OrderBy(v => v,StringComparer.OrdinalIgnoreCase).ToList(),StringComparer.OrdinalIgnoreCase);
        }
        public static bool Matches(Dictionary<string,string> values, List<FilterRule> rules, bool any)
        {
            var active = rules.Where(r => !string.IsNullOrWhiteSpace(r.Parameter) && !string.IsNullOrWhiteSpace(r.Value)).ToList();
            if (active.Count == 0) return true;
            Func<FilterRule,bool> match = rule =>
            {
                string actual;
                if (!values.TryGetValue(rule.Parameter.Trim(),out actual)) return false;
                actual = actual.Trim();
                var expected = rule.Value.Trim();
                switch ((rule.Operator ?? "Equals").ToLowerInvariant())
                {
                    case "contains": return actual.IndexOf(expected,StringComparison.OrdinalIgnoreCase) >= 0;
                    case "starts with": return actual.StartsWith(expected,StringComparison.OrdinalIgnoreCase);
                    case "not equals": return !actual.Equals(expected,StringComparison.OrdinalIgnoreCase);
                    default: return actual.Equals(expected,StringComparison.OrdinalIgnoreCase);
                }
            };
            return any ? active.Any(match) : active.All(match);
        }
        public static ElementId AssignedLevel(Element element)
        {
            if (Ids.Value(element.LevelId) >= 0) return element.LevelId;
            foreach (var name in new[] { "INSTANCE_REFERENCE_LEVEL_PARAM", "FAMILY_LEVEL_PARAM", "FAMILY_BASE_LEVEL_PARAM", "RBS_START_LEVEL_PARAM", "RBS_REFERENCE_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "LEVEL_PARAM", "WALL_BASE_CONSTRAINT" })
            {
                BuiltInParameter builtIn;
                if (!Enum.TryParse(name,out builtIn)) continue;
                var parameter = element.get_Parameter(builtIn);
                if (parameter?.StorageType == StorageType.ElementId && Ids.Value(parameter.AsElementId()) >= 0) return parameter.AsElementId();
            }
            return ElementId.InvalidElementId;
        }
        private static string LevelName(Element element)
        {
            var level = element.Document.GetElement(AssignedLevel(element));
            if (level != null) return level.Name;
            return new[] { "Reference Level", "Level", "Base Level" }.Select(n => ParameterText(element.Document,element.LookupParameter(n))).FirstOrDefault(v => v.Length > 0) ?? "";
        }
        public static CheckScope Scope(Document host, string kind, List<long> levels)
        {
            var scope = new CheckScope { Kind=kind, LevelIds=new HashSet<long>(levels) };
            if (kind == "levels")
            {
                var ordered = new FilteredElementCollector(host).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                foreach (var level in ordered.Where(l => scope.LevelIds.Contains(Ids.Value(l.Id))))
                {
                    var next = ordered.FirstOrDefault(l => l.Elevation > level.Elevation + 1e-9);
                    scope.Bands.Add(Tuple.Create(level.Elevation,next == null ? (double?)null : next.Elevation));
                }
            }
            if (kind == "active_view")
            {
                var view = host.ActiveView;
                if (!FilteredElementCollector.IsViewValidForElementIteration(host,view.Id)) throw new InvalidOperationException("This view cannot be used for an Active View check.");
                scope.ViewId = view.Id;
                scope.VisibleLinks = new HashSet<long>(new FilteredElementCollector(host,view.Id).OfClass(typeof(RevitLinkInstance)).Select(e => Ids.Value(e.Id)));
                var view3D = view as View3D;
                if (view3D != null && view3D.IsSectionBoxActive) scope.Bounds = Bounds.From(view3D.GetSectionBox(),Transform.Identity);
                else if (view.CropBoxActive) scope.Bounds = Bounds.From(view.CropBox,Transform.Identity);
            }
            return scope;
        }
        private static bool InScope(ModelSource source, Element element, Bounds bounds, CheckScope scope)
        {
            if (scope.Kind == "active_view") return scope.Bounds == null || bounds.Intersects(scope.Bounds);
            if (scope.Kind != "levels") return true;
            if (source.Link == null)
            {
                var assigned = Ids.Value(AssignedLevel(element));
                if (assigned >= 0) return scope.LevelIds.Contains(assigned);
            }
            return scope.Bands.Any(b => bounds.Max.Z >= b.Item1 && (!b.Item2.HasValue || bounds.Min.Z < b.Item2.Value));
        }
        public static List<ElementRecord> Records(ModelSource source, List<long> categories, CheckScope scope, List<FilterRule> rules, bool any)
        {
            var result = new List<ElementRecord>();
            foreach (var element in Collect(source,categories,scope))
            {
                if (!PhysicalElement(element)) continue;
                if (rules.Count > 0 && !Matches(Parameters(element),rules,any)) continue;
                var bounds = Bounds.From(element.get_BoundingBox(null),source.Transform);
                if (bounds != null && InScope(source,element,bounds,scope)) result.Add(new ElementRecord { Source=source, Element=element, Bounds=bounds });
            }
            return result;
        }
        private static IEnumerable<string> Cells(Bounds box)
        {
            const double size = 20.0;
            // Expand by the same tolerance as the bounding-box test at grid boundaries.
            var minX = (long)Math.Floor((box.Min.X-1e-8)/size); var maxX = (long)Math.Floor((box.Max.X+1e-8)/size);
            var minY = (long)Math.Floor((box.Min.Y-1e-8)/size); var maxY = (long)Math.Floor((box.Max.Y+1e-8)/size);
            var minZ = (long)Math.Floor((box.Min.Z-1e-8)/size); var maxZ = (long)Math.Floor((box.Max.Z+1e-8)/size);
            if ((double)(maxX-minX+1)*(maxY-minY+1)*(maxZ-minZ+1) > 1000) return null;
            var cells = new List<string>();
            for (var x=minX; x<=maxX; x++) for (var y=minY; y<=maxY; y++) for (var z=minZ; z<=maxZ; z++) cells.Add(x+","+y+","+z);
            return cells;
        }
        public static IEnumerable<Tuple<ElementRecord,ElementRecord>> Candidates(List<ElementRecord> a, List<ElementRecord> b, bool sameSource)
        {
            var grid = new Dictionary<string,List<int>>(); var global = new List<int>();
            for (var i=0; i<b.Count; i++)
            {
                var cells = Cells(b[i].Bounds);
                if (cells == null) { global.Add(i); continue; }
                foreach (var cell in cells) { if (!grid.ContainsKey(cell)) grid[cell] = new List<int>(); grid[cell].Add(i); }
            }
            var seen = new HashSet<string>();
            foreach (var left in a)
            {
                var cells = Cells(left.Bounds);
                var indices = cells == null ? new HashSet<int>(Enumerable.Range(0,b.Count)) : new HashSet<int>(global);
                if (cells != null) foreach (var cell in cells) { List<int> list; if (grid.TryGetValue(cell,out list)) indices.UnionWith(list); }
                foreach (var index in indices.OrderBy(i => i))
                {
                    var right = b[index]; var idA = Ids.Value(left.Element.Id); var idB = Ids.Value(right.Element.Id);
                    if (sameSource && idA == idB) continue;
                    var key = sameSource ? Math.Min(idA,idB)+":"+Math.Max(idA,idB) : idA+":"+idB;
                    if (!left.Bounds.Intersects(right.Bounds) || !seen.Add(key)) continue;
                    yield return Tuple.Create(left,right);
                }
            }
        }
        private static void AddSolids(GeometryElement geometry, List<Solid> output)
        {
            if (geometry == null) return;
            foreach (var item in geometry)
            {
                var solid = item as Solid;
                // Cache owned copies: document/instance geometry is borrowed and
                // must not be kept or disposed as if owned across ExternalEvents.
                if (solid != null && solid.Faces.Size > 0 && Math.Abs(solid.Volume) > 1e-12) output.Add(SolidUtils.Clone(solid));
                var instance = item as GeometryInstance;
                if (instance != null)
                    using (var nested=instance.GetInstanceGeometry()) AddSolids(nested,output);
            }
        }
        public static List<Solid> Solids(ElementRecord record, Options options, ref int warnings)
        {
            var result = new List<Solid>();
            try { using (var geometry=record.Element.get_Geometry(options)) AddSolids(geometry,result); }
            catch (Exception) { warnings++; }
            if (record.Source.Link == null) return result;
            var transformed = new List<Solid>();
            foreach (var solid in result)
            {
                try { transformed.Add(SolidUtils.CreateTransformed(solid,record.Source.Transform)); }
                catch (Exception) { warnings++; }
                finally { solid.Dispose(); }
            }
            return transformed;
        }
        public static bool Connected(ElementRecord a,ElementRecord b)
        {
            if (a.Source.Key!=b.Source.Key) return false;
            var curve=a.Element as MEPCurve; var family=a.Element as FamilyInstance; var fabrication=a.Element as FabricationPart;
            var manager=curve?.ConnectorManager ?? family?.MEPModel?.ConnectorManager ?? fabrication?.ConnectorManager;
            if (manager==null) return false;
            foreach (Connector connector in manager.Connectors)
            {
                if (connector.ConnectorType==ConnectorType.Logical) continue;
                foreach (Connector other in connector.AllRefs)
                    if (other.ConnectorType!=ConnectorType.Logical && Ids.Value(other.Owner.Id)==Ids.Value(b.Element.Id) && connector.IsConnectedTo(other)) return true;
            }
            return false;
        }
        private static string Detail(Element element, string[] builtins, string[] names)
        {
            foreach (var name in builtins)
            {
                BuiltInParameter builtIn;
                if (!Enum.TryParse(name,out builtIn)) continue;
                foreach (var target in ParameterTargets(element))
                {
                    var value = ParameterText(element.Document,target.get_Parameter(builtIn));
                    if (value.Length > 0) return value;
                }
            }
            var values = Parameters(element);
            foreach (var name in names) { string value; if (values.TryGetValue(name,out value)) return value; }
            return "";
        }
        private static string[] Details(Element element)
        {
            return new[] {
                Detail(element,new[] {"RBS_PIPING_SYSTEM_TYPE_PARAM","RBS_DUCT_SYSTEM_TYPE_PARAM","RBS_CABLETRAYCONDUIT_SYSTEM_TYPE","RBS_SYSTEM_NAME_PARAM"},new[] {"System Type","System Name","MEP System"}),
                Detail(element,new[] {"RBS_SYSTEM_CLASSIFICATION_PARAM","RBS_PIPE_CONNECTOR_SYSTEM_CLASSIFICATION_PARAM","RBS_DUCT_CONNECTOR_SYSTEM_CLASSIFICATION_PARAM"},new[] {"System Classification","Classification"}),
                Detail(element,new[] {"RBS_CTC_SERVICE_TYPE","RBS_SERVICE_TYPE_PARAM","FABRICATION_SERVICE_NAME","FABRICATION_SERVICE_PARAM","MEP_SEGMENT_SYSTEMORSERVICE"},new[] {"Service Type","Service Name","Fabrication Service","Service"}) };
        }
        public static ClashResult Result(int number, ElementRecord a, ElementRecord b, double volume, XYZ point)
        {
            var da = Details(a.Element); var db = Details(b.Element);
            var levelA = LevelName(a.Element);
            var result = new ClashResult { Number=number, RecordA=a, RecordB=b, SourceA=a.Source.Label, SourceB=b.Source.Label,
                IdA=Ids.Value(a.Element.Id), IdB=Ids.Value(b.Element.Id), UniqueIdA=a.Element.UniqueId, UniqueIdB=b.Element.UniqueId,
                CategoryA=a.Element.Category?.Name ?? "", CategoryB=b.Element.Category?.Name ?? "",
                TypeA=a.Source.Document.GetElement(a.Element.GetTypeId())?.Name ?? "", TypeB=b.Source.Document.GetElement(b.Element.GetTypeId())?.Name ?? "",
                SystemA=da[0], ClassificationA=da[1], ServiceA=da[2], SystemB=db[0], ClassificationB=db[1], ServiceB=db[2],
                Level=levelA.Length > 0 ? levelA : LevelName(b.Element), VolumeCm3=Math.Round(volume*Ft3ToCm3,2), Point=point };
            var keys = new[] {a.CacheKey,b.CacheKey}.OrderBy(k => k,StringComparer.Ordinal).ToArray();
            result.ClashKey = string.Format(CultureInfo.InvariantCulture,"{0}::{1}::{2:0},{3:0},{4:0}",keys[0],keys[1],Math.Round(result.Xmm/10,MidpointRounding.ToEven),Math.Round(result.Ymm/10,MidpointRounding.ToEven),Math.Round(result.Zmm/10,MidpointRounding.ToEven));
            return result;
        }
    }

    // The UI schedules small batches through ExternalEvent, allowing cancellation
    // without calling the Revit API from a worker thread or pumping nested UI events.
    public sealed class ClashSession : IDisposable
    {
        private readonly List<Tuple<ElementRecord,ElementRecord>> pairs;
        private readonly Dictionary<string,List<Solid>> cache = new Dictionary<string,List<Solid>>();
        private readonly Options options;
        private readonly ClashCriteria criteria;
        private int current;
        public List<ClashResult> Results { get; } = new List<ClashResult>();
        public int ElementsA { get; }
        public int ElementsB { get; }
        public int Warnings;
        public int Candidates { get { return pairs.Count; } }
        public int Checked { get { return current; } }
        public bool CancelRequested { get; set; }
        public bool Completed { get { return CancelRequested || current >= pairs.Count; } }
        public ClashSession(ModelSource a, List<long> idsA, ModelSource b, List<long> idsB, CheckScope scope, List<FilterRule> filtersA, List<FilterRule> filtersB, bool anyA, bool anyB, bool nonVisible, ClashCriteria criteria = null)
        {
            this.criteria=criteria ?? new ClashCriteria(); this.criteria.Validate();
            var recordsA = ClashEngine.Records(a,idsA,scope,filtersA,anyA); var recordsB = ClashEngine.Records(b,idsB,scope,filtersB,anyB);
            ElementsA=recordsA.Count; ElementsB=recordsB.Count;
            pairs=ClashEngine.Candidates(recordsA,recordsB,a.Key==b.Key).ToList();
            options=new Options { DetailLevel=ViewDetailLevel.Fine, IncludeNonVisibleObjects=nonVisible, ComputeReferences=false };
        }
        private List<Solid> Cached(ElementRecord record)
        {
            List<Solid> solids;
            if (!cache.TryGetValue(record.CacheKey,out solids)) cache[record.CacheKey] = solids = ClashEngine.Solids(record,options,ref Warnings);
            return solids;
        }
        public void Step()
        {
            var deadline=DateTime.UtcNow.AddMilliseconds(100);
            for (var n=0; !Completed && n<32; n++)
            {
                var pair=pairs[current++]; double volume=0; var weighted=XYZ.Zero; Bounds overlapBounds=null;
                if (criteria.IgnoreConnectedMep)
                {
                    try { if (ClashEngine.Connected(pair.Item1,pair.Item2) || ClashEngine.Connected(pair.Item2,pair.Item1)) continue; }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException) { Warnings++; }
                }
                foreach (var a in Cached(pair.Item1)) foreach (var b in Cached(pair.Item2))
                {
                    try
                    {
                        using (var intersection=BooleanOperationsUtils.ExecuteBooleanOperation(a,b,BooleanOperationsType.Intersect))
                        {
                            var v=Math.Abs(intersection.Volume);
                            if (v<=1e-12) continue;
                            var bounds=Bounds.From(intersection.GetBoundingBox(),Transform.Identity);
                            if (overlapBounds==null) overlapBounds=bounds;
                            else
                            {
                                overlapBounds.Min=new XYZ(Math.Min(overlapBounds.Min.X,bounds.Min.X),Math.Min(overlapBounds.Min.Y,bounds.Min.Y),Math.Min(overlapBounds.Min.Z,bounds.Min.Z));
                                overlapBounds.Max=new XYZ(Math.Max(overlapBounds.Max.X,bounds.Max.X),Math.Max(overlapBounds.Max.Y,bounds.Max.Y),Math.Max(overlapBounds.Max.Z,bounds.Max.Z));
                            }
                            XYZ center;
                            try { center=intersection.ComputeCentroid(); }
                            catch (Exception) { var box=intersection.GetBoundingBox(); center=box.Transform.OfPoint((box.Min+box.Max)*0.5); }
                            volume+=v; weighted+=center*v;
                        }
                    }
                    catch (Exception) { Warnings++; }
                }
                if (overlapBounds!=null && criteria.Accepts(volume*ClashEngine.Ft3ToCm3,
                    (overlapBounds.Max.X-overlapBounds.Min.X)*304.8,(overlapBounds.Max.Y-overlapBounds.Min.Y)*304.8,(overlapBounds.Max.Z-overlapBounds.Min.Z)*304.8))
                    Results.Add(ClashEngine.Result(Results.Count+1,pair.Item1,pair.Item2,volume,weighted/volume));
                if (DateTime.UtcNow>=deadline) break;
            }
        }
        public void Dispose()
        {
            foreach (var solid in cache.Values.SelectMany(x => x)) solid.Dispose();
            cache.Clear(); options.Dispose();
        }
    }
}
