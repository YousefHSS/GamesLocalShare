using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace GamesLocalShare.Controls;

public class OutlinedText : FrameworkElement
{
    public OutlinedText()
    {
        // Match TextBlock defaults more closely to align glyph metrics
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        // Hint ClearType for consistent glyph rendering
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedText),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner(typeof(OutlinedText),
            new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner(typeof(OutlinedText),
            new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner(typeof(OutlinedText),
            new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(OutlinedText),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(OutlinedText),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(OutlinedText),
            new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (string.IsNullOrEmpty(Text))
            return new Size(0, 0);

        var formatted = CreateFormattedText();
        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };

        // Build geometry positioned at baseline so bounds include ascenders/descenders
        var geom = formatted.BuildGeometry(new Point(0, formatted.Baseline));
        var bounds = geom.GetRenderBounds(pen);

        // Add a 1px safety padding
        double width = Math.Ceiling(bounds.Width) + 1;
        double height = Math.Ceiling(bounds.Height) + 1;

        // Respect available size
        if (!double.IsInfinity(availableSize.Width))
            width = Math.Min(width, availableSize.Width);
        if (!double.IsInfinity(availableSize.Height))
            height = Math.Min(height, availableSize.Height);

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (string.IsNullOrEmpty(Text))
            return;

        var formatted = CreateFormattedText();
        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };

        // Build geometry at baseline origin, then compute render bounds and translate so top-left aligns with control (0,0)
        var geom = formatted.BuildGeometry(new Point(0, formatted.Baseline));
        var bounds = geom.GetRenderBounds(pen);

        var translate = new TranslateTransform(-bounds.X, -bounds.Y);
        geom.Transform = translate;

        // Draw stroke then fill using same geometry for exact overlap
        drawingContext.DrawGeometry(null, pen, geom);
        drawingContext.DrawGeometry(Foreground, null, geom);
    }

    private FormattedText CreateFormattedText()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var ft = new FormattedText(
            Text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal),
            FontSize,
            Foreground,
            dpi.PixelsPerDip);

        ft.TextAlignment = TextAlignment.Left;
        ft.Trimming = TextTrimming.None;

        return ft;
    }
}