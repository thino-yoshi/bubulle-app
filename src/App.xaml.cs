using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace Bulles;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "Bulles.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Bulles est déjà lancé (icône près de l'horloge).", "Bulles");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        _controller = new AppController(AppSettings.Load());
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _controller?.Dispose();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(Path.Combine(AppSettings.Dir, "error.log"), $"[{DateTime.Now:s}] {ex}\n\n");
        }
        catch
        {
            // Rien à faire si même le log échoue.
        }
    }
}
