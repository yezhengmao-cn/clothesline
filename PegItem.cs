using System.ComponentModel;
using System.Windows.Media;

namespace Clothesline;

public enum PegType { Image, Text }

/// <summary>绳子上的一张"晾着"的截图或文字片段。</summary>
public sealed class PegItem : INotifyPropertyChanged
{
    public PegType Type { get; }
    public string FilePath { get; }       // 图片时用
    public string? Text { get; }          // 文字时用
    public ImageSource Bitmap { get; }    // 渲染用的缩略图（文字也会渲染成图显示）
    public double Angle { get; }

    public RotateTransform Rot { get; }
    public ScaleTransform Scale { get; }
    public TransformGroup Xf { get; }

    private bool _copyFeedback;
    public bool CopyFeedback
    {
        get => _copyFeedback;
        set
        {
            if (_copyFeedback == value) return;
            _copyFeedback = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CopyFeedback)));
        }
    }

    public PegItem(string filePath, ImageSource bitmap, double angle)
    {
        Type = PegType.Image;
        FilePath = filePath;
        Bitmap = bitmap;
        Angle = angle;
        Rot = new RotateTransform(angle);
        Scale = new ScaleTransform(1, 1);
        Xf = new TransformGroup();
        Xf.Children.Add(Rot);
        Xf.Children.Add(Scale);
    }

    public PegItem(string text, double angle)
    {
        Type = PegType.Text;
        Text = text;
        Bitmap = null!; // 文字卡片不需要图片
        Angle = angle;
        Rot = new RotateTransform(angle);
        Scale = new ScaleTransform(1, 1);
        Xf = new TransformGroup();
        Xf.Children.Add(Rot);
        Xf.Children.Add(Scale);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
