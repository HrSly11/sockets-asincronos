namespace Chat.Presentation;

public static class FluentGlyphs
{
    public const string Chat = "\uE8BD";
    public const string Send = "\uE724";
    public const string Attach = "\uE723";
    public const string People = "\uE716";
    public const string Server = "\uE968";
    public const string Activity = "\uE9D9";
    public const string Document = "\uE8A5";
    public const string Image = "\uEB9F";
    public const string Archive = "\uF012";
    public const string Code = "\uE943";
    public const string Spreadsheet = "\uE9F9";

    public static string ForFile(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => Image,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => Archive,
            ".cs" or ".js" or ".ts" or ".html" or ".css" or ".json" or ".xml" => Code,
            ".csv" or ".xls" or ".xlsx" => Spreadsheet,
            _ => Document
        };
    }
}
