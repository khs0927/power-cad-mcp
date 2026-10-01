namespace PowerCad.Core;

/// <summary>Stable error codes shared by the plugin, the server and the operating playbook.</summary>
public static class ErrorCodes
{
    public const string InvalidParams = "INVALID_PARAMS";
    public const string UnknownCommand = "UNKNOWN_COMMAND";
    public const string NotFound = "NOT_FOUND";
    public const string DocumentChanged = "DOCUMENT_CHANGED";
    public const string DocumentUnbound = "DOCUMENT_UNBOUND";
    public const string TargetAmbiguous = "TARGET_AMBIGUOUS";
    public const string StaleTarget = "STALE_TARGET";
    public const string LockedLayer = "LOCKED_LAYER";
    public const string Unsupported = "UNSUPPORTED";
    public const string VerifyFailed = "VERIFY_FAILED";
    public const string TooManyMatches = "TOO_MANY_MATCHES";
    public const string NoDocument = "NO_DOCUMENT";
    public const string Busy = "CAD_BUSY";
    public const string Timeout = "TIMEOUT";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string NotConnected = "NOT_CONNECTED";
    public const string ReadOnly = "READ_ONLY";
    public const string Internal = "INTERNAL";
    public const string PluginOutdated = "PLUGIN_OUTDATED";
}

/// <summary>An anticipated failure with a machine-readable code and a hint for the agent's next step.</summary>
public sealed class CadException(string code, string message, string? hint = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Hint { get; } = hint;

    public static CadException Invalid(string message, string? hint = null) =>
        new(ErrorCodes.InvalidParams, message, hint);
}
