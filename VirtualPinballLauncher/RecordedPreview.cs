using System.Diagnostics;
using System.IO;

namespace VirtualPinballLauncher;

// Capture beside the destination so publishing can replace it with a single move.
internal sealed class RecordedPreview : IDisposable
{
    private readonly string destination;
    public string TemporaryPath { get; }

    public RecordedPreview(string destination)
    {
        this.destination = Path.GetFullPath(destination);
        TemporaryPath = Path.Combine(Path.GetDirectoryName(this.destination)!,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.recording{Path.GetExtension(destination)}");
    }

    public void Commit() => File.Move(TemporaryPath, destination, overwrite: true);

    public static async Task RecordPairAsync(string playfieldPath, string backglassPath,
        Func<string, string, Task> record)
    {
        using var playfield = new RecordedPreview(playfieldPath);
        using var backglass = new RecordedPreview(backglassPath);
        await record(playfield.TemporaryPath, backglass.TemporaryPath);

        // Check both outputs before publishing either one.
        foreach (var preview in new[] { playfield, backglass })
        {
            if (!File.Exists(preview.TemporaryPath) || new FileInfo(preview.TemporaryPath).Length == 0)
                throw new IOException($"Recording did not produce a preview: {preview.destination}");
        }

        playfield.Commit();
        backglass.Commit();
    }

    public void Dispose()
    {
        try { File.Delete(TemporaryPath); }
        catch (IOException ex) { Trace.TraceWarning($"Could not delete temporary preview: {ex}"); }
        catch (UnauthorizedAccessException ex) { Trace.TraceWarning($"Could not delete temporary preview: {ex}"); }
    }
}
