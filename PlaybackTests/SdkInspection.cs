using System.Reflection;
using System.Reflection.Emit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

internal static class SdkInspection
{
 public static void Run()
 {
    Console.WriteLine("EVENTS ILibraryManager " + string.Join("; ", typeof(ILibraryManager).GetEvents().Select(e => e.ToString())));
    Console.WriteLine("EVENTS BaseItem " + string.Join("; ", typeof(BaseItem).GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Select(e => e.ToString())));
    var ops = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
    foreach (var method in new[] { typeof(BaseItem).GetMethod("GetUserDataKey"), typeof(BaseItem).GetMethod("GetUserDataKeyInternal", BindingFlags.Instance | BindingFlags.NonPublic), typeof(Video).GetMethod("GetUserDataKeyInternal", BindingFlags.Instance | BindingFlags.NonPublic), typeof(Movie).GetMethod("GetUserDataKeyInternal", BindingFlags.Instance | BindingFlags.NonPublic), typeof(MediaBrowser.Controller.Entities.TV.Episode).GetMethod("GetUserDataKeyInternal", BindingFlags.Instance | BindingFlags.NonPublic), typeof(Video).GetMethod("GetUserDataKey", BindingFlags.Public | BindingFlags.Static, new[] { typeof(BaseItem) }), typeof(Video).GetProperty("EnableDefaultVideoUserDataKeys", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetMethod, typeof(Video).GetMethod("GetRunTimeTicksForPlayState"), typeof(Video).GetMethod("GetUserDataKey", BindingFlags.Public | BindingFlags.Static, new[] { typeof(BaseItem), typeof(bool) }), typeof(BaseItem).GetMethod("GetRunTimeTicksForPlayState") }.Where(m => m != null).Distinct())
    {
        Console.WriteLine(method);
        var il = method!.GetMethodBody()!.GetILAsByteArray()!;
        for (int i = 0; i < il.Length;)
        {
            var offset = i; ushort code = il[i++]; if (code == 0xfe) code = (ushort)(0xfe00 | il[i++]);
            var op = ops[code]; object? operand = null;
            var size = op.OperandType switch { OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1, OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8, _ => 4 };
            if (size == 4) { var token = BitConverter.ToInt32(il, i); operand = token; try { if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok) operand = method.Module.ResolveMember(token); else if (op.OperandType == OperandType.InlineString) operand = method.Module.ResolveString(token); } catch { } }
            else if (size == 1) operand = il[i];
            Console.WriteLine($"{offset:x4}: {op} {operand}"); i += size;
        }
    }
    var movie = new Movie { InternalId = 12, UserDataKey = "embyhistory:test" };
    Console.WriteLine("CUSTOM KEY actual=" + ((BaseItem)movie).GetUserDataKey(null!));

 }
}
