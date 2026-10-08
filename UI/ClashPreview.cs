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
                    var brush=new SolidColorBrush(side==0 ? Color.FromRgb(144,128,92) : Color.FromRgb(86,140,173)); brush.Freeze();
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
                    var ids=new[] {result.RecordA,result.RecordB}.Select(r => r.Source.Link==null ? r.Element.Id : r.Source.Link.Id).GroupBy(Ids.Value).Select(g => g.First()).ToList();
                    app.ActiveUIDocument.Selection.SetElementIds(ids); app.ActiveUIDocument.RefreshActiveView();
                    var uiView=app.ActiveUIDocument.GetOpenUIViews().FirstOrDefault(v => Ids.Value(v.ViewId)==Ids.Value(view.Id));
                    if (uiView!=null) { var margin=new XYZ(1000/304.8,1000/304.8,1000/304.8); uiView.ZoomAndCenterRectangle(result.Point-margin,result.Point+margin); }
                    var choices=((IEnumerable<Choice>)View3DCombo.ItemsSource).ToList();
                    var selected=choices.FirstOrDefault(c => c.Id==Ids.Value(view.Id));
                    if (selected==null) { selected=new Choice {Id=Ids.Value(view.Id),Name=view.Name}; choices.Insert(0,selected); View3DCombo.ItemsSource=choices; }
                    View3DCombo.SelectedItem=selected; Summary.Text="Opened "+view.Name+" at clash #"+result.Number+" (2,000 mm section box).";
                },"Opening the selected 3D view at the clash…");
            });
        }
    }
}
