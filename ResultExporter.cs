using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MEP_Check_Clash_ver1
{
    public static class ResultExporter
    {
        public static string[] Headers(bool details)
        {
            var h=new List<string> { "Clash Key","Status","Source A","Element ID A","UniqueId A","Category A","Type A" };
            if (details) h.AddRange(new[] {"System A","System Classification A","Service A"});
            h.AddRange(new[] {"Source B","Element ID B","UniqueId B","Category B","Type B"});
            if (details) h.AddRange(new[] {"System B","System Classification B","Service B"});
            h.Add("Level"); return h.ToArray();
        }
        public static string[] Row(ClashResult r, bool details)
        {
            var row=new List<string> {r.ClashKey,r.Status,r.SourceA,r.IdA.ToString(CultureInfo.InvariantCulture),r.UniqueIdA,r.CategoryA,r.TypeA};
            if (details) row.AddRange(new[] {r.SystemA,r.ClassificationA,r.ServiceA});
            row.AddRange(new[] {r.SourceB,r.IdB.ToString(CultureInfo.InvariantCulture),r.UniqueIdB,r.CategoryB,r.TypeB});
            if (details) row.AddRange(new[] {r.SystemB,r.ClassificationB,r.ServiceB});
            row.Add(r.Level); return row.ToArray();
        }
        public static void Csv(string path, string[] headers, IEnumerable<string[]> rows)
        {
            WriteAtomic(path,temp =>
            {
                using (var writer=new StreamWriter(temp,false,new UTF8Encoding(true)))
                    foreach (var row in new[] {headers}.Concat(rows)) writer.WriteLine(string.Join(",",row.Select(CsvCell)));
            });
        }
        private static string CsvCell(string value)
        {
            value=value ?? "";
            // Protect spreadsheet consumers from formula injection in model metadata.
            var trimmed=value.TrimStart();
            if (trimmed.Length>0 && "=+-@".Contains(trimmed[0])) value="'"+value;
            return "\""+value.Replace("\"","\"\"")+"\"";
        }
        private static void WriteAtomic(string path, Action<string> write)
        {
            var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { write(temp); FileSave.Commit(temp,path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static readonly XNamespace S="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static string Column(int index)
        {
            var s=""; for (index++; index>0; index=(index-1)/26) s=(char)('A'+(index-1)%26)+s; return s;
        }
        private static string Clean(string value) { return new string((value ?? "").Where(XmlConvert.IsXmlChar).Take(32767).ToArray()); }
        public static void Xlsx(string path, string[] headers, IEnumerable<string[]> rows)
        {
            var data=rows.ToList();
            if (data.Count>1048575) throw new InvalidOperationException("Excel supports up to 1,048,575 results plus the header.");
            WriteAtomic(path,temp =>
            {
                using (var archive=new ZipArchive(File.Create(temp),ZipArchiveMode.Create))
                {
                    XNamespace ct="http://schemas.openxmlformats.org/package/2006/content-types";
                    Write(archive,"[Content_Types].xml",new XElement(ct+"Types",
                        new XElement(ct+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),
                        new XElement(ct+"Default",new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),
                        Override(ct,"/xl/workbook.xml","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
                        Override(ct,"/xl/worksheets/sheet1.xml","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
                        Override(ct,"/xl/styles.xml","application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")));
                    XNamespace rel="http://schemas.openxmlformats.org/package/2006/relationships";
                    const string office="http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
                    Write(archive,"_rels/.rels",new XElement(rel+"Relationships",Relation(rel,"rId1",office+"officeDocument","xl/workbook.xml")));
                    Write(archive,"xl/_rels/workbook.xml.rels",new XElement(rel+"Relationships",Relation(rel,"rId1",office+"worksheet","worksheets/sheet1.xml"),Relation(rel,"rId2",office+"styles","styles.xml")));
                    XNamespace r=office.TrimEnd('/');
                    Write(archive,"xl/workbook.xml",new XElement(S+"workbook",new XAttribute(XNamespace.Xmlns+"r",r),new XElement(S+"sheets",new XElement(S+"sheet",new XAttribute("name","Clash Results"),new XAttribute("sheetId",1),new XAttribute(r+"id","rId1")))));
                    Write(archive,"xl/styles.xml",Styles());
                    // Stream rows so exports do not build a second full worksheet in memory.
                    using (var stream=archive.CreateEntry("xl/worksheets/sheet1.xml").Open())
                    using (var xml=XmlWriter.Create(stream,new XmlWriterSettings { Encoding=new UTF8Encoding(false) }))
                    {
                        xml.WriteStartDocument(); xml.WriteStartElement("worksheet",S.NamespaceName);
                        new XElement(S+"dimension",new XAttribute("ref","A1:"+Column(headers.Length-1)+(data.Count+1))).WriteTo(xml);
                        new XElement(S+"sheetViews",new XElement(S+"sheetView",new XAttribute("workbookViewId",0),new XElement(S+"pane",new XAttribute("ySplit",1),new XAttribute("topLeftCell","A2"),new XAttribute("activePane","bottomLeft"),new XAttribute("state","frozen")))).WriteTo(xml);
                        new XElement(S+"cols",headers.Select((h,i) => new XElement(S+"col",new XAttribute("min",i+1),new XAttribute("max",i+1),new XAttribute("width",h=="Clash Key" ? 48 : h.Contains("UniqueId") ? 38 : h.Contains("Element ID") ? 16 : 24),new XAttribute("customWidth",1)))).WriteTo(xml);
                        xml.WriteStartElement("sheetData",S.NamespaceName);
                        var number=0;
                        foreach (var row in new[] {headers}.Concat(data))
                        {
                            number++;
                            var element=new XElement(S+"row",new XAttribute("r",number),new XAttribute("ht",number==1 ? 28 : 22),new XAttribute("customHeight",1));
                            for (var i=0; i<row.Length; i++) element.Add(new XElement(S+"c",new XAttribute("r",Column(i)+number),new XAttribute("t","inlineStr"),new XAttribute("s",number==1 ? 1 : number%2==0 ? 2 : 0),new XElement(S+"is",new XElement(S+"t",new XAttribute(XNamespace.Xml+"space","preserve"),Clean(row[i])))));
                            element.WriteTo(xml);
                        }
                        xml.WriteEndElement();
                        new XElement(S+"autoFilter",new XAttribute("ref","A1:"+Column(headers.Length-1)+(data.Count+1))).WriteTo(xml);
                        xml.WriteEndElement(); xml.WriteEndDocument();
                    }
                }
            });
        }
        private static XElement Override(XNamespace ns,string part,string type) { return new XElement(ns+"Override",new XAttribute("PartName",part),new XAttribute("ContentType",type)); }
        private static XElement Relation(XNamespace ns,string id,string type,string target) { return new XElement(ns+"Relationship",new XAttribute("Id",id),new XAttribute("Type",type),new XAttribute("Target",target)); }
        private static void Write(ZipArchive archive,string name,XElement root)
        {
            using (var stream=archive.CreateEntry(name).Open()) new XDocument(new XDeclaration("1.0","utf-8","yes"),root).Save(stream);
        }
        private static XElement Styles()
        {
            Func<string,XElement> font=color => new XElement(S+"font",new XElement(S+"sz",new XAttribute("val",11)),new XElement(S+"color",new XAttribute("rgb",color)),new XElement(S+"name",new XAttribute("val","Segoe UI")));
            var bold=font("FFFFFFFF"); bold.Add(new XElement(S+"b"));
            Func<string,XElement> fill=color => new XElement(S+"fill",new XElement(S+"patternFill",new XAttribute("patternType","solid"),new XElement(S+"fgColor",new XAttribute("rgb",color)),new XElement(S+"bgColor",new XAttribute("indexed",64))));
            Func<int,int,XElement> xf=(f,b) => new XElement(S+"xf",new XAttribute("numFmtId",0),new XAttribute("fontId",f),new XAttribute("fillId",b),new XAttribute("borderId",0),new XAttribute("xfId",0),new XAttribute("applyFont",1),new XAttribute("applyFill",1),new XAttribute("applyAlignment",1),new XElement(S+"alignment",new XAttribute("vertical","center")));
            return new XElement(S+"styleSheet",
                new XElement(S+"fonts",new XAttribute("count",2),font("FF37322B"),bold),
                new XElement(S+"fills",new XAttribute("count",4),new XElement(S+"fill",new XElement(S+"patternFill",new XAttribute("patternType","none"))),new XElement(S+"fill",new XElement(S+"patternFill",new XAttribute("patternType","gray125"))),fill("FF62543C"),fill("FFF5F2EE")),
                new XElement(S+"borders",new XAttribute("count",1),new XElement(S+"border",new XElement(S+"left"),new XElement(S+"right"),new XElement(S+"top"),new XElement(S+"bottom"),new XElement(S+"diagonal"))),
                new XElement(S+"cellStyleXfs",new XAttribute("count",1),new XElement(S+"xf",new XAttribute("numFmtId",0),new XAttribute("fontId",0),new XAttribute("fillId",0),new XAttribute("borderId",0))),
                new XElement(S+"cellXfs",new XAttribute("count",3),xf(0,0),xf(1,2),xf(0,3)),
                new XElement(S+"cellStyles",new XAttribute("count",1),new XElement(S+"cellStyle",new XAttribute("name","Normal"),new XAttribute("xfId",0),new XAttribute("builtinId",0))));
        }
    }
}
