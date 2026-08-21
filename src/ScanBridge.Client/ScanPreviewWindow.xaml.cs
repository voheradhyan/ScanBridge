using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScanBridge.Client;

/// <summary>
/// Shows the page a test scan produced, from memory, and forgets it on close.
///
/// The point of a test scan is the one question a byte count cannot answer: is the image right?
/// A page can arrive at exactly the expected size, with correct geometry and no error anywhere,
/// and be entirely the wrong colour - that happened here, and only looking at it caught it.
///
/// Nothing is written to disk at any point. The bytes arrive, are decoded once into a bitmap,
/// and both are dropped when this window closes. A diagnostic has no business leaving somebody's
/// document in a folder for whoever uses the PC next.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ScanPreviewWindow : Window
{
    public ScanPreviewWindow(byte[] page, string encoding, int bytes)
    {
        InitializeComponent();

        BitmapImage? image = Decode(page);

        if (image is null)
        {
            Detail.Text = $"{encoding}, {bytes / 1024:N0} KB — this format cannot be previewed here. " +
                          "The scan itself succeeded.";
            return;
        }

        Page.Source = image;
        Detail.Text = $"{image.PixelWidth} × {image.PixelHeight} px  ·  {encoding}  ·  {bytes / 1024:N0} KB";
    }

    /// <summary>
    /// Decodes to a bitmap that does not keep the stream.
    ///
    /// OnLoad rather than the default OnDemand: without it the BitmapImage holds the stream and
    /// decodes lazily, so disposing the MemoryStream below would leave an image that throws the
    /// first time it is drawn - and the point here is that the bytes do not outlive this call.
    /// </summary>
    private static BitmapImage? Decode(byte[] page)
    {
        try
        {
            using var stream = new MemoryStream(page, writable: false);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // A raw or TIFF page WPF will not decode. Not a failure of the scan, and not worth
            // an error dialog: the header above says what happened.
            return null;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // Explicit, so it is obvious this is deliberate rather than left to the collector.
        Page.Source = null;
        base.OnClosed(e);
    }
}
