using System.IO;
using System.Windows.Media.Imaging;
using DeskCal.Ai.Intent;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;

namespace DeskCal.App;

/// <summary>Why a picture could not be read (the message is for the user).</summary>
internal sealed class OcrException(string message) : Exception(message);

/// <param name="Text">The text, after <see cref="OcrTextCleaner"/>.</param>
/// <param name="Raw">What the engine returned.</param>
internal sealed record OcrReading(string Text, string Raw, string Language, int Lines, TimeSpan Elapsed);

/// <summary>
/// Reads the text in a picture with the OCR engine that comes with Windows: it works offline, needs no model and sends nothing anywhere.
/// Korean needs the Korean language pack (Settings > Time &amp; language). Reading is done on this computer only and the picture is never kept.
/// </summary>
internal static class OcrReader
{
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>The engine for Korean, else for the user's own languages; null when Windows has neither.</summary>
    private static OcrEngine? Engine(out string language)
    {
        var ko = OcrEngine.TryCreateFromLanguage(new Language("ko"));
        if (ko is not null) { language = "ko"; return ko; }
        var any = OcrEngine.TryCreateFromUserProfileLanguages();
        language = any?.RecognizerLanguage.LanguageTag ?? "";
        return any;
    }

    /// <summary>True when a Korean engine exists (otherwise the message says what to install).</summary>
    public static bool HasKorean(out string message)
    {
        bool ok = OcrEngine.TryCreateFromLanguage(new Language("ko")) is not null;
        message = ok ? "" : "한국어 OCR 을 쓰려면 Windows 의 한국어 언어 팩이 필요합니다 (설정 → 시간 및 언어 → 언어 및 지역 → 한국어 → 언어 옵션).";
        return ok;
    }

    public static async Task<OcrReading> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new OcrException("파일을 찾을 수 없습니다.");
        if (info.Length > MaxFileBytes) throw new OcrException($"이미지가 너무 큽니다 (최대 {MaxFileBytes / 1024 / 1024} MB).");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ReadAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>A picture from the clipboard (or any WPF bitmap).</summary>
    public static async Task<OcrReading> ReadBitmapAsync(BitmapSource bitmap, CancellationToken ct = default)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(WpfBitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        if (ms.Length > MaxFileBytes) throw new OcrException($"이미지가 너무 큽니다 (최대 {MaxFileBytes / 1024 / 1024} MB).");
        ms.Position = 0;
        return await ReadAsync(ms, ct).ConfigureAwait(false);
    }

    public static async Task<OcrReading> ReadAsync(Stream image, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var engine = Engine(out string language)
            ?? throw new OcrException("이 컴퓨터에는 사용할 수 있는 OCR 엔진이 없습니다. Windows 의 한국어 언어 팩을 설치하세요 (설정 → 시간 및 언어 → 언어 및 지역).");
        try
        {
            using var ras = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(image);
            var decoder = await WinBitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);
            uint max = OcrEngine.MaxImageDimension;
            SoftwareBitmap bitmap;
            if (decoder.PixelWidth > max || decoder.PixelHeight > max)
            {
                double scale = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);        // too large for the engine: shrink it
                var transform = new BitmapTransform { ScaledWidth = (uint)(decoder.PixelWidth * scale), ScaledHeight = (uint)(decoder.PixelHeight * scale) };
                bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
            }
            else bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct).ConfigureAwait(false);

            var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            string raw = string.Join("\n", result.Lines.Select(l => l.Text));
            return new OcrReading(OcrTextCleaner.Clean(raw), raw, language, result.Lines.Count, DateTime.UtcNow - started);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OcrException)
        {
            throw new OcrException("이미지를 읽지 못했습니다. PNG·JPG·BMP 같은 그림 파일인지 확인하세요. (" + ex.Message + ")");
        }
    }
}
