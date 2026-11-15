namespace Kazo.Ai.Http;

/// <summary>
/// Why a request to an AI service failed. The names are the usual families of errors of model
/// APIs, so that a race can say why every model failed (<see cref="Race.ModelRace"/>).
/// </summary>
public enum AiErrorKind
{
    /// <summary>Anything else.</summary>
    Unknown,

    /// <summary>The API key is missing, wrong or refused (401, 403).</summary>
    Auth,

    /// <summary>The account has no credit left (402).</summary>
    Credits,

    /// <summary>The model or the address does not exist (404).</summary>
    NotFound,

    /// <summary>Too many requests (429).</summary>
    RateLimit,

    /// <summary>The service took too long (408, 504, or the app's own time limit).</summary>
    Timeout,

    /// <summary>No answer at all: network down, address refused by the browser (CORS, mixed content).</summary>
    Network,

    /// <summary>The service failed (5xx).</summary>
    Server,

    /// <summary>The app may not reach the real network (the network.real.access capability was refused).</summary>
    NotAllowed,

    /// <summary>The local model cannot run here (no WebGPU) or is not loaded.</summary>
    Unavailable,
}

/// <summary>A request to an AI service failed.</summary>
public sealed class AiServiceException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="kind">Why it failed.</param>
    /// <param name="message">What the service or the browser said.</param>
    /// <param name="status">The HTTP status, or 0 without an answer.</param>
    /// <param name="innerException">The cause.</param>
    public AiServiceException(AiErrorKind kind, string message, int status = 0, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Status = status;
    }

    /// <summary>Gets why it failed.</summary>
    public AiErrorKind Kind { get; }

    /// <summary>Gets the HTTP status, or 0.</summary>
    public int Status { get; }

    /// <summary>The kind of error of an HTTP status.</summary>
    /// <param name="status">The status.</param>
    /// <returns>Its kind.</returns>
    public static AiErrorKind KindOf(int status) => status switch
    {
        401 or 403 => AiErrorKind.Auth,
        402 => AiErrorKind.Credits,
        404 => AiErrorKind.NotFound,
        408 or 504 => AiErrorKind.Timeout,
        429 => AiErrorKind.RateLimit,
        >= 500 => AiErrorKind.Server,
        _ => AiErrorKind.Unknown,
    };
}
