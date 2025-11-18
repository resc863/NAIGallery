using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.IO.Compression;
using NAIGallery; // AppDefaults

namespace NAIGallery.Services;

/// <summary>
/// Lightweight PNG textual chunk reader for tEXt, zTXt, iTXt without third-party deps.
/// Caps chunk sizes to avoid pathological allocations.
/// </summary>
internal static class PngTextChunkReader
{
    public static IEnumerable<string> ReadRawTextChunks(string file)
    {
        const int MaxChunkLength = AppDefaults.PngMaxChunkLength;
        const int MaxTextChunkLength = AppDefaults.PngMaxTextChunkLength;
        List<string> results = new();
        try
        {
            using var fs = File.OpenRead(file);
            Span<byte> sig = stackalloc byte[8];
            if (fs.Read(sig) != 8) return results;
            // Validate PNG signature: 89 50 4E 47 0D 0A 1A 0A
            ReadOnlySpan<byte> pngSig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (!sig.SequenceEqual(pngSig)) return results;

            byte[] lenBuf = new byte[4];
            byte[] typeBuf = new byte[4];
            while (true)
            {
                if (fs.Read(lenBuf, 0, 4) != 4) break;
                int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                if (length < 0 || length > MaxChunkLength)
                {
                    // Malformed chunk; stop parsing to avoid endless loop
                    break;
                }
                if (fs.Read(typeBuf, 0, 4) != 4) break;
                string chunkType = Encoding.ASCII.GetString(typeBuf);

                // If chunk is not relevant or too large, skip efficiently
                if (length == 0)
                {
                    fs.Seek(4, SeekOrigin.Current); // skip CRC
                    if (chunkType == "IEND") break;
                    continue;
                }

                bool isTextual = chunkType is "tEXt" or "zTXt" or "iTXt";
                if (!isTextual || length > MaxTextChunkLength)
                {
                    fs.Seek(length + 4, SeekOrigin.Current); // skip data + CRC
                    if (chunkType == "IEND") break;
                    continue;
                }

                var data = new byte[length];
                int read = fs.Read(data, 0, length);
                if (read != length) break;
                fs.Seek(4, SeekOrigin.Current); // skip CRC

                switch (chunkType)
                {
                    case "tEXt":
                        {
                            int sep = Array.IndexOf(data, (byte)0);
                            if (sep >= 0 && sep < data.Length - 1)
                                results.Add(Encoding.Latin1.GetString(data, sep + 1, data.Length - sep - 1));
                            break;
                        }
                    case "zTXt":
                        {
                            int sep = Array.IndexOf(data, (byte)0);
                            if (sep >= 0 && sep < data.Length - 2)
                            {
                                var compData = data[(sep + 2)..];
                                try
                                {
                                    using var ms = new MemoryStream(compData, writable: false);
                                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                                    using var outMs = new MemoryStream();
                                    z.CopyTo(outMs);
                                    results.Add(Encoding.UTF8.GetString(outMs.ToArray()));
                                }
                                catch { }
                            }
                            break;
                        }
                    case "iTXt":
                        {
                            int idx = Array.IndexOf(data, (byte)0);
                            if (idx >= 0 && idx + 5 < data.Length)
                            {
                                byte compressionFlag = data[idx + 1];
                                byte compressionMethod = data[idx + 2];
                                int pos = idx + 3;
                                int langEnd = Array.IndexOf(data, (byte)0, pos); if (langEnd < 0) break; pos = langEnd + 1;
                                int transEnd = Array.IndexOf(data, (byte)0, pos); if (transEnd < 0) break; pos = transEnd + 1;
                                byte[] textBytes = data[pos..];
                                try
                                {
                                    if (compressionFlag == 1 && compressionMethod == 0)
                                    {
                                        using var ms = new MemoryStream(textBytes, writable: false);
                                        using var z = new ZLibStream(ms, CompressionMode.Decompress);
                                        using var outMs = new MemoryStream();
                                        z.CopyTo(outMs);
                                        textBytes = outMs.ToArray();
                                    }
                                    results.Add(Encoding.UTF8.GetString(textBytes));
                                }
                                catch { }
                            }
                            break;
                        }
                }
                if (chunkType == "IEND") break;
            }
        }
        catch { }
        return results;
    }
}
