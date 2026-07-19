using Guna.UI2.WinForms;

namespace Chat.Presentation;

public static class Theme
{
    public const int InputRadius = 8;
    public const int CardRadius = 12;
    public const int IconSurfaceRadius = 26;
    public const int ProgressRadius = 3;
    public const string LogFontFamilyName = "Consolas";

    public static readonly Color MainBackground = ColorTranslator.FromHtml("#F5F6F8");
    public static readonly Color Sidebar = ColorTranslator.FromHtml("#1F2430");
    public static readonly Color SidebarSurface = ColorTranslator.FromHtml("#2B3140");
    public static readonly Color Primary = ColorTranslator.FromHtml("#4F6BFF");
    public static readonly Color PrimaryHover = ColorTranslator.FromHtml("#4059E8");
    public static readonly Color PrimaryPressed = ColorTranslator.FromHtml("#344BCB");
    public static readonly Color PrimaryDisabled = ColorTranslator.FromHtml("#AEB9F5");
    public static readonly Color Success = ColorTranslator.FromHtml("#34C759");
    public static readonly Color Error = ColorTranslator.FromHtml("#E5484D");
    public static readonly Color MainText = ColorTranslator.FromHtml("#1B1E27");
    public static readonly Color SecondaryText = ColorTranslator.FromHtml("#6B7280");
    public static readonly Color OwnMessage = Primary;
    public static readonly Color OtherMessage = ColorTranslator.FromHtml("#E9EAEE");
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = ColorTranslator.FromHtml("#DDE0E6");
    public static readonly Color DisabledSurface = ColorTranslator.FromHtml("#E4E6EB");
    public static readonly Color LogBackground = ColorTranslator.FromHtml("#171A22");
    public static readonly Color LogText = ColorTranslator.FromHtml("#D2D6DF");
    public static readonly Color Offline = ColorTranslator.FromHtml("#8B919E");
    public static readonly Color AlternatingRow = ColorTranslator.FromHtml("#FAFAFB");

    public static string UiFontFamilyName { get; } =
        IsFontInstalled("Segoe UI Variable") ? "Segoe UI Variable" : "Segoe UI";

    public static string IconFontFamilyName { get; } =
        IsFontInstalled("Segoe Fluent Icons")
            ? "Segoe Fluent Icons"
            : IsFontInstalled("Segoe MDL2 Assets")
                ? "Segoe MDL2 Assets"
                : UiFontFamilyName;

    public static Font BodyFont(float size = 9F, FontStyle style = FontStyle.Regular) =>
        new(UiFontFamilyName, size, style, GraphicsUnit.Point);

    public static Font TitleFont(float size = 15F) =>
        new(UiFontFamilyName, size, FontStyle.Bold, GraphicsUnit.Point);

    public static Font MetadataFont() =>
        new(UiFontFamilyName, 8F, FontStyle.Regular, GraphicsUnit.Point);

    public static Font IconFont(float size) =>
        new(IconFontFamilyName, size, FontStyle.Regular, GraphicsUnit.Point);

    public static Font LogFont(float size = 9F) =>
        new(LogFontFamilyName, size, FontStyle.Regular, GraphicsUnit.Point);

    public static void StylePrimaryButton(Guna2Button button)
    {
        button.BorderRadius = InputRadius;
        button.FillColor = Primary;
        button.ForeColor = Color.White;
        button.Font = BodyFont(9F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.Animated = true;
        button.HoverState.FillColor = PrimaryHover;
        button.PressedColor = PrimaryPressed;
        button.DisabledState.FillColor = PrimaryDisabled;
        button.DisabledState.ForeColor = Color.WhiteSmoke;
    }

    public static void StyleSecondaryButton(Guna2Button button, bool darkSurface = false)
    {
        button.BorderRadius = InputRadius;
        button.BorderThickness = 1;
        button.BorderColor = darkSurface ? Color.FromArgb(80, 255, 255, 255) : Border;
        button.FillColor = darkSurface ? Color.FromArgb(35, 255, 255, 255) : Surface;
        button.ForeColor = darkSurface ? Color.White : MainText;
        button.Font = BodyFont(9F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.Animated = true;
        button.HoverState.FillColor = darkSurface
            ? Color.FromArgb(55, 255, 255, 255)
            : ColorTranslator.FromHtml("#F0F1F4");
        button.PressedColor = darkSurface
            ? Color.FromArgb(70, 255, 255, 255)
            : ColorTranslator.FromHtml("#E4E6EB");
        button.DisabledState.FillColor = DisabledSurface;
        button.DisabledState.ForeColor = SecondaryText;
    }

    public static void StyleTextBox(Guna2TextBox textBox)
    {
        textBox.BorderRadius = InputRadius;
        textBox.BorderColor = Border;
        textBox.FillColor = Surface;
        textBox.ForeColor = MainText;
        textBox.PlaceholderForeColor = SecondaryText;
        textBox.Font = BodyFont();
        textBox.HoverState.BorderColor = PrimaryHover;
        textBox.FocusedState.BorderColor = Primary;
        textBox.DisabledState.FillColor = DisabledSurface;
        textBox.DisabledState.ForeColor = SecondaryText;
        textBox.DisabledState.BorderColor = Border;
    }

    private static bool IsFontInstalled(string familyName)
    {
        using var testFont = new Font(familyName, 9F);
        return string.Equals(testFont.Name, familyName, StringComparison.OrdinalIgnoreCase);
    }
}
