using System;
using System.Collections.Generic;
using System.IO;

namespace VirtualPinballLauncher;

public sealed record TableItem(string VpxPath, string DisplayName, string? PreviewVideoPath, string? BackglassVideoPath);

public static class TableScanner
{
    public static IEnumerable<TableItem> Scan(Config cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.TablesDirectory))
            yield break;

        if (!Directory.Exists(cfg.TablesDirectory))
            yield break;

        foreach (var vpx in Directory.EnumerateFiles(cfg.TablesDirectory, "*.vpx", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(vpx);

            if (string.IsNullOrWhiteSpace(cfg.PreviewsDirectory))
            {
                yield return new TableItem(vpx, name, null, null);
                continue;
            }

            var (preview, backglass) = cfg.PreviewVideoPaths(name);
            yield return new TableItem(vpx, name, preview, backglass);
        }
    }
}