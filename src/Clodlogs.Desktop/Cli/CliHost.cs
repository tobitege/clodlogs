using System.Runtime.InteropServices;
using System.Text;
using Clodlogs.Desktop.Services;

namespace Clodlogs.Desktop.Cli;

/// <summary>Runs the command line from <c>Main</c>, with the console of the shell that started the app.</summary>
internal static class CliHost
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;

    public static int Run(string[] args)
    {
        AttachToParentConsole();
        var output = CreateWriter(Console.IsOutputRedirected, Console.OpenStandardOutput);
        var error = CreateWriter(Console.IsErrorRedirected, Console.OpenStandardError);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var cli = new ClodlogsCli(
                new ClaudeSessionService(),
                output,
                error,
                Environment.CurrentDirectory,
                () => DateTimeOffset.Now,
                TimeZoneInfo.Local);
            return cli.RunAsync(args, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            error.WriteLine($"clodlogs: {ex.Message}");
            return ClodlogsCli.ExitFailure;
        }
        finally
        {
            output.Flush();
            error.Flush();
        }
    }

    // Redirected output (a pipe or a file) is written as UTF-8, so titles and paths survive any code page.
    private static TextWriter CreateWriter(bool redirected, Func<Stream> open)
        => redirected ? new StreamWriter(open(), new UTF8Encoding(false)) { AutoFlush = true } : Console.Out;

    /// <summary>
    /// The app is a Windows GUI program, so a shell gives it no console. Without redirected output
    /// it attaches to the console of the shell, so <c>clodlogs list</c> prints there.
    /// </summary>
    private static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outputMissing = !IsUsable(GetStdHandle(StdOutputHandle));
        var errorMissing = !IsUsable(GetStdHandle(StdErrorHandle));
        if (!outputMissing && !errorMissing || !AttachConsole(AttachParentProcess))
        {
            return;
        }

        var console = CreateFileW("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!IsUsable(console))
        {
            return;
        }

        if (outputMissing) SetStdHandle(StdOutputHandle, console);
        if (errorMissing) SetStdHandle(StdErrorHandle, console);
    }

    private static bool IsUsable(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int handle, IntPtr value);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
