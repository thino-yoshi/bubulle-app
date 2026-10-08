using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Bulles;

public partial class App : Application
{
    public const string StartupArgument = "--startup";

    private Mutex? _mutex;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "Bubulle.SingleInstance", out bool first);
        if (!first)
        {
            // Lancé automatiquement alors que Bulles tourne déjà : on s'arrête sans message.
            if (Array.IndexOf(e.Args, StartupArgument) < 0)
                MessageBox.Show("Bubulle est déjà lancé (icône près de l'horloge).", "Bubulle");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        // Une mise à jour téléchargée la dernière fois ? On l'installe avant de démarrer (Bubulle se relance tout seul).
        Updater.FindPending();
        if (Updater.Ready != null && Updater.StartInstall())
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

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
