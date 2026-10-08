using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Microsoft.Win32;
using RevitView = Autodesk.Revit.DB.View;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Grid = System.Windows.Controls.Grid;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace MEP_Check_Clash_ver1.UI
{
    public partial class ClashWindow : Window
    {
        public Document Document { get; }
        public bool IsBusy { get; private set; }
        private readonly UIApplication application;
        private readonly RevitRequest request;
        private List<ModelSource> sources;
        private readonly Dictionary<string,List<Choice>> categoryCache = new Dictionary<string,List<Choice>>();
        private readonly Dictionary<string,Dictionary<string,List<string>>> metadataCache = new Dictionary<string,Dictionary<string,List<string>>>();
        private List<Choice> categoriesA = new List<Choice>();
        private List<Choice> categoriesB = new List<Choice>();
        private List<Choice> levels = new List<Choice>();
        private List<ClashProfile> profiles = new List<ClashProfile>();
        private readonly List<RuleEditor> rowsA = new List<RuleEditor>();
        private readonly List<RuleEditor> rowsB = new List<RuleEditor>();
        private List<ClashResult> results = new List<ClashResult>();
        private ClashSession session;
        private EventHandler<IdlingEventArgs> resume;
        private bool initializing = true;
        private bool closed;
        private bool closeAfterCancel;
        private bool profilesReadable = true;
        private string cancellationReason;
        private string scopeLabel;
        private bool updatingLevels;

        public ClashWindow(UIApplication app)
        {
            application=app; Document=app.ActiveUIDocument.Document;
            InitializeComponent();
            var version=typeof(ClashWindow).Assembly.GetName().Version;
            Title="Clash Solution · v"+version;
            VersionText.Text="v"+version;
            Icon=App.Logo(32); ToolLogo.Source=App.Logo(48);
            request=new RevitRequest(Failed);
            ReloadSources();
            try { profiles=ProfileStore.Load(); }
            catch (Exception ex) { profilesReadable=false; Summary.Text="Cannot read profiles: "+ex.Message+". Existing file will be preserved."; }
            RefreshProfileList();
            initializing=false;
            RefreshSide("A"); RefreshSide("B");
            ApplyPreset("A"); ApplyPreset("B");
            UpdateViewHint(Document.ActiveView);
            Details_Changed(null,null);
            application.Application.DocumentChanged += ModelChanged;
            Dispatcher.UnhandledException += OwnUiException;
            AppDomain.CurrentDomain.UnhandledException += ObserveFatalException;
            Closed += (s,e) =>
            {
                closed=true; RemoveResume(); application.Application.DocumentChanged -= ModelChanged;
                Dispatcher.UnhandledException -= OwnUiException;
                AppDomain.CurrentDomain.UnhandledException -= ObserveFatalException;
                try { request.Dispose(); } catch (Exception ex) { Diagnostics.Write("ExternalEvent cleanup failed",ex); }
            };
            Diagnostics.Write("Window opened; Revit "+app.Application.VersionNumber);
        }
        private void ReloadSources()
        {
            sources=ClashEngine.Sources(Document);
            categoryCache.Clear(); metadataCache.Clear();
            foreach (var source in sources) categoryCache[source.Key]=ClashEngine.Categories(source);
            levels=new FilteredElementCollector(Document).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation)
                .Select(l => new Choice { Id=Ids.Value(l.Id),Name=l.Name }).ToList();
            var levelOptions=new List<Choice> { new Choice { Id=-1,Name="Active View Level" } };
            levelOptions.AddRange(levels); LevelsDropdown.ItemsSource=levelOptions; UpdateLevelText();
            var viewChoices=new FilteredElementCollector(Document).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate && !v.IsPerspective).OrderBy(v => v.Name)
                .Select(v => new Choice { Id=Ids.Value(v.Id),Name=v.Name }).ToList();
            viewChoices.Add(new Choice { Id=-1,Name="Create Clash Solution 3D view" });
            View3DCombo.ItemsSource=viewChoices;
            View3DCombo.SelectedItem=viewChoices.FirstOrDefault(v => v.Id==Ids.Value(Document.ActiveView.Id)) ?? viewChoices.First();
            SourceA.ItemsSource=sources; SourceB.ItemsSource=sources;
            SourceA.SelectedIndex=0; SourceB.SelectedIndex=0;
        }
        private static string Side(object sender) { return (string)((FrameworkElement)sender).Tag; }
        private ModelSource Source(string side) { return (ModelSource)(side=="A" ? SourceA : SourceB).SelectedItem; }
        private List<Choice> Categories(string side) { return side=="A" ? categoriesA : categoriesB; }
        private List<RuleEditor> Rows(string side) { return side=="A" ? rowsA : rowsB; }
        private StackPanel RulePanel(string side) { return side=="A" ? RulesA : RulesB; }
        private ComboBox Mode(string side) { return side=="A" ? ModeA : ModeB; }
        private List<long> SelectedIds(string side) { return Categories(side).Where(c => c.IsChecked).Select(c => c.Id).ToList(); }
        private bool UseActiveLevel { get { return LevelsDropdown.ItemsSource?.Cast<Choice>().FirstOrDefault(c => c.Id==-1)?.IsChecked==true; } }
        private string ScopeKind { get { return ViewScope.IsChecked==true ? "active_view" : LevelScope.IsChecked==true ? (UseActiveLevel ? "active_view_level" : "levels") : "entire"; } }
        private void SetBusy(bool busy)
        {
            IsBusy=busy;
            SetupPanel.IsEnabled=!busy; RunButton.IsEnabled=!busy; ResultActions.IsEnabled=!busy; RefreshButton.IsEnabled=!busy;
            ViewActions.IsEnabled=!busy;
            CancelButton.Visibility=session!=null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (!busy && previewPending)
            {
                previewPending=false;
                Dispatcher.BeginInvoke(new Action(() => { if (!closed) Result_SelectionChanged(null,null); }));
            }
        }
        private void Guard(UIApplication app)
        {
            if (!DocumentIdentity.Same(Document, app.ActiveUIDocument?.Document))
                throw new InvalidOperationException("Activate the model that opened this window, or reopen Clash Detector in the new model.");
        }
        private void Queue(Action<UIApplication> action,string message)
        {
            if (closed || IsBusy) return;
            Summary.Text=message; SetBusy(true);
            try
            {
                request.Post(app =>
                {
                    Guard(app); action(app);
                    if (session==null) { SetBusy(false); if (closeAfterCancel) Dispatcher.BeginInvoke(new Action(Close)); }
                });
            }
            catch (Exception ex) { Failed(ex); }
        }
        private void Failed(Exception ex)
        {
            Diagnostics.Write("Revit action failed",ex);
            RemoveResume();
            if (session!=null)
            {
                var failedSession=session; session=null;
                try { failedSession.Dispose(); } catch (Exception cleanup) { Diagnostics.Write("Geometry cleanup failed",cleanup); }
            }
            SetBusy(false); Progress.Visibility=System.Windows.Visibility.Collapsed;
            Summary.Text="Action failed: "+ex.Message;
            if (closeAfterCancel) Dispatcher.BeginInvoke(new Action(Close));
        }
        private void OwnUiException(object sender,DispatcherUnhandledExceptionEventArgs e)
        {
            // Recover only exceptions whose stack belongs to this window.
            // Do not suppress faults from Revit, other add-ins, or native memory.
            if (!Diagnostics.IsOwnUiException(e.Exception)) return;
            Diagnostics.Write("Window callback failed",e.Exception);
            if (session!=null) session.CancelRequested=true;
            Summary.Text="Window action failed: "+e.Exception.Message;
            e.Handled=true;
        }
        private void ObserveFatalException(object sender,UnhandledExceptionEventArgs e)
        {
            // Observation only: termination remains under the CLR/Revit's control.
            Diagnostics.Write("Unhandled .NET exception; terminating="+e.IsTerminating,e.ExceptionObject as Exception);
        }
        private void UserAction(Action action)
        {
            try { action(); } catch (Exception ex) { Diagnostics.Write("User action failed",ex); MessageBox.Show(this,ex.Message,"Clash Solution",MessageBoxButton.OK,MessageBoxImage.Information); }
        }
        private void RefreshSide(string side)
        {
            var source=Source(side);
            var items=source==null ? new List<Choice>() : categoryCache[source.Key].Select(c => new Choice { Id=c.Id,Name=c.Name }).ToList();
            if (side=="A") categoriesA=items; else categoriesB=items;
            SearchSide(side);
            foreach (var row in Rows(side)) row.Metadata(new Dictionary<string,List<string>>());
        }
        private void SearchSide(string side)
        {
            var search=(side=="A" ? SearchA : SearchB).Text.Trim();
            (side=="A" ? CategoriesA : CategoriesB).ItemsSource=Categories(side).Where(c => c.Name.IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0).ToList();
        }
        private void ApplyPreset(string side)
        {
            var ids=ClashEngine.CategoryIds(side=="A" ? ClashEngine.MepCategories : ClashEngine.ArchitectureCategories);
            foreach (var item in Categories(side)) item.IsChecked=ids.Contains(item.Id);
        }
        private void Source_Changed(object sender,SelectionChangedEventArgs e) { if (!initializing) RefreshSide(Side(sender)); }
        private void Search_Changed(object sender,TextChangedEventArgs e) { if (!initializing) SearchSide(Side(sender)); }
        private void Preset_Click(object sender,RoutedEventArgs e) { ApplyPreset(Side(sender)); }
        private void ClearCategories_Click(object sender,RoutedEventArgs e) { foreach (var item in Categories(Side(sender))) item.IsChecked=false; }
        private void Scope_Changed(object sender,RoutedEventArgs e) { if (LevelsDropdown!=null) LevelsDropdown.IsEnabled=LevelScope.IsChecked==true; }
        private void Details_Changed(object sender,RoutedEventArgs e)
        {
            // The compact result grid always shows No., ID and Category.
            // System details remain available in the export option.
        }
        private void UpdateViewHint(RevitView view)
        {
            var level=view.GenLevel;
            ViewLevelHint.Text="Active view: "+view.Name+(level==null ? " · no associated level" : " → "+level.Name);
        }
        private void UpdateLevelText()
        {
            if (LevelsDropdown==null) return;
            var selected=levels.Where(l => l.IsChecked).Select(l => l.Name).ToList();
            LevelsDropdown.Text=UseActiveLevel ? "Active View Level" : selected.Count==0 ? "Select levels" : string.Join(", ",selected);
        }
        private void LevelDropdown_SelectionChanged(object sender,SelectionChangedEventArgs e) { if (!initializing) UpdateLevelText(); }
        private void LevelChoice_Click(object sender,RoutedEventArgs e)
        {
            if (updatingLevels) return;
            var choice=(Choice)((CheckBox)sender).Tag;
            updatingLevels=true;
            try
            {
                if (choice.IsChecked)
                    foreach (var item in LevelsDropdown.ItemsSource.Cast<Choice>())
                        if ((choice.Id==-1 && item.Id!=-1) || (choice.Id!=-1 && item.Id==-1)) item.IsChecked=false;
                UpdateLevelText();
            }
            finally { updatingLevels=false; }
        }
        private string MetadataKey(string side) { return Source(side).Key+"|"+string.Join(",",SelectedIds(side).OrderBy(x => x)); }
        private void LoadMetadata(string side,bool force,Action after)
        {
            UserAction(() =>
            {
                if (Source(side)==null || SelectedIds(side).Count==0) throw new InvalidOperationException("Select a source and at least one category in Set "+side+" first.");
                var key=MetadataKey(side);
                Action<Dictionary<string,List<string>>> apply=metadata => { foreach (var row in Rows(side)) row.Metadata(metadata); after?.Invoke(); Summary.Text="Set "+side+": parameter options loaded (up to 5,000 elements / 500 values per parameter)."; };
                Dictionary<string,List<string>> cached;
                if (!force && metadataCache.TryGetValue(key,out cached)) { apply(cached); return; }
                var source=Source(side); var ids=SelectedIds(side);
                Queue(app => { ClashEngine.ValidateSource(source,Document); var metadata=ClashEngine.ParameterMetadata(source,ids); metadataCache[key]=metadata; apply(metadata); },"Loading Set "+side+" parameters…");
            });
        }
        private void AddRule(string side,FilterRule spec=null)
        {
            var row=new RuleEditor(this,side,spec);
            Rows(side).Add(row); RulePanel(side).Children.Add(row.Panel);
            Dictionary<string,List<string>> metadata;
            if (Source(side)!=null && metadataCache.TryGetValue(MetadataKey(side),out metadata)) row.Metadata(metadata);
        }
        private void ClearRules(string side) { Rows(side).Clear(); RulePanel(side).Children.Clear(); }
        private void AddRule_Click(object sender,RoutedEventArgs e) { var side=Side(sender); LoadMetadata(side,false,() => AddRule(side)); }
        private void RefreshRules_Click(object sender,RoutedEventArgs e) { LoadMetadata(Side(sender),true,null); }
        private void ClearRules_Click(object sender,RoutedEventArgs e) { ClearRules(Side(sender)); }
        private List<FilterRule> FilterSpecs(string side)
        {
            var specs=Rows(side).Select(row => row.Spec()).ToList();
            foreach (var rule in specs) if (string.IsNullOrWhiteSpace(rule.Parameter)!=string.IsNullOrWhiteSpace(rule.Value)) throw new InvalidOperationException("Each Set "+side+" rule needs both a parameter and a value.");
            return specs.Where(r => !string.IsNullOrWhiteSpace(r.Parameter)).ToList();
        }
        private void ValidateSetup()
        {
            if (Source("A")==null || Source("B")==null) throw new InvalidOperationException("Select both model sources.");
            if (SelectedIds("A").Count==0 || SelectedIds("B").Count==0) throw new InvalidOperationException("Select at least one category in both sets.");
            if (ScopeKind=="levels" && !levels.Any(l => l.IsChecked)) throw new InvalidOperationException("Select at least one host-model level.");
            FilterSpecs("A"); FilterSpecs("B");
            ReadCriteria();
        }
        private ClashCriteria ReadCriteria()
        {
            Func<string,string,double> parse=(text,label) =>
            {
                double number;
                if (!double.TryParse(text,NumberStyles.Float,CultureInfo.CurrentCulture,out number) && !double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out number))
                    throw new InvalidOperationException("Enter a number for "+label+".");
                return number;
            };
            var criteria=new ClashCriteria { MinVolumeCm3=parse(MinVolumeInput.Text,"minimum overlap volume"),MinIntersectionBoxMm=parse(MinBoxInput.Text,"minimum intersection box size"),IgnoreConnectedMep=IgnoreConnected.IsChecked==true };
            criteria.Validate(); return criteria;
        }
        private void Run_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                ValidateSetup();
                var a=Source("A"); var b=Source("B"); var idsA=SelectedIds("A"); var idsB=SelectedIds("B");
                var kind=ScopeKind; var selectedLevels=levels.Where(l => l.IsChecked).Select(l => l.Id).ToList();
                var filtersA=FilterSpecs("A"); var filtersB=FilterSpecs("B"); var anyA=ModeA.SelectedIndex==1; var anyB=ModeB.SelectedIndex==1; var nonVisible=NonVisible.IsChecked==true;
                var criteria=ReadCriteria();
                Queue(app =>
                {
                    Diagnostics.Write("Run starting; scope="+kind+"; category counts="+idsA.Count+"/"+idsB.Count);
                    if (kind=="active_view_level")
                    {
                        var activeLevel=Document.ActiveView.GenLevel;
                        UpdateViewHint(Document.ActiveView);
                        if (activeLevel==null) throw new InvalidOperationException("Active View Level requires a plan view with an associated level.");
                        selectedLevels=new List<long> { Ids.Value(activeLevel.Id) }; kind="levels";
                    }
                    ClashEngine.ValidateSource(a,Document); ClashEngine.ValidateSource(b,Document);
                    scopeLabel=kind=="active_view" ? "Active View: "+Document.ActiveView.Name : kind=="levels" ? selectedLevels.Count+" levels" : "Entire Model";
                    session=new ClashSession(a,idsA,b,idsB,ClashEngine.Scope(Document,kind,selectedLevels),filtersA,filtersB,anyA,anyB,nonVisible,criteria);
                    Diagnostics.Write("Candidates collected; elements="+session.ElementsA+"/"+session.ElementsB+"; pairs="+session.Candidates);
                    cancellationReason=null;
                    results=new List<ClashResult>(); ResultsGrid.ItemsSource=results;
                    ResultsCount.Text="Checking…";
                    Progress.Visibility=System.Windows.Visibility.Visible; Progress.Maximum=Math.Max(1,session.Candidates); Progress.Value=0; SetBusy(true);
                    ProcessBatch(app);
                },"Collecting elements and candidate pairs…");
            });
        }
        private void ProcessBatch(UIApplication app)
        {
            if (session==null) return;
            if (!DocumentIdentity.Same(Document, app.ActiveUIDocument?.Document))
            {
                session.CancelRequested=true; cancellationReason="Model closed or switched";
            }
            if (!session.CancelRequested)
            {
                Diagnostics.Write("Geometry batch starting at pair "+session.Checked);
                session.Step();
                Diagnostics.Write("Geometry batch finished at pair "+session.Checked);
            }
            Progress.Value=session.Checked;
            Summary.Text=scopeLabel+" · Checked "+session.Checked+" / "+session.Candidates+" pairs · "+session.Results.Count+" clashes";
            if (session.Completed)
            {
                results=session.Results; ResultsGrid.ItemsSource=results; ResultsCount.Text=results.Count+" intersections";
                Diagnostics.Write("Run finished; cancelled="+session.CancelRequested+"; results="+results.Count+"; warnings="+session.Warnings);
                Summary.Text=(session.CancelRequested ? "Cancelled · partial results"+(cancellationReason==null ? "" : " ("+cancellationReason+")") : "Completed")+" · "+scopeLabel+" · "+results.Count+" clashes · A "+session.ElementsA+" / B "+session.ElementsB+" elements · "+session.Candidates+" candidates · "+session.Warnings+" geometry warnings";
                session.Dispose(); session=null;
                Progress.Visibility=System.Windows.Visibility.Collapsed; SetBusy(false);
                if (closeAfterCancel) Dispatcher.BeginInvoke(new Action(Close));
                return;
            }
            // Raise the next request after this ExternalEvent has completed.
            resume=(s,e) =>
            {
                RemoveResume();
                if (!closed) { try { request.Post(ProcessBatch); } catch (Exception ex) { Failed(ex); } }
            };
            application.Idling += resume;
        }
        private void RemoveResume() { if (resume!=null) { application.Idling -= resume; resume=null; } }
        private void ModelChanged(object sender,DocumentChangedEventArgs e)
        {
            var changed=e.GetDocument();
            var transactions=e.GetTransactionNames();
            if (transactions.Count>0 && transactions.All(n => n=="Clash Solution - View Clash" || n=="Clash Solution - Section Box")) return;
            if (e.GetDeletedElementIds().Count==0 && !e.GetAddedElementIds().Concat(e.GetModifiedElementIds()).Any(id => !(changed.GetElement(id) is RevitView))) return;
            if (session!=null && sources.Any(s => DocumentIdentity.Same(s.Document,changed))) { session.CancelRequested=true; cancellationReason="Model changed during check; run again"; }
            else if (results.Count>0 && sources.Any(s => DocumentIdentity.Same(s.Document,changed)))
            {
                results.Clear(); ResultsGrid.ItemsSource=null; ResultsCount.Text="0 intersections";
                ClearPreview();
                Summary.Text="Model changed. Run the check again to refresh clash results.";
            }
            metadataCache.Clear();
        }
        private void Cancel_Click(object sender,RoutedEventArgs e) { if (session!=null) { session.CancelRequested=true; Summary.Text="Cancelling check…"; } }
        private ClashResult SelectedResult()
        {
            var result=ResultsGrid.SelectedItem as ClashResult;
            if (result==null) throw new InvalidOperationException("Select one clash result first.");
            return result;
        }
        private void ValidateResult(ClashResult result)
        {
            foreach (var record in new[] {result.RecordA,result.RecordB})
            {
                ClashEngine.ValidateSource(record.Source,Document);
                if (!record.Element.IsValidObject || record.Source.Document.GetElement(record.Element.UniqueId)==null) throw new InvalidOperationException("A clash element was deleted. Run the check again.");
            }
        }
        private void Zoom_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                var result=SelectedResult();
                Queue(app =>
                {
                    ValidateResult(result);
                    var ids=new[] {result.RecordA,result.RecordB}.Select(r => r.Source.Link==null ? r.Element.Id : r.Source.Link.Id).GroupBy(Ids.Value).Select(g => g.First()).ToList();
                    app.ActiveUIDocument.Selection.SetElementIds(ids); app.ActiveUIDocument.ShowElements(ids);
                    Summary.Text="Selected clash #"+result.Number+". Linked elements select their host link instance; IDs in the table belong to the linked model.";
                },"Selecting and zooming to clash…");
            });
        }
        private void Section_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                var result=SelectedResult();
                Queue(app =>
                {
                    ValidateResult(result);
                    var view=Document.ActiveView as View3D;
                    if (view==null || view.IsTemplate) throw new InvalidOperationException("Open a non-template 3D view first.");
                    var margin=new XYZ(1000/304.8,1000/304.8,1000/304.8);
                    using (var transaction=new Transaction(Document,"Clash Solution - Section Box"))
                    {
                        transaction.Start(); view.IsSectionBoxActive=true;
                        view.SetSectionBox(new BoundingBoxXYZ { Min=result.Point-margin, Max=result.Point+margin });
                        if (transaction.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("Revit did not commit the section box change.");
                    }
                    app.ActiveUIDocument.RefreshActiveView(); Summary.Text="Applied a 2,000 mm section box around clash #"+result.Number+".";
                },"Creating section box…");
            });
        }
        private void Export(bool excel)
        {
            UserAction(() =>
            {
                if (results.Count==0) throw new InvalidOperationException("Run a clash check with results before exporting.");
                var dialog=new SaveFileDialog { Title="Export Clash Solution results",FileName="Clash_Solution_Results",DefaultExt=excel ? ".xlsx" : ".csv",Filter=excel ? "Excel Workbook (*.xlsx)|*.xlsx" : "CSV (*.csv)|*.csv" };
                if (dialog.ShowDialog(this)!=true) return;
                var details=SystemDetails.IsChecked==true; var headers=ResultExporter.Headers(details); var rows=results.Select(r => ResultExporter.Row(r,details));
                if (excel) ResultExporter.Xlsx(dialog.FileName,headers,rows); else ResultExporter.Csv(dialog.FileName,headers,rows);
                Summary.Text="Exported "+results.Count+" clash results to "+dialog.FileName;
            });
        }
        private void Csv_Click(object sender,RoutedEventArgs e) { Export(false); }
        private void Excel_Click(object sender,RoutedEventArgs e) { Export(true); }
        private ClashProfile Snapshot(string name)
        {
            Func<IEnumerable<Choice>,List<SavedChoice>> choices=items => items.Select(c => new SavedChoice { Id=c.Id,Name=c.Name }).ToList();
            return new ClashProfile { Name=name, SourceAKey=Source("A")?.Key,SourceBKey=Source("B")?.Key,SourceALabel=Source("A")?.Label,SourceBLabel=Source("B")?.Label,
                CategoriesA=choices(categoriesA.Where(c => c.IsChecked)),CategoriesB=choices(categoriesB.Where(c => c.IsChecked)),Scope=ScopeKind,Levels=choices(levels.Where(l => l.IsChecked)),
                IncludeNonVisible=NonVisible.IsChecked==true,ShowSystemDetails=SystemDetails.IsChecked==true,FiltersA=FilterSpecs("A"),FiltersB=FilterSpecs("B"),FilterModeA=ModeA.SelectedIndex==1 ? "any" : "all",FilterModeB=ModeB.SelectedIndex==1 ? "any" : "all",Criteria=ReadCriteria() };
        }
        private void ApplyProfile(ClashProfile profile)
        {
            var missing=new List<string>();
            SourceA.SelectedItem=sources.FirstOrDefault(s => s.Key==profile.SourceAKey) ?? sources.FirstOrDefault(s => s.Label==profile.SourceALabel);
            SourceB.SelectedItem=sources.FirstOrDefault(s => s.Key==profile.SourceBKey) ?? sources.FirstOrDefault(s => s.Label==profile.SourceBLabel);
            if (SourceA.SelectedItem==null) missing.Add("Set A source"); if (SourceB.SelectedItem==null) missing.Add("Set B source");
            RefreshSide("A"); RefreshSide("B");
            Action<List<Choice>,List<SavedChoice>,string> select=(items,saved,label) =>
            {
                saved=saved ?? new List<SavedChoice>();
                foreach (var c in items) c.IsChecked=saved.Any(s => s.Id==c.Id || string.Equals(s.Name,c.Name,StringComparison.OrdinalIgnoreCase));
                foreach (var s in saved) if (!items.Any(c => s.Id==c.Id || string.Equals(s.Name,c.Name,StringComparison.OrdinalIgnoreCase))) missing.Add(label+": "+s.Name);
            };
            select(categoriesA,profile.CategoriesA,"A category"); select(categoriesB,profile.CategoriesB,"B category");
            EntireScope.IsChecked=profile.Scope=="entire" || string.IsNullOrEmpty(profile.Scope); ViewScope.IsChecked=profile.Scope=="active_view"; LevelScope.IsChecked=profile.Scope=="levels" || profile.Scope=="active_view_level";
            select(levels,profile.Levels,"Level");
            LevelsDropdown.ItemsSource.Cast<Choice>().First(c => c.Id==-1).IsChecked=profile.Scope=="active_view_level";
            if (UseActiveLevel) foreach (var level in levels) level.IsChecked=false;
            UpdateLevelText();
            NonVisible.IsChecked=profile.IncludeNonVisible; SystemDetails.IsChecked=profile.ShowSystemDetails;
            var criteria=profile.Criteria ?? new ClashCriteria();
            MinVolumeInput.Text=criteria.MinVolumeCm3.ToString(CultureInfo.InvariantCulture); MinBoxInput.Text=criteria.MinIntersectionBoxMm.ToString(CultureInfo.InvariantCulture); IgnoreConnected.IsChecked=criteria.IgnoreConnectedMep;
            ClearRules("A"); ClearRules("B");
            foreach (var rule in profile.FiltersA ?? (profile.LegacyFilterA==null ? new List<FilterRule>() : new List<FilterRule>{profile.LegacyFilterA})) AddRule("A",rule);
            foreach (var rule in profile.FiltersB ?? (profile.LegacyFilterB==null ? new List<FilterRule>() : new List<FilterRule>{profile.LegacyFilterB})) AddRule("B",rule);
            ModeA.SelectedIndex=profile.FilterModeA=="any" ? 1 : 0; ModeB.SelectedIndex=profile.FilterModeB=="any" ? 1 : 0;
            ProfileName.Text=profile.Name; Summary.Text="Profile applied: "+profile.Name+(missing.Count==0 ? "" : ". Missing: "+string.Join(", ",missing));
        }
        private void RefreshProfileList(string selected=null)
        {
            var wasInitializing=initializing; initializing=true;
            ProfilesCombo.ItemsSource=null; ProfilesCombo.ItemsSource=profiles.OrderBy(p => p.Name,StringComparer.OrdinalIgnoreCase).ToList();
            ProfilesCombo.SelectedItem=profiles.FirstOrDefault(p => string.Equals(p.Name,selected,StringComparison.OrdinalIgnoreCase));
            initializing=wasInitializing;
        }
        private void Profile_Changed(object sender,SelectionChangedEventArgs e) { if (!initializing && ProfilesCombo.SelectedItem is ClashProfile) UserAction(() => ApplyProfile((ClashProfile)ProfilesCombo.SelectedItem)); }
        private void NewProfile_Click(object sender,RoutedEventArgs e) { ProfilesCombo.SelectedItem=null; ProfileName.Clear(); ProfileName.Focus(); Summary.Text="Enter a profile name, adjust settings, then Save."; }
        private void SaveProfile(string name)
        {
            if (!profilesReadable) throw new InvalidOperationException("The existing profile file could not be read. Repair or restore it before saving: "+ProfileStore.FilePath);
            ValidateSetup(); name=(name ?? "").Trim(); if (name.Length==0) throw new InvalidOperationException("Enter a profile name first.");
            var existing=profiles.FirstOrDefault(p => p.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            if (existing!=null && MessageBox.Show(this,"Replace profile \""+existing.Name+"\"?","Clash Solution",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
            var updated=profiles.Where(p => p!=existing).ToList(); updated.Add(Snapshot(name));
            ProfileStore.Save(updated); profiles=updated; RefreshProfileList(name); ProfileName.Text=name; Summary.Text="Saved profile: "+name;
        }
        private void SaveProfile_Click(object sender,RoutedEventArgs e) { UserAction(() => SaveProfile(ProfileName.Text)); }
        private void ExportProfile_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                ValidateSetup();
                var name=string.IsNullOrWhiteSpace(ProfileName.Text) ? "Clash Profile" : ProfileName.Text.Trim();
                var dialog=new SaveFileDialog { Title="Export current clash profile",FileName="Clash_Profile.json",DefaultExt=".json",Filter="Clash profile (*.json)|*.json" };
                if (dialog.ShowDialog(this)!=true) return;
                ProfileStore.Save(new List<ClashProfile> { Snapshot(name) },dialog.FileName);
                Summary.Text="Exported current check settings, filters and clash conditions to "+dialog.FileName;
            });
        }
        private void ImportProfile_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                if (!profilesReadable) throw new InvalidOperationException("Repair the existing profile file before importing profiles.");
                var dialog=new OpenFileDialog { Title="Import clash profiles",Filter="Clash profile (*.json)|*.json" };
                if (dialog.ShowDialog(this)!=true) return;
                var imported=ProfileStore.Load(dialog.FileName);
                if (imported.Count==0) throw new InvalidOperationException("The file contains no named clash profiles.");
                var merged=profiles.ToList(); var renamed=0;
                foreach (var profile in imported)
                {
                    var original=profile.Name;
                    var suffix=1;
                    while (merged.Any(p => string.Equals(p.Name,profile.Name,StringComparison.OrdinalIgnoreCase)))
                    {
                        profile.Name=original+" (imported "+suffix+")"; suffix++;
                    }
                    if (profile.Name!=original) renamed++;
                    merged.Add(profile);
                }
                ProfileStore.Save(merged); profiles=merged; RefreshProfileList(imported[0].Name); ApplyProfile(imported[0]);
                Summary.Text+=" · Imported and saved "+imported.Count+" profile(s)"+(renamed>0 ? "; "+renamed+" renamed to preserve existing profiles." : ".");
            });
        }
        private void SaveAsProfile_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                var name=AskProfileName((ProfileName.Text.Trim().Length==0 ? "Clash Profile" : ProfileName.Text.Trim())+" Copy");
                if (name!=null) SaveProfile(name);
            });
        }
        private string AskProfileName(string defaultName)
        {
            var dialog=new Window { Title="Save Profile As",Width=420,Height=185,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=this,Background=Background,FontFamily=FontFamily };
            var panel=new StackPanel { Margin=new Thickness(22) };
            panel.Children.Add(new TextBlock { Text="New profile name",Margin=new Thickness(0,0,0,10) });
            var input=new TextBox { Text=defaultName,Height=32,VerticalContentAlignment=VerticalAlignment.Center };
            panel.Children.Add(input);
            var save=new Button { Content="Save",Style=(Style)FindResource("PrimaryButton"),Margin=new Thickness(0,14,0,0),HorizontalAlignment=HorizontalAlignment.Right,IsDefault=true };
            save.Click += (s,e) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult=true; };
            panel.Children.Add(save); dialog.Content=panel; dialog.Loaded += (s,e) => { input.Focus(); input.SelectAll(); };
            return dialog.ShowDialog()==true ? input.Text : null;
        }
        private void DeleteProfile_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                if (!profilesReadable) throw new InvalidOperationException("Cannot modify an unreadable profile file.");
                var selected=ProfilesCombo.SelectedItem as ClashProfile; if (selected==null) throw new InvalidOperationException("Select a profile to delete.");
                if (MessageBox.Show(this,"Delete profile \""+selected.Name+"\"?","Clash Solution",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
                var updated=profiles.Where(p => p!=selected).ToList(); ProfileStore.Save(updated); profiles=updated; RefreshProfileList(); ProfileName.Clear(); Summary.Text="Deleted profile: "+selected.Name;
            });
        }
        private void Refresh_Click(object sender,RoutedEventArgs e)
        {
            UserAction(() =>
            {
                var snapshot=Snapshot(ProfileName.Text);
                Queue(app =>
                {
                    initializing=true; try { ReloadSources(); } finally { initializing=false; }
                    ApplyProfile(snapshot); UpdateViewHint(Document.ActiveView);
                    results.Clear(); ResultsGrid.ItemsSource=null; ResultsCount.Text="0 intersections";
                    ClearPreview();
                    Summary.Text+=" · Sources refreshed; previous results cleared.";
                },"Refreshing model sources and categories…");
            });
        }
        private void Window_Closing(object sender,CancelEventArgs e)
        {
            if (IsBusy)
            {
                e.Cancel=true;
                closeAfterCancel=true;
                if (session!=null) { closeAfterCancel=true; session.CancelRequested=true; Summary.Text="Cancelling check before closing…"; }
                else Summary.Text="Waiting for the pending Revit action before closing.";
            }
        }
        private void Minimize_Click(object sender,RoutedEventArgs e) { WindowState=WindowState.Minimized; }
        internal void Shutdown()
        {
            RemoveResume();
            if (session!=null) { session.Dispose(); session=null; }
            SetBusy(false); Close();
        }
        private void Maximize_Click(object sender,RoutedEventArgs e) { WindowState=WindowState==WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void CloseWindow_Click(object sender,RoutedEventArgs e) { Close(); }
        private void Window_StateChanged(object sender,EventArgs e) { Root.Margin=WindowState==WindowState.Maximized ? new Thickness(7) : new Thickness(0); }

        private sealed class RuleEditor
        {
            public Grid Panel { get; }
            private readonly ComboBox parameter;
            private readonly ComboBox op;
            private readonly ComboBox value;
            private Dictionary<string,List<string>> metadata=new Dictionary<string,List<string>>();
            public RuleEditor(ClashWindow owner,string side,FilterRule spec)
            {
                Panel=new Grid();
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1.25,GridUnitType.Star) });
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(105) });
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(32) });
                parameter=new ComboBox { IsEditable=true,Text=spec?.Parameter ?? "",ToolTip="Parameter name" };
                op=new ComboBox { ItemsSource=new[] {"Equals","Contains","Starts with","Not Equals"},SelectedItem=spec?.Operator ?? "Equals" };
                value=new ComboBox { IsEditable=true,Text=spec?.Value ?? "",ToolTip="Actual value or custom text" };
                var remove=new Button { Content="×",Padding=new Thickness(5),Margin=new Thickness(0,0,0,8),ToolTip="Remove rule" };
                Grid.SetColumn(op,1); Grid.SetColumn(value,2); Grid.SetColumn(remove,3);
                Panel.Children.Add(parameter); Panel.Children.Add(op); Panel.Children.Add(value); Panel.Children.Add(remove);
                parameter.SelectionChanged += (s,e) => UpdateValues(parameter.SelectedItem as string ?? parameter.Text);
                parameter.LostKeyboardFocus += (s,e) => UpdateValues(parameter.Text);
                remove.Click += (s,e) => { owner.Rows(side).Remove(this); owner.RulePanel(side).Children.Remove(Panel); };
            }
            public FilterRule Spec() { return new FilterRule { Parameter=parameter.Text.Trim(),Operator=op.SelectedItem as string ?? "Equals",Value=value.Text.Trim() }; }
            public void Metadata(Dictionary<string,List<string>> data)
            {
                var name=parameter.Text; metadata=data;
                parameter.ItemsSource=data.Keys.OrderBy(n => n,StringComparer.OrdinalIgnoreCase).ToList(); parameter.Text=name;
                UpdateValues(name);
            }
            private void UpdateValues(string name)
            {
                var old=value.Text; List<string> values;
                value.ItemsSource=metadata.TryGetValue(name,out values) ? values : null; value.Text=old;
            }
        }
    }
}
