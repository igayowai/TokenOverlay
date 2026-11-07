using System.Runtime.InteropServices;

namespace TokenOverlay;

// Codex stopped adding new chats to session_index.jsonl (last write 2026-10-15); their titles now live only in its
// state database (state_<n>.sqlite, table threads). Read id and the shown title (name when set, else title), nothing else, through Windows' own SQLite,
// opened read-only: Codex's files are never written.
internal static class CodexThreadTitles
{
    public static Dictionary<string, string>? Read(string codexHome)
    {
        var path = Latest(codexHome);
        if (path is null) return null;
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Utf8(path), out db, OpenReadOnly, IntPtr.Zero) != Ok) return null;
            sqlite3_busy_timeout(db, 200);
            if (sqlite3_prepare_v2(db, Utf8("select id, coalesce(nullif(name, ''), title) from threads where archived = 0"), -1, out statement, IntPtr.Zero) != Ok)
                return null;
            int step;
            while ((step = sqlite3_step(statement)) == Row)
            {
                if (Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) is { Length: > 0 } id
                    && Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 1)) is { Length: > 0 } title)
                    titles[id] = title;
            }
            return step == Done ? titles : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
    }

    // The highest-numbered state database, as Codex migrates by bumping the number.
    private static string? Latest(string codexHome)
    {
        try
        {
            return Directory.EnumerateFiles(codexHome, "state_*.sqlite")
                .Select(file => (file, number: int.TryParse(Path.GetFileNameWithoutExtension(file)["state_".Length..], out var n) ? n : -1))
                .Where(pair => pair.number >= 0).OrderByDescending(pair => pair.number).Select(pair => pair.file).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text + "\0");

    private const int Ok = 0, Row = 100, Done = 101, OpenReadOnly = 0x1;
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_close_v2(IntPtr db);
}
