namespace QuiVrRevival.Server;

/// <summary>An error reported to the game in Parse's format: HTTP status + {"code":..,"error":..}.</summary>
public sealed class ParseError(int code, string message, int status = 400) : Exception(message)
{
    public int Code { get; } = code;
    public int Status { get; } = status;

    public const int OtherCause = -1;
    public const int ObjectNotFound = 101;
    public const int InvalidQuery = 102;
    public const int InvalidClassName = 103;
    public const int InvalidJson = 107;
    public const int OperationForbidden = 119;
    public const int ScriptFailed = 141;
    public const int UsernameMissing = 200;
    public const int PasswordMissing = 201;
    public const int UsernameTaken = 202;
    public const int InvalidSessionToken = 209;

    public static ParseError NotFound() => new(ObjectNotFound, "Object not found.", 404);
    public static ParseError Forbidden(string msg = "Permission denied") => new(OperationForbidden, msg, 403);
}
