using System;

namespace Urlaubstool.Infrastructure.Pdf;

/// <summary>
/// Defines precise coordinate-based layout for stamping text onto Template.pdf (v1).
/// All coordinates use PDF coordinate system: origin (0,0) at bottom-left.
/// Y-coordinates represent text baseline position for accurate alignment with printed lines.
/// Template contains two identical sections: Original (top half) and Kopie (bottom half).
/// </summary>
public static class TemplateLayout
{
    // PDF page dimensions (A4: 595pt x 842pt)
    public const int PageWidth = 595;
    public const int PageHeight = 842;
    public const int PageNumber = 1; // Single-page template

    // Font configuration
    public const string FontFamily = "Helvetica"; // Standard PDF font, no embedding required
    public const float DefaultFontSize = 10f;
    public const float SmallFontSize = 8f;
    public const float MinimumFontSize = 6f; // Shrink threshold before ellipsis

    /// <summary>
    /// Field definitions for the Original section (top half of page).
    /// Each field specifies X/Y baseline position, max width, font size, and alignment.
    /// </summary>
    public static class Original
    {
        // Personal Information Section (top-left area)
        public static readonly FieldLayout Name = new(120, 770, 200, DefaultFontSize, TextAlignment.Left);
        public static readonly FieldLayout Vorname = new(120, 755, 200, DefaultFontSize, TextAlignment.Left);
        public static readonly FieldLayout Adresse = new(120, 740, 250, DefaultFontSize, TextAlignment.Left);
        public static readonly FieldLayout Abteilung = new(120, 725, 200, DefaultFontSize, TextAlignment.Left);
        public static readonly FieldLayout Personalnummer = new(120, 710, 150, DefaultFontSize, TextAlignment.Left);

        // Request Date Range Section
        public static readonly FieldLayout StartDatum = new(150, 680, 100, DefaultFontSize, TextAlignment.Left);
        public static readonly FieldLayout Enddatum = new(350, 680, 100, DefaultFontSize, TextAlignment.Left);

        // Vacation Calculation Section (mid-page tabular layout)
        public static readonly FieldLayout GesamtVerfuegbarerUrlaub = new(400, 640, 80, DefaultFontSize, TextAlignment.Right);
        public static readonly FieldLayout BereitsErhaltenerUrlaub = new(400, 625, 80, DefaultFontSize, TextAlignment.Right);
        public static readonly FieldLayout MitDiesemAntragBeantragt = new(400, 610, 80, DefaultFontSize, TextAlignment.Right);
        public static readonly FieldLayout Resturlaub = new(400, 595, 80, DefaultFontSize, TextAlignment.Right);
        public static readonly FieldLayout AnzahlHalbtage = new(400, 565, 80, DefaultFontSize, TextAlignment.Right);

        // AZA-Tage multiline section (5 lines maximum, wrapping/clipping if needed)
        // This section lists vocational school days or special days during the vacation period
        public static readonly MultilineFieldLayout AzaTage = new(
            X: 80,
            TopY: 540, // Start Y for first line
            MaxWidth: 450,
            LineHeight: 12f,
            MaxLines: 5,
            FontSize: SmallFontSize,
            Alignment: TextAlignment.Left
        );

        // Signature and Date Section (bottom of original section)
        public static readonly FieldLayout AntragsDatum = new(120, 480, 100, DefaultFontSize, TextAlignment.Left);
        public static readonly FieldLayout UnterschriftPlatzhalter = new(350, 480, 150, DefaultFontSize, TextAlignment.Left);

        // Administrative fields (approval/rejection) - typically left blank for initial submission
        public static readonly FieldLayout Genehmigt = new(120, 450, 100, DefaultFontSize, TextAlignment.Left);
        public static readonly FieldLayout Bearbeitet = new(250, 450, 100, DefaultFontSize, TextAlignment.Left);
        public static readonly FieldLayout Personalabteilung = new(380, 450, 150, DefaultFontSize, TextAlignment.Left);
        public static readonly FieldLayout AblehnungGrund = new(80, 420, 450, SmallFontSize, TextAlignment.Left);
    }

