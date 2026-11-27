using System.Runtime.InteropServices;
using FileBridge.Cli;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
    context => { context.Cancel = true; stop.Cancel(); });
return await CliApp.RunAsync(args, Console.Out, Console.Error, stop.Token);
