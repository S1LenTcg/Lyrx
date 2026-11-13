using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Musebase.Windows.Services;

public static class TaskbarCommands
{
    public const string Lyrics = "lyrics";

    private const string PipeName = "Lyrx.Commands.v1";
    private const string MutexName = @"Local\Lyrx.SingleInstance.v1";

    private static Mutex? _instanceLock;

    public static bool ClaimInstance(string? command = Lyrics)
    {
        try
        {
            _instanceLock = new Mutex(initiallyOwned: true, MutexName, out var first);
            if (first) return true;
        }
        catch (Exception)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(command)) Send(command!);
        return false;
    }

    private static void Send(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(command);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch (Exception)
        {
        }
    }

    public static void Listen(Action<string> onCommand)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var command = (await reader.ReadToEndAsync()).Trim();
                    if (command.Length > 0) onCommand(command);
                }
                catch (Exception e)
                {
                    Log.Write($"[ipc] 명령 수신 실패: {e.Message}");
                    await Task.Delay(1000);
                }
            }
        });
    }
}