    /// <summary>
    /// Field definitions for the Kopie (copy) section (bottom half of page).
    /// Identical structure to Original section, shifted down vertically.
    /// Y-offset of approximately 421pt (half page height) from Original fields.
    /// </summary>
    public static class Kopie
    {
        private const float YOffset = -421f; // Vertical shift from Original to Kopie section

        // Personal Information Section
        public static readonly FieldLayout Name = Original.Name.WithYOffset(YOffset);
        public static readonly FieldLayout Vorname = Original.Vorname.WithYOffset(YOffset);
        public static readonly FieldLayout Adresse = Original.Adresse.WithYOffset(YOffset);
        public static readonly FieldLayout Abteilung = Original.Abteilung.WithYOffset(YOffset);
        public static readonly FieldLayout Personalnummer = Original.Personalnummer.WithYOffset(YOffset);

        // Request Date Range Section
        public static readonly FieldLayout StartDatum = Original.StartDatum.WithYOffset(YOffset);
        public static readonly FieldLayout Enddatum = Original.Enddatum.WithYOffset(YOffset);

        // Vacation Calculation Section
        public static readonly FieldLayout GesamtVerfuegbarerUrlaub = Original.GesamtVerfuegbarerUrlaub.WithYOffset(YOffset);
        public static readonly FieldLayout BereitsErhaltenerUrlaub = Original.BereitsErhaltenerUrlaub.WithYOffset(YOffset);
        public static readonly FieldLayout MitDiesemAntragBeantragt = Original.MitDiesemAntragBeantragt.WithYOffset(YOffset);
        public static readonly FieldLayout Resturlaub = Original.Resturlaub.WithYOffset(YOffset);
        public static readonly FieldLayout AnzahlHalbtage = Original.AnzahlHalbtage.WithYOffset(YOffset);

        // AZA-Tage multiline section
        public static readonly MultilineFieldLayout AzaTage = Original.AzaTage.WithYOffset(YOffset);

        // Signature and Date Section
        public static readonly FieldLayout AntragsDatum = Original.AntragsDatum.WithYOffset(YOffset);
        public static readonly FieldLayout UnterschriftPlatzhalter = Original.UnterschriftPlatzhalter.WithYOffset(YOffset);

        // Administrative fields
        public static readonly FieldLayout Genehmigt = Original.Genehmigt.WithYOffset(YOffset);
        public static readonly FieldLayout Bearbeitet = Original.Bearbeitet.WithYOffset(YOffset);
        public static readonly FieldLayout Personalabteilung = Original.Personalabteilung.WithYOffset(YOffset);
        public static readonly FieldLayout AblehnungGrund = Original.AblehnungGrund.WithYOffset(YOffset);
    }

    /// <summary>
    /// Text alignment options for field rendering.
    /// </summary>
    public enum TextAlignment
    {
        Left,
        Right,
        Center
    }

    /// <summary>
    /// Layout definition for a single-line text field.
    /// Coordinates use PDF coordinate system with baseline Y positioning.
    /// </summary>
    public readonly record struct FieldLayout(
        float X,           // Horizontal position (baseline start for Left, end for Right)
        float Y,           // Vertical baseline position
        float MaxWidth,    // Maximum width before clipping/shrinking
        float FontSize,    // Initial font size (may shrink if text too wide)
        TextAlignment Alignment
    )
    {
        /// <summary>
        /// Creates a new FieldLayout with Y coordinate offset (for Kopie section).
        /// </summary>
        public FieldLayout WithYOffset(float offset) => this with { Y = Y + offset };
    }

    /// <summary>
    /// Layout definition for a multiline text field.
    /// Used for sections like AZA-Tage that may span multiple lines.
    /// </summary>
    public readonly record struct MultilineFieldLayout(
        float X,           // Left edge X position
        float TopY,        // Top line baseline Y position
        float MaxWidth,    // Maximum width per line
        float LineHeight,  // Vertical spacing between lines
        int MaxLines,      // Maximum number of lines to render
        float FontSize,    // Font size for all lines
        TextAlignment Alignment
    )
    {
        /// <summary>
        /// Creates a new MultilineFieldLayout with Y coordinate offset (for Kopie section).
        /// </summary>
        public MultilineFieldLayout WithYOffset(float offset) => this with { TopY = TopY + offset };
    }
}
