using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Bulles;

/// <summary>
/// GIF animé pour WPF (qui n'affiche que la première image d'un GIF) : chaque image est recomposée
/// à pleine taille, puis jouée en boucle avec ses délais d'origine.
/// </summary>
public sealed class GifAnimation
{
    private readonly List<BitmapSource> _frames = new();
    private readonly List<TimeSpan> _delays = new();

    /// <param name="maxSize">Taille maxi des images gardées en mémoire (pixels), le GIF est réduit si besoin.</param>
    public static GifAnimation? Load(string path, double maxSize = 512)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var gif = new GifAnimation();
            int width = Query(decoder.Metadata, "/logscrdesc/Width", decoder.Frames[0].PixelWidth);
            int height = Query(decoder.Metadata, "/logscrdesc/Height", decoder.Frames[0].PixelHeight);

            double scale = Math.Min(1, maxSize / Math.Max(width, height));
            int outW = Math.Max(1, (int)Math.Round(width * scale)), outH = Math.Max(1, (int)Math.Round(height * scale));

            // Recomposition : une image de GIF peut ne contenir qu'une partie de l'image (à sa position).
            BitmapSource? previous = null;
            foreach (var frame in decoder.Frames)
            {
                var meta = frame.Metadata as BitmapMetadata;
                int left = Query(meta, "/imgdesc/Left", 0), top = Query(meta, "/imgdesc/Top", 0);
                int delay = Query(meta, "/grctlext/Delay", 10);
                int disposal = Query(meta, "/grctlext/Disposal", 0);

                var visual = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                using (var dc = visual.RenderOpen())
                {
                    if (previous != null) dc.DrawImage(previous, new Rect(0, 0, outW, outH));
                    dc.DrawImage(frame, new Rect(left * scale, top * scale, frame.PixelWidth * scale, frame.PixelHeight * scale));
                }
                var composed = new RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
                composed.Render(visual);
                composed.Freeze();
                gif._frames.Add(composed);
                gif._delays.Add(TimeSpan.FromMilliseconds(Math.Max(20, delay * 10)));
                // Disposal 2 = on repart d'un fond vide pour l'image suivante.
                previous = disposal == 2 ? null : composed;
            }
            return gif;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    private static int Query(ImageMetadata? metadata, string query, int fallback)
    {
        try
        {
            return metadata is BitmapMetadata m && m.GetQuery(query) is { } v ? Convert.ToInt32(v) : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Lance l'animation en boucle sur une Image (ou l'arrête si play = false).</summary>
    public void Play(Image image, bool play)
    {
        if (!play || _frames.Count == 0)
        {
            image.BeginAnimation(Image.SourceProperty, null);
            return;
        }
        image.Source = _frames[0];
        if (_frames.Count == 1) return;
        var animation = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        var time = TimeSpan.Zero;
        for (int i = 0; i < _frames.Count; i++)
        {
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(_frames[i], KeyTime.FromTimeSpan(time)));
            time += _delays[i];
        }
        animation.Duration = time;
        image.BeginAnimation(Image.SourceProperty, animation);
    }
}
