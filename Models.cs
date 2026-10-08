using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;
using Autodesk.Revit.DB;

namespace MEP_Check_Clash_ver1
{
    public static class DocumentIdentity
    {
        // Revit may return different managed wrappers for one open document.
        // Its Equals override compares the underlying document, unlike ==.
        public static bool Same(Document left, Document right)
        {
            return left != null && right != null && left.IsValidObject && right.IsValidObject && left.Equals(right);
        }
    }
    public static class Ids
    {
        public static long Value(ElementId id)
        {
            if (id == null) return -1;
#if REVIT2025 || REVIT2024
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }
        public static ElementId Create(long value)
        {
#if REVIT2025 || REVIT2024
            return new ElementId(value);
#else
            return new ElementId(checked((int)value));
#endif
        }
    }
    public class ModelSource
    {
        public string Label { get; set; }
        public string Key { get; set; }
        public Document Document { get; set; }
        public Transform Transform { get; set; }
        public RevitLinkInstance Link { get; set; }
    }
    public class Choice : INotifyPropertyChanged
    {
        public long Id { get; set; }
        public string Name { get; set; }
        private bool selected;
        public bool IsChecked { get { return selected; } set { selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsChecked")); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }
    public class Bounds
    {
        public XYZ Min { get; set; }
        public XYZ Max { get; set; }
        public bool Intersects(Bounds b)
        {
            const double t = 1e-8;
            return Max.X >= b.Min.X-t && b.Max.X >= Min.X-t && Max.Y >= b.Min.Y-t && b.Max.Y >= Min.Y-t && Max.Z >= b.Min.Z-t && b.Max.Z >= Min.Z-t;
        }
        public static Bounds From(BoundingBoxXYZ box, Transform source)
        {
            if (box == null) return null;
            var min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
            var max = new XYZ(double.MinValue, double.MinValue, double.MinValue);
            foreach (var x in new[] { box.Min.X, box.Max.X })
            foreach (var y in new[] { box.Min.Y, box.Max.Y })
            foreach (var z in new[] { box.Min.Z, box.Max.Z })
            {
                var p = source.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z)));
                min = new XYZ(Math.Min(min.X,p.X), Math.Min(min.Y,p.Y), Math.Min(min.Z,p.Z));
                max = new XYZ(Math.Max(max.X,p.X), Math.Max(max.Y,p.Y), Math.Max(max.Z,p.Z));
            }
            return new Bounds { Min = min, Max = max };
        }
    }
    public class ElementRecord
    {
        public ModelSource Source { get; set; }
        public Element Element { get; set; }
        public Bounds Bounds { get; set; }
        public string CacheKey { get { return Source.Key + "|" + Element.UniqueId; } }
    }
    public class ClashResult
    {
        public int Number { get; set; }
        public ElementRecord RecordA { get; set; }
        public ElementRecord RecordB { get; set; }
        public string SourceA { get; set; }
        public string SourceB { get; set; }
        public long IdA { get; set; }
        public long IdB { get; set; }
        public string UniqueIdA { get; set; }
        public string UniqueIdB { get; set; }
        public string CategoryA { get; set; }
        public string CategoryB { get; set; }
        public string TypeA { get; set; }
        public string TypeB { get; set; }
        public string SystemA { get; set; }
        public string SystemB { get; set; }
        public string ClassificationA { get; set; }
        public string ClassificationB { get; set; }
        public string ServiceA { get; set; }
        public string ServiceB { get; set; }
        public string Level { get; set; }
        public string Status { get; set; } = "New";
        public string ClashKey { get; set; }
        public double VolumeCm3 { get; set; }
        public XYZ Point { get; set; }
        public double Xmm { get { return Math.Round(Point.X * 304.8,1); } }
        public double Ymm { get { return Math.Round(Point.Y * 304.8,1); } }
        public double Zmm { get { return Math.Round(Point.Z * 304.8,1); } }
    }
    [DataContract]
    public class SavedChoice
    {
        [DataMember(Name="id")] public long Id { get; set; }
        [DataMember(Name="name")] public string Name { get; set; }
    }
    [DataContract]
    public class FilterRule
    {
        [DataMember(Name="parameter")] public string Parameter { get; set; }
        [DataMember(Name="operator")] public string Operator { get; set; } = "Equals";
        [DataMember(Name="value")] public string Value { get; set; }
    }
    [DataContract]
    public class ClashCriteria
    {
        [DataMember(Name="min_volume_cm3")] public double MinVolumeCm3 { get; set; } = 0.10;
        [DataMember(Name="min_intersection_box_mm")] public double MinIntersectionBoxMm { get; set; }
        [DataMember(Name="ignore_connected_mep")] public bool IgnoreConnectedMep { get; set; }
        [OnDeserializing] private void Defaults(StreamingContext context) { MinVolumeCm3=0.10; }
        public void Validate()
        {
            if (double.IsNaN(MinVolumeCm3) || double.IsInfinity(MinVolumeCm3) || MinVolumeCm3<0 ||
                double.IsNaN(MinIntersectionBoxMm) || double.IsInfinity(MinIntersectionBoxMm) || MinIntersectionBoxMm<0)
                throw new InvalidOperationException("Clash thresholds must be finite numbers greater than or equal to zero.");
        }
        public bool Accepts(double volumeCm3,double xMm,double yMm,double zMm)
        {
            return volumeCm3>MinVolumeCm3 && (MinIntersectionBoxMm<=0 || Math.Min(xMm,Math.Min(yMm,zMm))>=MinIntersectionBoxMm);
        }
    }
    [DataContract]
    public class ClashProfile
    {
        [DataMember(Name="name")] public string Name { get; set; }
        [DataMember(Name="source_a_key")] public string SourceAKey { get; set; }
        [DataMember(Name="source_b_key")] public string SourceBKey { get; set; }
        [DataMember(Name="source_a_label")] public string SourceALabel { get; set; }
        [DataMember(Name="source_b_label")] public string SourceBLabel { get; set; }
        [DataMember(Name="categories_a")] public List<SavedChoice> CategoriesA { get; set; }
        [DataMember(Name="categories_b")] public List<SavedChoice> CategoriesB { get; set; }
        [DataMember(Name="scope")] public string Scope { get; set; }
        [DataMember(Name="levels")] public List<SavedChoice> Levels { get; set; }
        [DataMember(Name="include_nonvisible")] public bool IncludeNonVisible { get; set; }
        [DataMember(Name="show_system_details")] public bool ShowSystemDetails { get; set; }
        [DataMember(Name="filters_a")] public List<FilterRule> FiltersA { get; set; }
        [DataMember(Name="filters_b")] public List<FilterRule> FiltersB { get; set; }
        [DataMember(Name="filter_a", EmitDefaultValue=false)] public FilterRule LegacyFilterA { get; set; }
        [DataMember(Name="filter_b", EmitDefaultValue=false)] public FilterRule LegacyFilterB { get; set; }
        [DataMember(Name="filter_mode_a")] public string FilterModeA { get; set; }
        [DataMember(Name="filter_mode_b")] public string FilterModeB { get; set; }
        [DataMember(Name="clash_criteria")] public ClashCriteria Criteria { get; set; } = new ClashCriteria();
        [OnDeserializing] private void Defaults(StreamingContext context) { ShowSystemDetails = true; Criteria=new ClashCriteria(); }
    }
    [DataContract]
    public class ProfilePayload
    {
        [DataMember(Name="schema")] public int Schema { get; set; } = 1;
        [DataMember(Name="profiles")] public List<ClashProfile> Profiles { get; set; }
    }
    public class CheckScope
    {
        public string Kind { get; set; }
        public ElementId ViewId { get; set; }
        public Bounds Bounds { get; set; }
        public HashSet<long> VisibleLinks { get; set; }
        public HashSet<long> LevelIds { get; set; }
        public List<Tuple<double,double?>> Bands { get; set; } = new List<Tuple<double,double?>>();
    }
}
