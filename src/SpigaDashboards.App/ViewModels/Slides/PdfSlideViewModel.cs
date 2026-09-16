using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace SpigaDashboards.App.ViewModels.Slides;

// Slide 8: an admin-selected external PDF (consignes, instructions,
// documents qualite). Renders page 1 only -- multi-page cycling isn't in the
// spec and would add complexity for no confirmed need.
public partial class PdfSlideViewModel : ObservableObject
{
    [ObservableProperty] private BitmapImage? pageImage;
    [ObservableProperty] private string errorMessage = "";

    public static Task<PdfSlideViewModel> LoadAsync(string pdfPath) => Task.Run(() =>
    {
        var slide = new PdfSlideViewModel();

        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            slide.ErrorMessage = "Document introuvable. Configurez le chemin dans Parametres.";
            return slide;
        }

        // Cheap sanity check before handing the file to the native PDF renderer:
        // a file that isn't actually a PDF (wrong extension, corrupted download,
        // 0 bytes) can crash the whole process at the native layer in a way a
        // C# try/catch below can't reliably protect against.
        if (!LooksLikePdf(pdfPath))
        {
            slide.ErrorMessage = "Le fichier configure ne semble pas etre un PDF valide.";
            return slide;
        }

        try
        {
            using var pdfStream = File.OpenRead(pdfPath);
            using var bitmap = PDFtoImage.Conversion.ToImage(pdfStream, page: 0);
            using var pngData = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var pngStream = new MemoryStream(pngData.ToArray());

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = pngStream;
            image.EndInit();
            image.Freeze();

            slide.PageImage = image;
        }
        catch (Exception ex)
        {
            slide.ErrorMessage = $"Impossible d'afficher le document : {ex.Message}";
        }

        return slide;
    });

    private static bool LooksLikePdf(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[5];
            using var stream = File.OpenRead(path);
            if (stream.Length < header.Length)
                return false;
            var read = stream.Read(header);
            return read == header.Length && header.SequenceEqual("%PDF-"u8);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
