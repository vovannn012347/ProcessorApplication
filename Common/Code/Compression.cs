using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.Code;

public static class Compression
{
    public static byte[] Compress(byte[] data)
    {
        using (var compressedStream = new MemoryStream())
        using (var gzip = new GZipStream(compressedStream, CompressionMode.Compress))
        {
            gzip.Write(data, 0, data.Length);
            gzip.Close(); // Flush
            return compressedStream.ToArray();
        }
    }

    public static byte[] Decompress(byte[] data)
    {
        using (var compressedStream = new MemoryStream(data))
        using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress))
        using (var decompressedStream = new MemoryStream())
        {
            gzip.CopyTo(decompressedStream);
            return decompressedStream.ToArray();
        }
    }
}
