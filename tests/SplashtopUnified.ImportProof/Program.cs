using SplashtopUnified.Core;
using SplashtopUnified.App;
static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
var old=new InventoryItem(new Computer("a",1,"old"),new LocalComputerMetadata("a",1,true,"alias",new[]{"tag"}));
var other=new InventoryItem(new Computer("b",1,"other"));
var original=new List<InventoryItem>{old,other};
var noIds=InventoryImportPipeline.Prepare(original,"a","Name\r\nDuplicate\r\nDuplicate");
Check(noIds.ImportedCount==2 && noIds.ReplacementItems.Count==3,"missing IDs and duplicate names individually retained");
Check(noIds.ReplacementItems.Skip(1).All(x=>x.Computer.Identity is null && x.LocalMetadata is null),"no fabricated identity or metadata");
Check(ReferenceEquals(original[0],old)&&original.Count==2,"prepare does not mutate original");
var keyed=InventoryImportPipeline.Prepare(original,"a","Name,ID\r\nrenamed,1");
var m=keyed.ReplacementItems.Last().LocalMetadata;
Check(m?.IsFavorite==true && m.Alias=="alias" && m.Tags.Single()=="tag","keyed favorite alias tags preserved");
Check(ReferenceEquals(keyed.ReplacementItems[0],other),"other account retained");
foreach(var csv in new[]{"Name,ID\r\nx,1\r\ny,1","Name,ID\r\nx,invalid"}){bool failed=false;try{InventoryImportPipeline.Prepare(original,"a",csv);}catch(FormatException){failed=true;}Check(failed&&original.Count==2&&ReferenceEquals(original[0],old),"invalid import preserves source");}
var empty=InventoryImportPipeline.Prepare(original,"a","Name,ID");Check(empty.ImportedCount==0&&empty.ReplacementItems.SequenceEqual(original),"empty import preserves original");
