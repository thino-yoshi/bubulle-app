using System;
using System.IO;
using System.Windows.Media;

namespace Bulles;

/// <summary>Sons d'interface de Bulles (déploiement et repli des bulles), préchargés pour jouer sans délai.</summary>
public sealed class Sounds
{
    private readonly AppSettings _s;
    private MediaPlayer? _open, _close;

    public static string Dir => Path.Combine(AppSettings.Dir, "sounds");

    public Sounds(AppSettings settings)
    {
        _s = settings;
        Reload();
    }

    /// <summary>Recharge les fichiers et le volume après un changement de paramètres.</summary>
    public void Reload()
    {
        _open = Load(_s.OpenSoundPath);
        _close = Load(_s.CloseSoundPath);
    }

    public void PlayOpen() => Play(_open);
    public void PlayClose() => Play(_close);

    private void Play(MediaPlayer? player)
    {
        if (!_s.SoundEnabled || player == null) return;
        player.Volume = Math.Clamp(_s.SoundVolume, 0, 100) / 100.0;
        player.Stop();
        player.Position = TimeSpan.Zero;
        player.Play();
    }

    private static MediaPlayer? Load(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var player = new MediaPlayer();
        player.Open(new Uri(path));
        return player;
    }

    /// <summary>Copie un son choisi par toi dans le dossier de Bulles (il marche même si l'original est supprimé).</summary>
    public static string Import(string sourcePath, string name)
    {
        Directory.CreateDirectory(Dir);
        var dest = Path.Combine(Dir, name + Path.GetExtension(sourcePath).ToLowerInvariant());
        foreach (var old in Directory.GetFiles(Dir, name + ".*")) File.Delete(old);
        File.Copy(sourcePath, dest, overwrite: true);
        return dest;
    }
}
