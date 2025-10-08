using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ConnectXRevit.UI;

namespace ConnectXRevit.Core
{
    [Transaction(TransactionMode.Manual)]
    public class AIChat : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ChatView window = new ChatView();
            _ = new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle
            };
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

            window.Show();

            return Result.Succeeded;
        }
    }
}
