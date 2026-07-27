using System;
using System.IO;

namespace Jellyfin.Plugin.StrmAudio
{
    /// <summary>
    /// Helper functions for reading .strm files.
    /// </summary>
    internal static class StrmFile
    {
        public static bool IsStrm(string? path)
            => !string.IsNullOrEmpty(path)
               && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads the first non-empty, non-comment line (the target) from a .strm file.
        /// Returns null when the file is unreadable or empty.
        /// </summary>
        public static string? ReadTarget(string strmPath)
        {
            try
            {
                foreach (var raw in File.ReadLines(strmPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#'))
                    {
                        continue;
                    }

                    return line;
                }
            }
            catch (Exception)
            {
                // File unreadable/removed; caller logs.
            }

            return null;
        }
    }
}
