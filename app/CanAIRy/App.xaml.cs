namespace CanAIRy;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        ThemeService.Start();
        base.OnStartup(e);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        ThemeService.Stop();
        base.OnExit(e);
    }
}
