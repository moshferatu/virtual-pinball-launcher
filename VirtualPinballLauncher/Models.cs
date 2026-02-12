using System;
using System.Collections.Generic;
using System.IO;

namespace VirtualPinballLauncher;

public sealed record TableItem(string VpxPath, string DisplayName, string? PreviewVideoPath);

public static class TableScanner
{
    public static IEnumerable<TableItem> Scan(Config cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.TablesDirectory))
            yield break;

        if (!Directory.Exists(cfg.TablesDirectory))
            yield break;

        string videoExt = string.IsNullOrWhiteSpace(cfg.VideoExtension) ? ".mp4" : cfg.VideoExtension;

        foreach (var vpx in Directory.EnumerateFiles(cfg.TablesDirectory, "*.vpx", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(vpx);

            string? preview = null;
            if (!string.IsNullOrWhiteSpace(cfg.VideosDirectory))
                preview = Path.Combine(cfg.VideosDirectory, name + videoExt);

            yield return new TableItem(vpx, name, preview);
        }
    }
}