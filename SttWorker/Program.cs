using System.Security.Cryptography;
using System.Text;
using GBFR.ChatOverlay.Stt;

namespace GBFR.ChatOverlay.SttWorker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var protocol = new ProtocolWriter();

        try
        {
            var options = WorkerOptions.Parse(args);
            await ValidateRuntimeAsync(options).ConfigureAwait(false);
            await using var host = new SttWorkerHost(options, protocol);

            protocol.Write(new SttEvent(SttMessageTypes.Ready));
            while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!SttProtocol.TryParseCommand(line, out var command, out var error))
                {
                    protocol.Write(new SttEvent(SttMessageTypes.Error, Error: error));
                    continue;
                }

                if (command!.Type is SttMessageTypes.Shutdown)
                {
                    await host.ShutdownAsync().ConfigureAwait(false);
                    break;
                }

                await host.HandleAsync(command).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            try
            {
                protocol.Write(new SttEvent(SttMessageTypes.Error, Error: exception.Message));
            }
            catch
            {
            }
            return 1;
        }
    }

    private static async Task ValidateRuntimeAsync(WorkerOptions options)
    {
        if (!File.Exists(options.WhisperExecutable))
            throw new FileNotFoundException("whisper-cli.exe was not found.", options.WhisperExecutable);
        if (!File.Exists(options.ModelFile))
            throw new FileNotFoundException("The Whisper base model was not found.", options.ModelFile);

        await using var stream = File.OpenRead(options.ModelFile);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
        if (!actualHash.Equals(options.ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The Whisper model hash is invalid. Expected {options.ModelSha256}, got {actualHash}.");
        }
    }
}
