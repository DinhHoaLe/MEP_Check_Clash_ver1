using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace MEP_Check_Clash_ver1
{
    internal static class Diagnostics
    {
        private static readonly object gate = new object();
        internal static void Write(string stage, Exception error = null)
        {
            try
            {
                var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClashSolution");
                Directory.CreateDirectory(folder);
                var version=Assembly.GetExecutingAssembly().GetName().Version;
                var line=DateTimeOffset.Now.ToString("o")+" [PID "+Process.GetCurrentProcess().Id+" / v"+version+"] "+stage+Environment.NewLine;
                if (error!=null) line+=error+Environment.NewLine;
                lock (gate) File.AppendAllText(Path.Combine(folder,"errors.log"),line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        internal static bool IsOwnUiException(Exception error)
        {
            for (var ex=error; ex!=null; ex=ex.InnerException)
            {
                if (ex is AccessViolationException || ex is OutOfMemoryException || ex is System.Runtime.InteropServices.SEHException) return false;
            }
            for (var ex=error; ex!=null; ex=ex.InnerException)
            foreach (var frame in new StackTrace(ex).GetFrames() ?? new StackFrame[0])
            {
                var type=frame.GetMethod()?.DeclaringType;
                if (type?.Assembly==typeof(Diagnostics).Assembly && type.FullName.StartsWith("MEP_Check_Clash_ver1.UI.",StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
