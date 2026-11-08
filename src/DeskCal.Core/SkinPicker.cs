namespace DeskCal.Core;

/// <summary>Chooses which background photo is current. Pure and stateless, so the choice survives restarts without being stored.</summary>
public static class SkinPicker
{
    /// <summary>Picture types WPF can decode on its own.</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    public static bool IsPicture(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The pictures of a folder (not its subfolders), sorted by name, at most <paramref name="max"/>. Missing or unreadable folders give an empty list.</summary>
    public static List<string> List(string? folder, int max = 500)
    {
        if (string.IsNullOrWhiteSpace(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder).Where(IsPicture)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>
    /// Index of the picture to show. With no rotation it is always 0; otherwise the picture advances every
    /// <paramref name="rotateMinutes"/> minutes of wall-clock time, so every run of the program agrees.
    /// </summary>
    public static int Pick(int count, DateTimeOffset now, int rotateMinutes)
    {
        if (count <= 0) return -1;
        if (rotateMinutes <= 0) return 0;
        long slot = now.ToUnixTimeSeconds() / 60 / rotateMinutes;
        return (int)(slot % count);
    }
}
