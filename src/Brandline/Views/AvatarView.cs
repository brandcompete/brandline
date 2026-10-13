using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brandline.Services;

namespace Brandline.Views;

/// <summary>Runder Avatar: Bild, sonst Initialen auf farbigem Kreis, sonst Personensymbol.</summary>
public sealed class AvatarView : FrameworkElement
{
    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(AvatarView), new PropertyMetadata("", (d, _) => ((AvatarView)d).Update()));

    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image), typeof(ImageSource), typeof(AvatarView), new PropertyMetadata(null, (d, _) => ((AvatarView)d).Update()));

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(AvatarView), new PropertyMetadata(36.0, (d, _) => ((AvatarView)d).Update()));

    private readonly Grid _root = new();
    private readonly System.Windows.Shapes.Ellipse _circle = new();
    private readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.SemiBold,
    };

    public AvatarView()
    {
        _root.Children.Add(_circle);
        _root.Children.Add(_text);
        AddVisualChild(_root);
        Focusable = false;
        Update();
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    private void Update()
    {
        Width = Height = _circle.Width = _circle.Height = Diameter;
        if (Image != null)
        {
            _circle.Fill = new ImageBrush(Image) { Stretch = Stretch.UniformToFill };
            _text.Text = "";
            return;
        }

        var initials = Avatars.Initials(DisplayName ?? "");
        _circle.Fill = Avatars.BrushFor(DisplayName ?? "");
        if (initials.Length > 0)
        {
            _text.FontFamily = SystemFonts.MessageFontFamily;
            _text.FontSize = Diameter * 0.38;
            _text.Text = initials;
        }
        else
        {
            // Nur eine Nummer bekannt → Personensymbol.
            _text.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            _text.FontSize = Diameter * 0.45;
            _text.Text = "";
        }
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _root;

    protected override Size MeasureOverride(Size constraint)
    {
        _root.Measure(new Size(Diameter, Diameter));
        return new Size(Diameter, Diameter);
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _root.Arrange(new Rect(0, 0, Diameter, Diameter));
        return new Size(Diameter, Diameter);
    }
}
