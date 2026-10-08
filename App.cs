using System;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEP_Check_Clash_ver1.UI;

namespace MEP_Check_Clash_ver1
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            const string tab = "Clash Solution";
            try { app.CreateRibbonTab(tab); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
            var panel = app.CreateRibbonPanel(tab, "Clash Detection");
            var button = (PushButton)panel.AddItem(new PushButtonData("ClashDetector", "Clash\nDetector",
                Assembly.GetExecutingAssembly().Location, typeof(ClashCommand).FullName));
            button.ToolTip = "Check exact solid clashes between current model and loaded Revit links.";
            button.LargeImage=Logo(32); button.Image=Logo(16);
            return Result.Succeeded;
        }
        internal static BitmapImage Logo(int size)
        {
            using (var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ClashSolution.Logo"))
            {
                var image=new BitmapImage(); image.BeginInit(); image.DecodePixelWidth=size;
                image.StreamSource=stream; image.CacheOption=BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image;
            }
        }
        public Result OnShutdown(UIControlledApplication app)
        {
            ClashCommand.CloseWindow();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ClashCommand : IExternalCommand
    {
        private static ClashWindow window;
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (data.Application.ActiveUIDocument == null)
            {
                TaskDialog.Show("Clash Solution", "Open a Revit model first.");
                return Result.Cancelled;
            }
            try
            {
                if (window != null && window.IsVisible)
                {
                    if (DocumentIdentity.Same(window.Document, data.Application.ActiveUIDocument.Document)) { window.Activate(); return Result.Succeeded; }
                    if (window.IsBusy) { TaskDialog.Show("Clash Solution", "Finish or cancel the current check before switching models."); return Result.Cancelled; }
                    window.Close();
                }
                window = new ClashWindow(data.Application);
                new WindowInteropHelper(window).Owner = data.Application.MainWindowHandle;
                window.Closed += (s, e) => window = null;
                window.Show();
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.ToString(); return Result.Failed; }
        }
        internal static void CloseWindow() { if (window != null) window.Shutdown(); }
    }
}
