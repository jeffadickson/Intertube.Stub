using System.Diagnostics;
using Intertube.Stub.Sftp;
using Xunit.Abstractions;

namespace Intertube.Stub.Tests.Sftp;

public sealed class SftpFileSystemTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "intertube-fs-" + Guid.NewGuid().ToString("N")[..12])).FullName;

    public void Dispose()
    {
        try
        {
            // Remove links first, so the recursive delete never walks through one.
            foreach (var link in Directory.EnumerateDirectories(_scratch, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
                Directory.Delete(link);
            Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void InMemory_RefusesToCreateTheRootAsAFileOrDirectory()
    {
        var files = new InMemorySftpFileSystem();

        Assert.Equal(SftpStatus.Failure, Assert.Throws<SftpException>(() => files.OpenFile("/", FileMode.CreateNew, FileAccess.ReadWrite)).Status);
        Assert.Equal(SftpStatus.Failure, Assert.Throws<SftpException>(() => files.CreateDirectory("/")).Status);
        Assert.Throws<SftpException>(() => files.Rename("/", "/x", overwrite: true));
        Assert.Empty(files.ListDirectory("/"));
    }

    [Fact]
    public void InMemory_RenameToItself_IsANoOp()
    {
        var files = new InMemorySftpFileSystem().AddFile("/a.txt", "a").AddDirectory("/d");

        files.Rename("/a.txt", "/a.txt", overwrite: false);
        files.Rename("/d", "/d", overwrite: true);

        Assert.Equal("a", files.ReadAllText("/a.txt"));
        Assert.True(files.DirectoryExists("/d"));
        Assert.Throws<SftpException>(() => files.Rename("/d", "/d/e", overwrite: false));
    }

    [Fact]
    public void InMemory_EnforcesMaxFileSize_AndReadsPastTheEndAsEof()
    {
        var files = new InMemorySftpFileSystem { MaxFileSize = 10 };
        using var stream = files.OpenFile("/f", FileMode.Create, FileAccess.ReadWrite);
        stream.Write(new byte[10]);

        Assert.Equal(SftpStatus.Failure, Assert.Throws<SftpException>(() => stream.Write(new byte[1])).Status);
        stream.Position = long.MaxValue - 1;
        Assert.Throws<SftpException>(() => stream.Write(new byte[2]));
        Assert.Equal(0, stream.Read(new byte[4]));
        Assert.Throws<SftpException>(() => files.SetAttributes("/f", new SftpAttributes { Size = 11 }));
        Assert.Throws<SftpException>(() => stream.SetLength(int.MaxValue + 1L));
        Assert.Equal(10, files.GetAttributes("/f").Size);
    }

    [Fact]
    public void Directory_ServingAFileSystemRoot_AllowsPathsBelowIt()
    {
        var root = Path.GetPathRoot(_scratch)!;
        var files = new DirectorySftpFileSystem(root);

        Assert.Equal(Path.Combine(root, "a", "b"), files.LocalPath("/a/b"));
        Assert.Equal(root, files.LocalPath("/"));
        Assert.True(files.GetAttributes("/").IsDirectory);
    }

    [Fact]
    public void Directory_RootWithTrailingSeparator_StillJails()
    {
        var jail = Directory.CreateDirectory(Path.Combine(_scratch, "jail")).FullName;
        var files = new DirectorySftpFileSystem(jail + Path.DirectorySeparatorChar);

        Assert.Equal(Path.Combine(jail, "x"), files.LocalPath("/x"));
        Assert.Equal(Path.Combine(jail, "x"), files.LocalPath("/../x"));
    }

    [Fact]
    public void Directory_LinkLeadingOutsideTheRoot_IsRefused()
    {
        var jail = Directory.CreateDirectory(Path.Combine(_scratch, "jail")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_scratch, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        Directory.CreateDirectory(Path.Combine(jail, "inner"));
        if (!TryLinkDirectory(Path.Combine(jail, "escape"), outside) || !TryLinkDirectory(Path.Combine(jail, "alias"), Path.Combine(jail, "inner")))
        {
            output.WriteLine("Cannot create directory links here; nothing to check.");
            return;
        }
        var files = new DirectorySftpFileSystem(jail);

        foreach (var path in new[] { "/escape", "/escape/secret.txt", "/escape/new.txt" })
            Assert.Equal(SftpStatus.PermissionDenied, Assert.Throws<SftpException>(() => files.LocalPath(path)).Status);
        Assert.Throws<SftpException>(() => files.OpenFile("/escape/secret.txt", FileMode.Open, FileAccess.Read));
        Assert.Equal(Path.Combine(jail, "alias", "x"), files.LocalPath("/alias/x"));
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (!OperatingSystem.IsWindows()) return false;
        // Junctions need no privilege.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        return mklink.ExitCode == 0;
    }
}
