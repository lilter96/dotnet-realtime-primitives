using System.Net;
using System.Net.WebSockets;
using ShadowPrompter.Core.Resilience;
using Xunit;

namespace ShadowPrompter.Tests.Resilience;

/// <summary>The one reading of "is the key refused" and "when may I come back", shared by the STT, LLM and quota code.</summary>
public sealed class HttpFailureClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void Only_401_And_403_Reject_The_Credential(HttpStatusCode status, bool rejected)
    {
        Assert.Equal(rejected, HttpFailureClassifier.IsAuthRejection(status));
        Assert.Equal(rejected, HttpFailureClassifier.IsAuthFailure(new HttpRequestException("x", null, status)));
    }

    [Fact]
    public void No_Status_Is_Not_An_Auth_Rejection()
    {
        Assert.False(HttpFailureClassifier.IsAuthRejection(null));
        Assert.False(HttpFailureClassifier.IsAuthFailure(new HttpRequestException("dns")));
    }

    [Fact]
    public void An_Auth_Failure_Is_Found_Anywhere_In_The_Exception_Chain()
    {
        var wrapped = new InvalidOperationException("stream broke", new HttpRequestException("denied", null, HttpStatusCode.Unauthorized));

        Assert.True(HttpFailureClassifier.IsAuthFailure(wrapped));
        Assert.False(HttpFailureClassifier.IsAuthFailure(new InvalidOperationException("boom")));
    }

    [Theory]
    [InlineData("The server returned status code '401' when status code '101' was expected.", true)]
    [InlineData("The server returned status code '403' when the handshake was attempted.", true)]
    [InlineData("Unauthorized", true)]
    [InlineData("forbidden by policy", true)]
    [InlineData("The remote party closed the WebSocket connection without completing the close handshake.", false)]
    public void A_Refused_WebSocket_Handshake_Is_Recognised_By_Its_Text(string message, bool rejected)
    {
        Assert.Equal(rejected, HttpFailureClassifier.IsAuthFailure(new WebSocketException(message)));
    }

    private static HttpResponseMessage WithHeader(string name, string value)
    {
        var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    [Theory]
    [InlineData("retry-after", "3", 3000)]
    [InlineData("Retry-After", "0.5", 500)]
    [InlineData("retry-after", "600", 600_000)]
    [InlineData("retry-after-ms", "1500", 1500)]
    [InlineData("retry-after-ms", "0", 0)]
    public void Retry_After_Reads_Seconds_And_Milliseconds(string header, string value, double expectedMs)
    {
        using var response = WithHeader(header, value);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), HttpFailureClassifier.RetryAfter(response.Headers));
    }

    [Fact]
    public void The_Millisecond_Header_Wins_Over_The_Second_One()
    {
        using var response = WithHeader("retry-after-ms", "250");
        response.Headers.TryAddWithoutValidation("retry-after", "30");

        Assert.Equal(TimeSpan.FromMilliseconds(250), HttpFailureClassifier.RetryAfter(response.Headers));
    }

    [Fact]
    public void An_Http_Date_Is_Turned_Into_The_Time_Left_And_Never_Goes_Negative()
    {
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        using var future = WithHeader("Retry-After", "Fri, 25 Sep 2026 12:00:45 GMT");
        Assert.Equal(TimeSpan.FromSeconds(45), HttpFailureClassifier.RetryAfter(future.Headers, now));

        using var past = WithHeader("Retry-After", "Fri, 25 Sep 2026 11:00:00 GMT");
        Assert.Equal(TimeSpan.Zero, HttpFailureClassifier.RetryAfter(past.Headers, now));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("")]
    public void Anything_Unreadable_Is_No_Retry_After(string value)
    {
        using var response = WithHeader("retry-after", value);

        Assert.Null(HttpFailureClassifier.RetryAfter(response.Headers));
    }

    [Fact]
    public void No_Header_Is_No_Retry_After()
    {
        using var response = new HttpResponseMessage();

        Assert.Null(HttpFailureClassifier.RetryAfter(response.Headers));
    }
}
