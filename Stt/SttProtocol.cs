using System.Text.Json;

namespace GBFR.ChatOverlay.Stt;

public static class SttMessageTypes
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Cancel = "cancel";
    public const string Shutdown = "shutdown";

    public const string Ready = "ready";
    public const string Recording = "recording";
    public const string Transcribing = "transcribing";
    public const string Result = "result";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
}

public sealed record SttCommand(string Type, long RequestId = 0);

public sealed record SttEvent(
    string Type,
    long RequestId = 0,
    string? Text = null,
    string? Error = null,
    long ElapsedMilliseconds = 0);

/// <summary>
/// Version-one JSON-lines protocol shared by the injected mod and the isolated STT worker.
/// Each message is exactly one bounded UTF-8 line so diagnostics can remain on stderr.
/// </summary>
public static class SttProtocol
{
    public const int Version = 1;
    public const int MaximumMessageCharacters = 16_384;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(SttCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        return JsonSerializer.Serialize(command, SerializerOptions);
    }

    public static string Serialize(SttEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateEvent(message);
        return JsonSerializer.Serialize(message, SerializerOptions);
    }

    public static bool TryParseCommand(string? line, out SttCommand? command, out string? error)
    {
        command = null;
        if (!TryValidateLine(line, out error))
            return false;

        try
        {
            command = JsonSerializer.Deserialize<SttCommand>(line!, SerializerOptions);
            if (command is null)
                throw new JsonException("The command was empty.");
            ValidateCommand(command);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            command = null;
            error = exception.Message;
            return false;
        }
    }

    public static bool TryParseEvent(string? line, out SttEvent? message, out string? error)
    {
        message = null;
        if (!TryValidateLine(line, out error))
            return false;

        try
        {
            message = JsonSerializer.Deserialize<SttEvent>(line!, SerializerOptions);
            if (message is null)
                throw new JsonException("The event was empty.");
            ValidateEvent(message);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            message = null;
            error = exception.Message;
            return false;
        }
    }

    private static bool TryValidateLine(string? line, out string? error)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            error = "The protocol line is empty.";
            return false;
        }

        if (line.Length > MaximumMessageCharacters)
        {
            error = $"The protocol line exceeds {MaximumMessageCharacters} characters.";
            return false;
        }

        if (line.Contains('\r') || line.Contains('\n'))
        {
            error = "A protocol message cannot contain a line break.";
            return false;
        }

        error = null;
        return true;
    }

    private static void ValidateCommand(SttCommand command)
    {
        var requiresRequest = command.Type is
            SttMessageTypes.Start or
            SttMessageTypes.Stop or
            SttMessageTypes.Cancel;

        if (!requiresRequest && command.Type is not SttMessageTypes.Shutdown)
            throw new ArgumentException($"Unknown STT command type '{command.Type}'.", nameof(command));
        if (requiresRequest && command.RequestId <= 0)
            throw new ArgumentException("The STT command requires a positive request id.", nameof(command));
        if (command.Type is SttMessageTypes.Shutdown && command.RequestId != 0)
            throw new ArgumentException("The shutdown command cannot have a request id.", nameof(command));
    }

    private static void ValidateEvent(SttEvent message)
    {
        var requiresRequest = message.Type is
            SttMessageTypes.Recording or
            SttMessageTypes.Transcribing or
            SttMessageTypes.Result or
            SttMessageTypes.Cancelled;
        var knownType = requiresRequest || message.Type is SttMessageTypes.Ready or SttMessageTypes.Error;

        if (!knownType)
            throw new ArgumentException($"Unknown STT event type '{message.Type}'.", nameof(message));
        if (requiresRequest && message.RequestId <= 0)
            throw new ArgumentException("The STT event requires a positive request id.", nameof(message));
        if (message.Type is SttMessageTypes.Ready && message.RequestId != 0)
            throw new ArgumentException("The ready event cannot have a request id.", nameof(message));
        if (message.RequestId < 0)
            throw new ArgumentException("The STT event request id cannot be negative.", nameof(message));
    }
}
