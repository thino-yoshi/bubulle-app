using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace Bulles;

/// <summary>
/// Volume d'une app Windows dans le mélangeur de Windows (comme « Mélangeur de volume »).
/// Une app peut avoir plusieurs sessions audio (Chrome joue le son depuis un processus à part,
/// mais du même nom) : on règle toutes celles du programme, sur toutes les sorties audio.
/// </summary>
public static class AppAudio
{
    private static int _pending = -1;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Applique le volume (0-100) en arrière-plan ; pendant un glissé, seule la dernière valeur compte.</summary>
    public static void SetVolumeAsync(string processName, int percent)
    {
        if (string.IsNullOrEmpty(processName)) return;
        Interlocked.Exchange(ref _pending, percent);
        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync();
            try
            {
                int value = Interlocked.Exchange(ref _pending, -1);
                if (value >= 0) Apply(processName, value / 100f);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
            finally
            {
                Gate.Release();
            }
        });
    }

    private static void Apply(string processName, float volume)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (IsProcess(session.GetProcessID, processName))
                        session.SimpleAudioVolume.Volume = volume;
                }
            }
        }
    }

    private static bool IsProcess(uint pid, string processName)
    {
        if (pid == 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
