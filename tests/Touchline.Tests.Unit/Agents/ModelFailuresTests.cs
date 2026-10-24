using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Touchline.Agents;

namespace Touchline.Tests.Unit.Agents;

/// <summary>Which model failures are worth a paid retry (ADR-052), and which are content-filter blocks (ADR-044).</summary>
[TestFixture]
public class ModelFailuresTests
{
    private const string _filteredBody =
        """{"error":{"code":"content_filter","message":"The prompt was filtered.","innererror":{"code":"ResponsibleAIPolicyViolation","content_filter_result":{"jailbreak":{"filtered":true,"detected":true}}}}}""";

    [TestCase(429)]
    [TestCase(408)]
    [TestCase(500)]
    [TestCase(503)]
    public void A_throttled_or_server_error_response_is_transient(int status) =>
        ModelFailures.Classify(Failure(status)).Should().Be(ModelFailureKind.Transient);

    [TestCase(400)]
    [TestCase(401)]
    [TestCase(403)]
    [TestCase(404)]
    public void A_client_error_response_is_permanent(int status) =>
        ModelFailures.Classify(Failure(status)).Should().Be(ModelFailureKind.Permanent);

    [Test]
    public void A_400_with_the_content_filter_code_is_a_content_filter_block() =>
        ModelFailures.Classify(Failure(400, _filteredBody)).Should().Be(ModelFailureKind.ContentFilter);

    [Test]
    public void A_content_filter_code_in_the_message_alone_is_a_content_filter_block() =>
        ModelFailures.Classify(new ClientResultException("HTTP 400 (invalid_request_error: content_filter)"))
            .Should().Be(ModelFailureKind.ContentFilter);

    [Test]
    public void A_completion_the_filter_cut_off_is_a_content_filter_block()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.ContentFilter };

        var act = () => ModelFailures.ThrowIfFiltered(response);

        act.Should().Throw<ModelContentFilteredException>()
            .Which.Should().Match<Exception>(ex => ModelFailures.Classify(ex, default) == ModelFailureKind.ContentFilter);
    }

    [Test]
    public void A_completed_reply_is_not_a_content_filter_block()
    {
        var act = () => ModelFailures.ThrowIfFiltered(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { FinishReason = ChatFinishReason.Stop });

        act.Should().NotThrow();
    }

    [Test]
    public void A_timeout_that_the_host_did_not_ask_for_is_transient()
    {
        ModelFailures.Classify(new TaskCanceledException("The request timed out."), CancellationToken.None).Should().Be(ModelFailureKind.Transient);
        ModelFailures.Classify(new TimeoutException()).Should().Be(ModelFailureKind.Transient);
    }

    [Test]
    public void A_cancellation_the_host_asked_for_is_not_transient()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        ModelFailures.Classify(new TaskCanceledException(), cancelled.Token).Should().Be(ModelFailureKind.Permanent);
    }

    [Test]
    public void An_http_status_on_a_plain_request_exception_is_classified_like_a_response()
    {
        ModelFailures.Classify(new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable)).Should().Be(ModelFailureKind.Transient);
        ModelFailures.Classify(new HttpRequestException("bad", null, HttpStatusCode.BadRequest)).Should().Be(ModelFailureKind.Permanent);
    }

    [Test]
    public void Anything_unrecognised_is_permanent()
    {
        ModelFailures.Classify(new System.Text.Json.JsonException("not a recap")).Should().Be(ModelFailureKind.Permanent);
        ModelFailures.Classify(new HttpRequestException("Foundry is down.")).Should().Be(ModelFailureKind.Permanent);
    }

    [Test]
    public void A_wrapped_failure_is_classified_by_its_cause() =>
        ModelFailures.Classify(new InvalidOperationException("wrapped", Failure(503))).Should().Be(ModelFailureKind.Transient);

    [Test]
    public void The_fallback_reason_names_a_content_filter_block_and_nothing_else()
    {
        ModelFailures.FallbackReason(Failure(400, _filteredBody)).Should().Be(ModelFailures.ContentFilterReason).And.Be("content_filter");
        ModelFailures.FallbackReason(Failure(503)).Should().Be(ModelFailures.FailedReason);
    }

    /// <summary>A failed Azure OpenAI call, as the OpenAI SDK raises it.</summary>
    internal static ClientResultException Failure(int status, string body = """{"error":{"code":"server_error"}}""") =>
        new(new StubResponse(status, body));

    private sealed class StubResponse(int status, string body) : PipelineResponse
    {
        private readonly BinaryData _content = BinaryData.FromString(body);

        public override int Status => status;

        public override string ReasonPhrase => ((HttpStatusCode)status).ToString();

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => _content;

        protected override PipelineResponseHeaders HeadersCore => new StubHeaders();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => _content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_content);

        public override void Dispose()
        {
        }
    }

    private sealed class StubHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value)
        {
            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
    }
}
