using System;
using System.IO;

namespace MEP_Check_Clash_ver1
{
    internal static class FileSave
    {
        // Prefer atomic replacement; some network/restricted filesystems only
        // support renames. Keep the original recoverable until the new file lands.
        public static void Commit(string staged,string target,string backup=null)
        {
            if (!File.Exists(target)) { File.Move(staged,target); return; }
            try { File.Replace(staged,target,backup); return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (PlatformNotSupportedException) { }
            if (backup!=null) File.Copy(target,backup,true);
            var rollback=target+"."+Guid.NewGuid().ToString("N")+".previous";
            File.Move(target,rollback);
            try { File.Move(staged,target); }
            catch { File.Move(rollback,target); throw; }
            File.Delete(rollback);
        }
    }
}
