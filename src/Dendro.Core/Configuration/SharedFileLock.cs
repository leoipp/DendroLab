namespace Dendro.Core.Configuration;

/// <summary>Server-enforced file sharing lock; the stream remains open for the entire write transaction.</summary>
public static class SharedFileLock
{
    public static FileStream Acquire(string path, CancellationToken cancellation = default, int timeoutSeconds = 60)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (DirectoryNotFoundException) { throw; }
            catch (IOException) when (watch.Elapsed.TotalSeconds < timeoutSeconds) { cancellation.WaitHandle.WaitOne(100); }
            catch (IOException ex) { throw new IOException("Outro usuário está gravando nesta base. Aguarde e tente novamente.", ex); }
        }
    }
}
