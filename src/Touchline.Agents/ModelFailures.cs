using System.ClientModel;
using System.Net;
using Microsoft.Extensions.AI;

namespace Touchline.Agents;

/// <summary>What kind of model failure an exception is, which decides whether a retry is worth paying for (ADR-052).</summary>
public enum ModelFailureKind
{
    /// <summary>Retrying won't help: a bad request, an unreadable reply, a budget stop, or anything unrecognised.</summary>
    Permanent,

    /// <summary>The model may answer if asked again later: throttled (429), a server error (5xx), or a timeout.</summary>
    Transient,

    /// <summary>The deployment's content filter blocked the prompt or the completion (Prompt Shields or a harm category).</summary>
    ContentFilter
}

/// <summary>
/// Classifies model-call failures (ADR-052) and names the fallback reason the squad's metrics
/// record for them (ADR-044).
/// </summary>
/// <remarks>
/// Azure OpenAI reports a blocked prompt as HTTP 400 with the error code <c>content_filter</c>, which
/// the OpenAI SDK raises as a <see cref="ClientResultException"/>. A blocked completion comes back as
/// a normal reply whose finish reason is <see cref="ChatFinishReason.ContentFilter"/>; the agents turn
/// that into a <see cref="ModelContentFilteredException"/> with <see cref="ThrowIfFiltered"/>.
/// </remarks>
public static class ModelFailures
{
    /// <summary>The fallback reason for a call the content filter blocked.</summary>
    public const string ContentFilterReason = "content_filter";

    /// <summary>The fallback reason for any other failed call.</summary>
    public const string FailedReason = "failed";

    /// <summary>The error code Azure OpenAI gives a filtered request.</summary>
    public const string ContentFilterCode = "content_filter";

    /// <summary>Classifies a failed model call.</summary>
    /// <param name="exception">What the call threw.</param>
    /// <param name="hostCancellation">
    /// The caller's token. A cancellation it asked for isn't a failure at all; any other
    /// cancellation is the HTTP client's timeout, so it's transient.
    /// </param>
    /// <returns>The kind of failure.</returns>
    public static ModelFailureKind Classify(Exception exception, CancellationToken hostCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ModelContentFilteredException:
                    return ModelFailureKind.ContentFilter;
                case ClientResultException result when IsFilteredRequest(result):
                    return ModelFailureKind.ContentFilter;
                case ClientResultException result:
                    return IsTransientStatus(result.Status) ? ModelFailureKind.Transient : ModelFailureKind.Permanent;
                case HttpRequestException { StatusCode: { } status }:
                    return IsTransientStatus((int)status) ? ModelFailureKind.Transient : ModelFailureKind.Permanent;
                case TimeoutException:
                    return ModelFailureKind.Transient;
                case OperationCanceledException when !hostCancellation.IsCancellationRequested:
                    return ModelFailureKind.Transient;
            }
        }

        return ModelFailureKind.Permanent;
    }

    /// <summary>The fallback reason to record for a failed call: <see cref="ContentFilterReason"/> or <see cref="FailedReason"/>.</summary>
    /// <param name="exception">What the call threw.</param>
    /// <returns>The reason tag's value.</returns>
    public static string FallbackReason(Exception exception) =>
        Classify(exception) == ModelFailureKind.ContentFilter ? ContentFilterReason : FailedReason;

    /// <summary>Throws <see cref="ModelContentFilteredException"/> when the content filter cut the completion off.</summary>
    /// <param name="response">The model's reply.</param>
    /// <exception cref="ModelContentFilteredException">The finish reason is <see cref="ChatFinishReason.ContentFilter"/>.</exception>
    public static void ThrowIfFiltered(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.FinishReason == ChatFinishReason.ContentFilter)
        {
            throw new ModelContentFilteredException("The content filter blocked the model's reply.");
        }
    }

    private static bool IsTransientStatus(int status) =>
        status is (int)HttpStatusCode.TooManyRequests or (int)HttpStatusCode.RequestTimeout or >= 500 and < 600;

    private static bool IsFilteredRequest(ClientResultException exception)
    {
        if (exception.Status != (int)HttpStatusCode.BadRequest && exception.Status != 0)
        {
            return false;
        }

        if (exception.Message.Contains(ContentFilterCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return exception.GetRawResponse()?.Content.ToString().Contains(ContentFilterCode, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (InvalidOperationException)
        {
            // The body wasn't buffered, so only the message can say.
            return false;
        }
    }
}

/// <summary>The content filter blocked a model's completion (ADR-044).</summary>
public sealed class ModelContentFilteredException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ModelContentFilteredException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public ModelContentFilteredException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ModelContentFilteredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
