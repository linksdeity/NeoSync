using System.IO.Pipes;

namespace NeoSync.Core.Ipc;

/// <summary>Synchronous client for the NeoSyncHelper local service (named pipe).</summary>
public static class NeoSyncHelperClient
{
    private const string PipeName = "NeoSyncHelper";

    /// <summary>
    /// Sends a single command line and returns the response line.
    /// Returns "OK" on success, "FAIL:&lt;reason&gt;" for known errors,
    /// or "ERROR:&lt;message&gt;" if the service is unreachable.
    /// </summary>
    public static string Send(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName,
                PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(5_000);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(command);
            return reader.ReadLine() ?? "FAIL:empty response from service";
        }
        catch (TimeoutException)
        {
            return "ERROR:NeoSyncHelper service not responding - please reinstall NeoSync.";
        }
        catch (Exception ex)
        {
            return $"ERROR:{ex.Message}";
        }
    }
}
