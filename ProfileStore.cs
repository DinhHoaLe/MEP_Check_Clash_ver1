using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace MEP_Check_Clash_ver1
{
    public static class ProfileStore
    {
        public static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ClashSolution","profiles.json"); } }
        public static List<ClashProfile> Load(string path = null)
        {
            path = path ?? FilePath;
            if (!File.Exists(path)) return new List<ClashProfile>();
            var bytes=File.ReadAllBytes(path);
            var text=System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF',' ','\r','\n','\t');
            using (var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)))
            {
                var list=text.StartsWith("[") ? (List<ClashProfile>)new DataContractJsonSerializer(typeof(List<ClashProfile>)).ReadObject(stream)
                    : ((ProfilePayload)new DataContractJsonSerializer(typeof(ProfilePayload)).ReadObject(stream)).Profiles;
                var valid=(list ?? new List<ClashProfile>()).Where(p => !string.IsNullOrWhiteSpace(p?.Name)).OrderBy(p => p.Name,StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var profile in valid) { profile.Criteria=profile.Criteria ?? new ClashCriteria(); profile.Criteria.Validate(); }
                return valid;
            }
        }
        public static void Save(List<ClashProfile> profiles, string path = null)
        {
            path = path ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using (var stream=File.Create(temp)) new DataContractJsonSerializer(typeof(ProfilePayload)).WriteObject(stream,new ProfilePayload { Profiles=profiles.OrderBy(p => p.Name,StringComparer.OrdinalIgnoreCase).ToList() });
                FileSave.Commit(temp,path,path+".bak");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
