using System.Net;

namespace OverTranslate.Translation.OpenAi;

/// <summary>How an OpenAI-compatible server failed to give a translation.</summary>
public enum OpenAiChatFailure
{
    /// <summary>The server answered with an error status.</summary>
    Rejected,

    /// <summary>The answer was not a chat completion.</summary>
    Unparsable,

    /// <summary>The answer was empty once any thinking block was taken out.</summary>
    NoTranslation,
}

/// <summary>
/// An OpenAI-compatible server answered, but not with a translation.
/// </summary>
/// <remarks>
/// Its own type rather than <see cref="TranslationEngineException"/>, because the application shows
/// each of these to the user in their own words — a server's error message is often the only thing
/// that says which setting is wrong — so it needs the kind and the server's words apart, not one
/// message with both in it. The message here is for logs and never contains the text that was sent.
/// </remarks>
public sealed class OpenAiChatException(
    OpenAiChatFailure failure,
    HttpStatusCode? statusCode = null,
    string? serverMessage = null,
    Exception? inner = null)
    : Exception(statusCode is null ? $"OpenAiChat: {failure}" : $"OpenAiChat: {failure} ({(int)statusCode})", inner)
{
    public OpenAiChatFailure Failure { get; } = failure;

    /// <summary>The HTTP status, for <see cref="OpenAiChatFailure.Rejected"/>.</summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>
    /// For <see cref="OpenAiChatFailure.Rejected"/>: the server's <c>error.message</c>, or the body
    /// itself (cut at 300 characters) when it had none — empty when the body was. Null when the
    /// server sent an <c>error.message</c> that was null.
    /// </summary>
    public string? ServerMessage { get; } = serverMessage;
}
