using Avalonia;

namespace FileBridge.Desktop;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--filebridge-funnel", var executable, var publicPort, var localPort]
            && int.TryParse(publicPort, out var port) && int.TryParse(localPort, out var target))
        {
            try { Environment.ExitCode = TailscaleFunnel.RunHelperAsync(executable, port, target).GetAwaiter().GetResult(); }
            catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
            return;
        }
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
