using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RemoteScanner.Common;
using RemoteScanner.Protocol;
using CommonLog = RemoteScanner.Common.Log;

namespace RemoteScanner.Agent;

/// <summary>
/// Launches ScanHost and streams its frames back.
///
/// One process per operation. That is deliberate: a vendor driver that leaks handles, hangs
/// or calls ExitProcess costs exactly one job and cannot damage the agent, and it is the only
/// way to load a 32-bit TWAIN data source from a 64-bit agent.
///
/// The two processes talk over a per-job named pipe with a random name, ACLed to the current
/// user only — the same protocol the rest of the product speaks, so ScanHost's page frames
/// can be forwarded onward with no translation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScanHostRunner
{
    private readonly string _installDirectory;

    public ScanHostRunner(string? installDirectory = null)
        => _installDirectory = installDirectory ?? AppContext.BaseDirectory;

    /// <summary>Where each bitness of ScanHost lives once installed.</summary>
    public string ExecutablePath(bool use64Bit)
        => Path.Combine(_installDirectory, use64Bit ? "x64" : "x86", "RemoteScanner.ScanHost.exe");

    public bool IsAvailable(bool use64Bit) => File.Exists(ExecutablePath(use64Bit));

    /// <summary>
    /// Runs one command and yields every frame the host emits, in order, until it exits.
    /// Cancelling kills the host process — which is the only reliable way to stop a driver
    /// that is mid-transfer and not checking anything.
    /// </summary>
    public async IAsyncEnumerable<Frame> ExecuteAsync(
        bool use64Bit,
        MessageType commandType,
        IMessage command,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string executable = ExecutablePath(use64Bit);
        if (!File.Exists(executable))
            throw new FileNotFoundException($"ScanHost is missing: {executable}", executable);

        string pipeName = "RemoteScanner.Host." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        await using var server = SecurePipe.Create(pipeName, SecurePipe.CurrentUserSid(),
                                                   maxInstances: 1, firstInstance: true);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, $"--pipe {pipeName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            },
            EnableRaisingEvents = true,
        };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await server.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("ScanHost did not connect. The scanner driver may be blocking startup.");
            }

            await using var channel = new FrameChannel(server, ownsStream: false);
            channel.Start();

            await channel.SendAsync(command, 1, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                Frame? frame = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null) break;          // host finished and closed the pipe

                yield return frame.Value;

                // These are terminal; the host exits right after sending one.
                if (frame.Value.Type is MessageType.ScanComplete or MessageType.ScanError
                                     or MessageType.ScannerEnumResponse or MessageType.ScannerCapsResponse
                    && commandType != MessageType.ScanRequest)
                {
                    break;
                }

                if (frame.Value.Type is MessageType.ScanComplete or MessageType.ScanError) break;
            }
        }
        finally
        {
            await ShutdownAsync(process).ConfigureAwait(false);
        }
    }

    private static async Task ShutdownAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;

            // Give it a moment to exit on its own before killing; a clean exit lets the
            // driver release the scanner, an abrupt one can leave the device locked.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                CommonLog.Logger.Warning("ScanHost {Pid} did not exit; terminating it.", process.Id);
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* already gone */ }
        }
        catch (InvalidOperationException) { /* never started */ }
    }
}
