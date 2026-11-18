using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
// Executes the actual Emby SDK predicate, not a hand-written imitation of its path rules.
var options = new MetadataRefreshOptions((IFileSystem)null!);
var root = "/config/data/emby-history/archive/14";
var series = new Folder { Path = root + "/Test (2026)" };
var season = new Folder { Path = series.Path + "/Season 01" };
var neighbor = new Folder { Path = "/config/data/emby-history/archive/140/Other" };
var leaf = season.Path + "/Test - S01E01.disc";
int passed=0;
void Check(string name,bool yes){if(!yes)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
options.RefreshPaths = new[] { root };
Check("root-only refresh silently skips series metadata", !options.RefreshItem(series));
Check("root-only refresh silently skips season metadata", !options.RefreshItem(season));
options.RefreshPaths = new[] { leaf };
Check("changed leaf admits series ancestor", options.RefreshItem(series));
Check("changed leaf admits season ancestor", options.RefreshItem(season));
Check("changed leaf excludes neighboring source", !options.RefreshItem(neighbor));
options.RefreshPaths = new[] { season.Path + "/already-deleted.disc" };
Check("deleted leaf still admits container for withdrawal", options.RefreshItem(season));
Console.WriteLine($"RESULT {passed}/6 real SDK RefreshItem contract checks");
