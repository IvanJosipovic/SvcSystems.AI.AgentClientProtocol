using System.Text;
using SvcSystems.AI.AgentClientProtocol.Transport;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class TransportBufferingTests
{
    [Fact]
    public async Task StdioFrameReaderEnforcesByteLimitBeforeNewline()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("123456789"));

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
        {
            await foreach (var _ in StdioTransport.ReadFramesAsync(input, maximumFrameBytes: 8, TestContext.Current.CancellationToken)) { }
        });

        Assert.Contains("8 byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdioFrameReaderAcceptsExactLimitAndCrLf()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("12345\r\n{}\n"));

        var frames = new List<string>();
        await foreach (var frame in StdioTransport.ReadFramesAsync(input, maximumFrameBytes: 5, TestContext.Current.CancellationToken))
        {
            frames.Add(Encoding.UTF8.GetString(frame.Span));
        }

        Assert.Equal(["12345", "{}"], frames);
    }

    [Fact]
    public async Task StdioFrameReaderSkipsBlankLines()
    {
        // Agents sometimes emit a blank line as a keepalive. It carries no frame and must not
        // reach the JSON parser, which would fail the whole connection.
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("\n\r\n{\"a\":1}\n\n{\"b\":2}\n"));

        var frames = new List<string>();
        await foreach (var frame in StdioTransport.ReadFramesAsync(input, maximumFrameBytes: 1024, TestContext.Current.CancellationToken))
        {
            frames.Add(Encoding.UTF8.GetString(frame.Span));
        }

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], frames);
    }

    [Fact]
    public async Task StdioFrameReaderSkipsATrailingBlankLineAtEndOfStream()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}\n\n"));

        var frames = new List<string>();
        await foreach (var frame in StdioTransport.ReadFramesAsync(input, maximumFrameBytes: 1024, TestContext.Current.CancellationToken))
        {
            frames.Add(Encoding.UTF8.GetString(frame.Span));
        }

        Assert.Equal(["{\"a\":1}"], frames);
    }

    [Fact]
    public void SessionUpdateQueueFailsInsteadOfGrowingOrDroppingUpdates()
    {
        var session = new Session(null!, "session-1", maximumBufferedUpdates: 1);

        Assert.True(session.Publish(new CurrentModeUpdate { CurrentModeId = "first" }));
        Assert.False(session.Publish(new CurrentModeUpdate { CurrentModeId = "second" }));
        Assert.False(session.Publish(new CurrentModeUpdate { CurrentModeId = "third" }));
    }

    [Fact]
    public async Task StdioTransportRejectsMissingExecutable()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await StdioTransport.StartAsync(
                new ProcessOptions { FileName = " " },
                TestContext.Current.CancellationToken));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task StdioTransportRejectsNonPositiveFrameLimit()
    {
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await StdioTransport.StartAsync(
                new ProcessOptions { FileName = "agent", MaximumMessageBytes = 0 },
                TestContext.Current.CancellationToken));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task StdioTransportWrapsStartupFailures()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"acp-missing-agent-{Guid.NewGuid():N}.exe");

        var exception = await Assert.ThrowsAsync<ProcessException>(async () =>
            await StdioTransport.StartAsync(
                new ProcessOptions { FileName = missing },
                TestContext.Current.CancellationToken));

        Assert.Contains("Failed to start", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
    }
}
