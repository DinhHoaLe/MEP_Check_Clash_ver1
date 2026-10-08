param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\artifacts'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml
$path=Join-Path $PSScriptRoot '..\UI\ClashWindow.xaml'
[xml]$xml=Get-Content -LiteralPath $path -Raw -Encoding UTF8
$events=@('Click','Checked','Unchecked','Closing','StateChanged','SelectionChanged','TextChanged','MouseLeftButtonDown','MouseLeftButtonUp','MouseMove','MouseWheel')
foreach ($node in $xml.SelectNodes('//*')) {
    foreach ($attribute in @($node.Attributes)) {
        if (($attribute.LocalName -eq 'Class' -and $attribute.NamespaceURI -eq 'http://schemas.microsoft.com/winfx/2006/xaml') -or $events -contains $attribute.Name) { $node.RemoveAttributeNode($attribute) | Out-Null }
    }
}
$window=[System.Windows.Markup.XamlReader]::Parse($xml.OuterXml)
# Render the real XAML offscreen without launching Revit or displaying a window.
$root=$window.Content
$source=@([pscustomobject]@{Label='Current model'},[pscustomobject]@{Label='Structure.rvt [ID 48291]'})
foreach ($name in @('SourceA','SourceB')) { $combo=$window.FindName($name); $combo.ItemsSource=$source; $combo.SelectedIndex=0 }
$window.FindName('SourceB').SelectedIndex=1
$window.FindName('CategoriesA').ItemsSource=@('Cable Trays','Conduits','Duct Fittings','Ducts','Mechanical Equipment','Pipe Fittings','Pipes' | ForEach-Object { [pscustomobject]@{Name=$_;IsChecked=$true} })
$window.FindName('CategoriesB').ItemsSource=@('Ceilings','Floors','Structural Columns','Structural Framing','Walls' | ForEach-Object { [pscustomobject]@{Name=$_;IsChecked=$true} })
$window.FindName('SearchA').Text='Search categories'
$window.FindName('SearchB').Text='Search categories'
$window.FindName('ProfileName').Text='MEP vs Structure'
$window.FindName('ViewLevelHint').Text='Active view: Level 02 - Coordination → Level 02'
$window.FindName('LevelsDropdown').ItemsSource=@('Active View Level','Level 01','Level 02','Level 03' | ForEach-Object { [pscustomobject]@{Name=$_;IsChecked=$false} })
$window.FindName('LevelsDropdown').Text='Select levels'
$window.FindName('VersionText').Text='v1.2.0.0'
$window.FindName('ToolLogo').Source=[System.Windows.Media.Imaging.BitmapImage]::new([Uri]::new((Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\Assets\ClashLogo.png')).Path))
$window.FindName('View3DCombo').ItemsSource=@([pscustomobject]@{Name='{3D} - Coordination'})
$window.FindName('View3DCombo').SelectedIndex=0
$window.FindName('Summary').Text='Completed · Entire Model · 12 clashes · A 245 / B 180 elements · 86 candidates · 0 geometry warnings'
$window.FindName('ResultsCount').Text='12 intersections'
$window.FindName('ResultsGrid').ItemsSource=@(1..12 | ForEach-Object { [pscustomobject]@{Number=$_;Level='Level 02';SourceA='Current model';IdA=120340+$_;CategoryA='Pipes';TypeA='CHW - DN100';SystemA='Hydronic Supply';ClassificationA='Hydronic Supply';ServiceA='CHW';SourceB='Structure.rvt';IdB=230100+$_;CategoryB='Structural Framing';TypeB='Concrete Beam';Status='New'} })
# Synthetic geometry is only for visual QA of the WPF viewport, not Revit data.
$scene=[System.Windows.Media.Media3D.Model3DGroup]::new()
$scene.Children.Add([System.Windows.Media.Media3D.AmbientLight]::new([System.Windows.Media.Color]::FromRgb(150,150,150)))
$scene.Children.Add([System.Windows.Media.Media3D.DirectionalLight]::new([System.Windows.Media.Colors]::White,[System.Windows.Media.Media3D.Vector3D]::new(-1,-1,-2)))
foreach ($box in @(@(-3,-0.25,-0.25,3,0.25,0.25,'#90805C'),@(-0.4,-2,-0.5,0.4,2,0.5,'#568CAD'))) {
    $mesh=[System.Windows.Media.Media3D.MeshGeometry3D]::new()
    foreach ($point in @(@($box[0],$box[1],$box[2]),@($box[3],$box[1],$box[2]),@($box[3],$box[4],$box[2]),@($box[0],$box[4],$box[2]),@($box[0],$box[1],$box[5]),@($box[3],$box[1],$box[5]),@($box[3],$box[4],$box[5]),@($box[0],$box[4],$box[5]))) { $mesh.Positions.Add([System.Windows.Media.Media3D.Point3D]::new($point[0],$point[1],$point[2])) }
    foreach ($index in @(0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7)) { $mesh.TriangleIndices.Add($index) }
    $brush=[System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString($box[6]))
    $material=[System.Windows.Media.Media3D.DiffuseMaterial]::new($brush)
    $model=[System.Windows.Media.Media3D.GeometryModel3D]::new($mesh,$material); $model.BackMaterial=$material; $scene.Children.Add($model)
}
$viewport=$window.FindName('ClashViewport')
$viewport.Children.Add([System.Windows.Media.Media3D.ModelVisual3D]@{Content=$scene})
$viewport.Camera=[System.Windows.Media.Media3D.PerspectiveCamera]::new([System.Windows.Media.Media3D.Point3D]::new(8,-10,7),[System.Windows.Media.Media3D.Vector3D]::new(-8,10,-7),[System.Windows.Media.Media3D.Vector3D]::new(0,0,1),45)
$window.FindName('PreviewHint').Visibility=[System.Windows.Visibility]::Collapsed
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($width in @(1700,1360)) {
    $root.Measure([System.Windows.Size]::new($width,808))
    $root.Arrange([System.Windows.Rect]::new(0,0,$width,808))
    $root.UpdateLayout()
    $bitmap=[System.Windows.Media.Imaging.RenderTargetBitmap]::new($width,808,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($root)
    $encoder=[System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $target=Join-Path $OutputDirectory "ClashWindow-$width.png"
    $stream=[IO.File]::Create($target)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    Write-Output $target
}
$body=$root.Children | Where-Object { $_ -is [System.Windows.Controls.Grid] -and [System.Windows.Controls.Grid]::GetRow($_) -eq 1 } | Select-Object -First 1
$scroll=$body.Children | Where-Object { $_ -is [System.Windows.Controls.ScrollViewer] } | Select-Object -First 1
$scroll.ScrollToEnd()
$root.UpdateLayout()
$bitmap=[System.Windows.Media.Imaging.RenderTargetBitmap]::new(1360,808,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($root)
$encoder=[System.Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$target=Join-Path $OutputDirectory 'ClashWindow-results.png'
$stream=[IO.File]::Create($target)
try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Output $target
$window.Close()
