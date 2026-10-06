using System.Windows;

namespace ImageViewer;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow();
        window.Show();
        if (e.Args.Length > 0)
            window.OpenFile(e.Args[0]);
    }
}
