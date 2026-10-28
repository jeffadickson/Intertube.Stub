using System.Diagnostics;
using Intertube.Stub.Sftp;

namespace Intertube.Stub.Tests.Sftp;

/// <summary>Skips the test when OpenSSH's <c>sftp</c> and <c>ssh-keygen</c> are not on the PATH.</summary>
public sealed class OpenSshFactAttribute : FactAttribute
{
    public OpenSshFactAttribute()
    {
        if (OpenSsh.Directory is null) Skip = "OpenSSH (sftp and ssh-keygen) is not on the PATH.";
    }
}

/// <inheritdoc cref="OpenSshFactAttribute"/>
public sealed class OpenSshTheoryAttribute : TheoryAttribute
{
    public OpenSshTheoryAttribute()
    {
        if (OpenSsh.Directory is null) Skip = "OpenSSH (sftp and ssh-keygen) is not on the PATH.";
    }
}

/// <summary>
/// Drives the real OpenSSH <c>sftp</c> client in batch mode against a server, from a scratch
/// directory holding an ed25519 identity made by <c>ssh-keygen</c>, the way CodeCourier does.
/// </summary>
public sealed class OpenSsh : IDisposable
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";

    public static string? Directory { get; } = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(d => File.Exists(Path.Combine(d, "sftp" + Exe)) && File.Exists(Path.Combine(d, "ssh-keygen" + Exe)));

    private OpenSsh(string scratch)
    {
        Scratch = scratch;
        Identity = Path.Combine(scratch, "id_ed25519");
    }

    /// <summary>Working directory for local files; batch commands can use plain relative names.</summary>
    public string Scratch { get; }

    public string Identity { get; set; }

    public SshPublicKey PublicKey => SshPublicKey.Load(Identity + ".pub");

    public static async Task<OpenSsh> CreateAsync()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "intertube-sftp-" + Guid.NewGuid().ToString("N")[..12]);
        System.IO.Directory.CreateDirectory(scratch);
        var client = new OpenSsh(scratch);
        var (exit, output) = await RunAsync("ssh-keygen", scratch, ["-q", "-t", "ed25519", "-N", "", "-C", "test", "-f", "id_ed25519"]);
        Assert.True(exit == 0, "ssh-keygen failed: " + output);
        return client;
    }

    public string PathOf(string name) => Path.Combine(Scratch, name);

    /// <summary>Run <c>sftp -b</c> with these batch commands as <paramref name="user"/>, pinning the server's host key.</summary>
    public Task<(int ExitCode, string Output)> SftpAsync(SftpServer server, string batch, string user = "test", params string[] options) =>
        SftpAsync(server.Port, server.KnownHostsLine, batch, user, options);

    public async Task<(int ExitCode, string Output)> SftpAsync(int port, string knownHostsLine, string batch, string user = "test", params string[] options)
    {
        await File.WriteAllTextAsync(PathOf("known_hosts"), knownHostsLine + "\n");
        await File.WriteAllTextAsync(PathOf("ssh_config"), "");
        await File.WriteAllTextAsync(PathOf("batch"), batch.ReplaceLineEndings("\n"));
        List<string> args =
        [
            "-F", "ssh_config", "-b", "batch", "-P", port.ToString(), "-i", Path.GetFileName(Identity),
            "-o", "UserKnownHostsFile=known_hosts", "-o", "GlobalKnownHostsFile=ssh_config", "-o", "StrictHostKeyChecking=yes",
            "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "PasswordAuthentication=no", "-o", "ConnectTimeout=10",
            .. options,
            $"{user}@127.0.0.1",
        ];
        return await RunAsync("sftp", Scratch, args);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string tool, string workingDirectory, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Path.Combine(Directory!, tool + Exe))
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{tool} did not finish. Output so far:\n{await stdout}\n{await stderr}");
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Scratch, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
