using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace MEP_Check_Clash_ver1.UI
{
    public partial class ClashWindow
    {
        private Point? orbitStart;
        private Point3D previewCenter;
        private double previewDistance=10, yaw=-0.75, pitch=0.55;
        private bool previewPending;
        private bool updatingClashColors;
        private sealed class SavedColorOverride
        {
            public string ViewId, ElementId;
            public OverrideGraphicSettings Original;
        }
        private readonly Dictionary<string,SavedColorOverride> savedColors=new Dictionary<string,SavedColorOverride>();
        private static Color ReadClashColor(string text)
        {
            text=(text ?? "").Trim();
            if (text.Length==9 && text.StartsWith("#FF",StringComparison.OrdinalIgnoreCase)) text="#"+text.Substring(3);
            if (text.Length!=7 || text[0]!='#' || !text.Substring(1).All(Uri.IsHexDigit))
                throw new InvalidOperationException("Enter an opaque color as #RRGGBB, for example #32CD32 or #FF3333.");
            return (Color)ColorConverter.ConvertFromString(text);
        }
        private void Colors_Changed(object sender,RoutedEventArgs e)
        {
            if (initializing || updatingClashColors || closed || IsBusy || ColorAInput==null || ColorBInput==null) return;
            UserAction(() =>
            {
                if (sender is System.Windows.Controls.ComboBox combo && combo.SelectedItem is System.Windows.Controls.ComboBoxItem item) combo.Text=item.Content.ToString();
                var a=ReadClashColor(ColorAInput.Text); var b=ReadClashColor(ColorBInput.Text);
                ColorLegend.Text="A · "+a+"     B · "+b+"  |  Drag to orbit · scroll to zoom";
                if (ResultsGrid.SelectedItem!=null) Result_SelectionChanged(null,null);
            });
        }
        private void ColorSelected_Click(object sender,RoutedEventArgs e) { UserAction(() => ColorResults(new[] {SelectedResult()})); }
        private void ColorAll_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                if (results.Count==0) throw new InvalidOperationException("Run a clash check first.");
                ColorResults(results.ToArray());
            });
        }
        private void ColorResults(IEnumerable<ClashResult> clashes)
        {
            var snapshot=clashes.ToArray(); ReadClashColor(ColorAInput.Text); ReadClashColor(ColorBInput.Text);
            Queue(app => { Summary.Text=ApplyClashColors(Document.ActiveView,snapshot); app.ActiveUIDocument.RefreshActiveView(); },"Applying clash colors in the active view…");
        }
        private string ApplyClashColors(Autodesk.Revit.DB.View view,IEnumerable<ClashResult> clashes)
        {
            if (view.IsTemplate || !view.AreGraphicsOverridesAllowed()) throw new InvalidOperationException("This view does not support element color overrides.");
            var a=ReadClashColor(ColorAInput.Text); var b=ReadClashColor(ColorBInput.Text);
            var snapshot=clashes.ToArray(); foreach (var clash in snapshot) ValidateResult(clash);
            var targets=new Dictionary<string,Tuple<Element,Color>>(); var linked=new HashSet<string>(); var conflicts=new HashSet<string>(); var idsA=new HashSet<string>();
            // A first, B second: B wins for elements appearing in both sets.
            foreach (var side in new[] {0,1}) foreach (var clash in snapshot)
            {
                var record=side==0 ? clash.RecordA : clash.RecordB;
                if (record.Source.Link!=null) { linked.Add(record.CacheKey); continue; }
                if (side==0) idsA.Add(record.Element.UniqueId);
                else if (idsA.Contains(record.Element.UniqueId)) conflicts.Add(record.Element.UniqueId);
                targets[record.Element.UniqueId]=Tuple.Create(record.Element,side==0 ? a : b);
            }
            if (targets.Count==0) return "No current-model elements to color. Linked elements are colored in the 3D preview.";
            var solid=new FilteredElementCollector(Document).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
            if (solid==null) throw new InvalidOperationException("No solid fill pattern found in this model.");
            var backups=new Dictionary<string,SavedColorOverride>();
            try
            {
                using (var transaction=new Transaction(Document,"Clash Solution - Colors"))
                {
                    transaction.Start();
                    foreach (var target in targets.Values)
                    {
                        var key=view.UniqueId+"|"+target.Item1.UniqueId;
                        using (var current=view.GetElementOverrides(target.Item1.Id))
                        {
                            if (!savedColors.ContainsKey(key)) backups[key]=new SavedColorOverride {ViewId=view.UniqueId,ElementId=target.Item1.UniqueId,Original=new OverrideGraphicSettings(current)};
                            var color=new Autodesk.Revit.DB.Color(target.Item2.R,target.Item2.G,target.Item2.B);
                            current.SetProjectionLineColor(color); current.SetCutLineColor(color);
                            current.SetSurfaceForegroundPatternId(solid.Id); current.SetSurfaceForegroundPatternColor(color); current.SetSurfaceForegroundPatternVisible(true);
                            current.SetCutForegroundPatternId(solid.Id); current.SetCutForegroundPatternColor(color); current.SetCutForegroundPatternVisible(true);
                            current.SetSurfaceTransparency(0); current.SetHalftone(false);
                            view.SetElementOverrides(target.Item1.Id,current);
                        }
                    }
                    if (transaction.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("Revit could not apply clash colors.");
                }
                foreach (var entry in backups) savedColors.Add(entry.Key,entry.Value);
            }
            catch { foreach (var backup in backups.Values) backup.Original.Dispose(); throw; }
            return "Colored "+targets.Count+" elements in "+view.Name+"."+(linked.Count>0 ? " "+linked.Count+" linked elements: preview only." : "")+(conflicts.Count>0 ? " Elements in both sets use Set B color." : "");
        }
        private void RestoreColors_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() => Queue(app =>
            {
                var view=Document.ActiveView;
                var entries=savedColors.Where(p => p.Value.ViewId==view.UniqueId).ToArray();
                if (entries.Length==0) { Summary.Text="No saved clash colors to restore in this view during this tool session."; return; }
                using (var transaction=new Transaction(Document,"Clash Solution - Restore Colors"))
                {
                    transaction.Start();
                    foreach (var entry in entries)
                    {
                        var element=Document.GetElement(entry.Value.ElementId);
                        if (element!=null) view.SetElementOverrides(element.Id,entry.Value.Original);
                    }
                    if (transaction.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("Revit could not restore colors.");
                }
                foreach (var entry in entries) { savedColors.Remove(entry.Key); entry.Value.Original.Dispose(); }
                app.ActiveUIDocument.RefreshActiveView(); Summary.Text="Restored original element overrides in "+view.Name+".";
            },"Restoring the active view's original colors…"));
        }
        private void ClearPreview()
        {
            ClashViewport.Children.Clear(); ClashViewport.Camera=null;
            PreviewHint.Text="Select a clash to preview its two elements";
            PreviewHint.Visibility=System.Windows.Visibility.Visible;
        }
        private void Result_SelectionChanged(object sender,SelectionChangedEventArgs e)
        {
            if (initializing || closed) return;
            if (ResultsGrid.SelectedItem==null) { ClearPreview(); return; }
            if (IsBusy) { previewPending=true; return; }
            var result=ResultsGrid.SelectedItem as ClashResult;
            if (result==null) return;
            Queue(app =>
            {
                ValidateResult(result); BuildPreview(result);
                Summary.Text="Preview clash #"+result.Number+" · choose a 3D view and click View clash to navigate in Revit.";
            },"Loading clash geometry for 3D preview…");
        }
        private void BuildPreview(ClashResult result)
        {
            ColorLegend.Text="A · "+ReadClashColor(ColorAInput.Text)+"     B · "+ReadClashColor(ColorBInput.Text)+"  |  Drag to orbit · scroll to zoom";
            var scene=new Model3DGroup();
            scene.Children.Add(new AmbientLight(Color.FromRgb(150,150,150)));
            scene.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-1,-1,-2)));
            var bounds=Rect3D.Empty; var warnings=0; var triangles=0; const int limit=60000;
            using (var options=new Options { DetailLevel=ViewDetailLevel.Fine,IncludeNonVisibleObjects=NonVisible.IsChecked==true })
            {
                var records=new[] {result.RecordA,result.RecordB};
                for (var side=0; side<records.Length; side++)
                {
                    var sideStart=triangles;
                    var mesh=new MeshGeometry3D();
                    var solids=ClashEngine.Solids(records[side],options,ref warnings);
                    try
                    {
                        foreach (var solid in solids)
                        foreach (Face face in solid.Faces)
                        {
                            if (triangles-sideStart>=limit/2) break;
                            using (var tessellation=face.Triangulate())
                            for (var i=0; i<tessellation.NumTriangles && triangles-sideStart<limit/2; i++)
                            {
                                var triangle=tessellation.get_Triangle(i);
                                for (var k=0; k<3; k++)
                                {
                                    var p=triangle.get_Vertex(k)-result.Point;
                                    var vertex=new Point3D(p.X,p.Y,p.Z);
                                    bounds.Union(vertex);
                                    mesh.TriangleIndices.Add(mesh.Positions.Count); mesh.Positions.Add(vertex);
                                }
                                triangles++;
                            }
                        }
                    }
                    finally { foreach (var solid in solids) solid.Dispose(); }
                    mesh.Freeze();
                    var brush=new SolidColorBrush(ReadClashColor(side==0 ? ColorAInput.Text : ColorBInput.Text)); brush.Freeze();
                    var material=new DiffuseMaterial(brush); material.Freeze();
                    scene.Children.Add(new GeometryModel3D(mesh,material) { BackMaterial=material });
                }
            }
            if (bounds.IsEmpty) { ClearPreview(); PreviewHint.Text="No solid geometry available for preview"; return; }
            previewCenter=new Point3D(bounds.X+bounds.SizeX/2,bounds.Y+bounds.SizeY/2,bounds.Z+bounds.SizeZ/2);
            previewDistance=Math.Max(1,Math.Sqrt(bounds.SizeX*bounds.SizeX+bounds.SizeY*bounds.SizeY+bounds.SizeZ*bounds.SizeZ)*1.5);
            scene.Freeze(); ClashViewport.Children.Clear(); ClashViewport.Children.Add(new ModelVisual3D { Content=scene });
            PreviewHint.Visibility=System.Windows.Visibility.Collapsed; UpdateCamera();
            if (triangles>=limit) Summary.Text="Preview simplified to 60,000 triangles.";
        }
        private void UpdateCamera()
        {
            var direction=new Vector3D(Math.Cos(pitch)*Math.Cos(yaw),Math.Cos(pitch)*Math.Sin(yaw),Math.Sin(pitch));
            ClashViewport.Camera=new PerspectiveCamera(previewCenter+direction*previewDistance,-direction,new Vector3D(0,0,1),45)
            { NearPlaneDistance=Math.Max(0.001,previewDistance/10000),FarPlaneDistance=previewDistance*20 };
        }
        private void Preview_MouseDown(object sender,MouseButtonEventArgs e) { if (ClashViewport.Camera==null) return; orbitStart=e.GetPosition(ClashViewport); ClashViewport.CaptureMouse(); }
        private void Preview_MouseMove(object sender,MouseEventArgs e)
        {
            if (!orbitStart.HasValue || e.LeftButton!=MouseButtonState.Pressed) return;
            var position=e.GetPosition(ClashViewport); yaw-=(position.X-orbitStart.Value.X)*0.01;
            pitch=Math.Max(-1.45,Math.Min(1.45,pitch+(position.Y-orbitStart.Value.Y)*0.01)); orbitStart=position; UpdateCamera();
        }
        private void Preview_MouseUp(object sender,MouseButtonEventArgs e) { orbitStart=null; ClashViewport.ReleaseMouseCapture(); }
        private void Preview_MouseWheel(object sender,MouseWheelEventArgs e)
        {
            if (ClashViewport.Camera==null) return;
            previewDistance=Math.Max(0.01,Math.Min(1e6,previewDistance*Math.Exp(-e.Delta/120.0*0.15))); UpdateCamera(); e.Handled=true;
        }
        private void ViewClash_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                var result=SelectedResult(); var choice=View3DCombo.SelectedItem as Choice;
                if (ColorOnViewClash.IsChecked==true) { ReadClashColor(ColorAInput.Text); ReadClashColor(ColorBInput.Text); }
                if (choice==null) throw new InvalidOperationException("Choose a 3D view first.");
                Queue(app =>
                {
                    ValidateResult(result);
                    var view=choice.Id<0 ? null : Document.GetElement(Ids.Create(choice.Id)) as View3D;
                    using (var transaction=new Transaction(Document,"Clash Solution - View Clash"))
                    {
                        transaction.Start();
                        if (view==null)
                        {
                            var type=new FilteredElementCollector(Document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily==ViewFamily.ThreeDimensional);
                            view=View3D.CreateIsometric(Document,type.Id);
                            view.Name="Clash Solution "+Ids.Value(view.Id);
                        }
                        if (view.IsTemplate || view.IsPerspective) throw new InvalidOperationException("Choose a non-template orthographic 3D view.");
                        var margin=new XYZ(1000/304.8,1000/304.8,1000/304.8);
                        view.IsSectionBoxActive=true; view.SetSectionBox(new BoundingBoxXYZ { Min=result.Point-margin,Max=result.Point+margin });
                        if (transaction.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("Revit could not apply the clash view.");
                    }
                    app.ActiveUIDocument.ActiveView=view;
                    var colorSummary=ColorOnViewClash.IsChecked==true ? ApplyClashColors(view,new[] {result}) : "";
                    var ids=new[] {result.RecordA,result.RecordB}.Select(r => r.Source.Link==null ? r.Element.Id : r.Source.Link.Id).GroupBy(Ids.Value).Select(g => g.First()).ToList();
                    app.ActiveUIDocument.Selection.SetElementIds(ids); app.ActiveUIDocument.RefreshActiveView();
                    var uiView=app.ActiveUIDocument.GetOpenUIViews().FirstOrDefault(v => Ids.Value(v.ViewId)==Ids.Value(view.Id));
                    if (uiView!=null) { var margin=new XYZ(1000/304.8,1000/304.8,1000/304.8); uiView.ZoomAndCenterRectangle(result.Point-margin,result.Point+margin); }
                    var choices=((IEnumerable<Choice>)View3DCombo.ItemsSource).ToList();
                    var selected=choices.FirstOrDefault(c => c.Id==Ids.Value(view.Id));
                    if (selected==null) { selected=new Choice {Id=Ids.Value(view.Id),Name=view.Name}; choices.Insert(0,selected); View3DCombo.ItemsSource=choices; }
                    View3DCombo.SelectedItem=selected; Summary.Text="Opened "+view.Name+" at clash #"+result.Number+" (2,000 mm section box). "+colorSummary;
                },"Opening the selected 3D view at the clash…");
            });
        }
    }
}
