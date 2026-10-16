using System.Diagnostics;
using System.Text;

namespace SvcSystems.AI.AgentClientProtocol.Transport;

internal sealed class StdioAcpTransport : IAcpTransport
{
    private const int MaximumStandardErrorLineCharacters = 16 * 1024;
    private readonly Process _process;
    private readonly StreamReader _standardOutput;
    private readonly StreamWriter _standardInput;
    private readonly Action<string>? _standardErrorReceived;
    private readonly int _maximumMessageBytes;
    private readonly TimeSpan _shutdownTimeout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _standardErrorTask;
    private int _disposed;

    private StdioAcpTransport(Process process, AcpProcessOptions options)
    {
        _process = process;
        _standardOutput = process.StandardOutput;
        _standardInput = process.StandardInput;
        _standardErrorReceived = options.StandardErrorReceived;
        _maximumMessageBytes = options.MaximumMessageBytes;
        _shutdownTimeout = options.ShutdownTimeout;
        _standardErrorTask = DrainStandardErrorAsync();
    }

    public static ValueTask<StdioAcpTransport> StartAsync(
        AcpProcessOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(options.FileName))
        {
            throw new ArgumentException("An agent executable must be specified.", nameof(options));
        }
        if (options.MaximumMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum frame size must be positive.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.FileName,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var variable in options.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new AcpProcessException($"Failed to start ACP agent '{options.FileName}'.");
            }
            return ValueTask.FromResult(new StdioAcpTransport(process, options));
        }
        catch (AcpProcessException)
        {
            throw;
        }
        catch (Exception exception)
        {
            process.Dispose();
            throw new AcpProcessException($"Failed to start ACP agent '{options.FileName}'.", exception);
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _standardInput.BaseStream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await _standardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
            await _standardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new AcpProcessException("The ACP agent process stdin is no longer available.", exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await _standardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or DecoderFallbackException or ObjectDisposedException)
            {
                throw new AcpProcessException("Reading ACP agent stdout failed.", exception);
            }

            if (line is null)
            {
                if (_process.HasExited)
                {
                    throw new AcpProcessException($"ACP agent process exited with code {_process.ExitCode}.");
                }
                yield break;
            }

            var bytes = Encoding.UTF8.GetBytes(line);
            if (bytes.Length > _maximumMessageBytes)
            {
                throw new AcpProtocolException($"ACP stdio frame exceeded the configured {_maximumMessageBytes} byte limit.");
            }
            yield return bytes;
        }
    }

    private async Task DrainStandardErrorAsync()
    {
        var buffer = new char[2048];
        var line = new StringBuilder();
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await _process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0) break;
                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\r') continue;
                    if (character == '\n')
                    {
                        ReportStandardErrorLine(line, truncated);
                        line.Clear();
                        truncated = false;
                        continue;
                    }

                    if (line.Length < MaximumStandardErrorLineCharacters) line.Append(character);
                    else truncated = true;
                }
            }

            if (line.Length > 0 || truncated) ReportStandardErrorLine(line, truncated);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Process shutdown can close stderr while it is being drained.
        }
    }

    private void ReportStandardErrorLine(StringBuilder line, bool truncated)
    {
        try
        {
            _standardErrorReceived?.Invoke(truncated ? line.ToString() + " [truncated]" : line.ToString());
        }
        catch
        {
            // A host diagnostic callback must not stop stderr draining and deadlock the child.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            _standardInput.Close();
            using var timeout = new CancellationTokenSource(_shutdownTimeout);
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                // The process exited between checking HasExited and Kill.
            }
        }
        catch (InvalidOperationException)
        {
            // The process has already exited.
        }
        catch (IOException)
        {
            // Closing stdin can fail if the child exited at the same time.
        }
        finally
        {
            try { await _standardErrorTask.ConfigureAwait(false); }
            catch { /* stderr drain errors do not prevent deterministic cleanup */ }
            _process.Dispose();
            _writeLock.Dispose();
        }
    }
}
