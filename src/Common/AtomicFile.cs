using System;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    internal static class AtomicFile
    {
        private static readonly object WriteLock = new object();

        internal static void WriteAllText(string path, string text, Encoding encoding)
        {
            // Use a sibling file so replacement stays on the same volume.
            lock (WriteLock)
            {
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] preamble = encoding.GetPreamble();
                        stream.Write(preamble, 0, preamble.Length);
                        byte[] data = encoding.GetBytes(text);
                        stream.Write(data, 0, data.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
        }
    }
}
