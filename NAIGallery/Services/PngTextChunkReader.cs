using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.IO.Compression;
using System.Buffers.Binary;

namespace NAIGallery.Services;

/// <summary>
/// Lightweight PNG textual chunk reader for tEXt, zTXt, iTXt without third-party deps.
/// Caps chunk sizes to avoid pathological allocations.
/// </summary>
internal static class PngTextChunkReader
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    
    private const int MaxChunkLength = AppDefaults.PngMaxChunkLength;
    private const int MaxTextChunkLength = AppDefaults.PngMaxTextChunkLength;

    public static IEnumerable<string> ReadRawTextChunks(string file)
    {
        var results = new List<string>();
        
        try
        {
            using var fs = File.OpenRead(file);
            if (!ValidatePngSignature(fs))
                return results;

            ProcessChunks(fs, results);
        }
        catch { }
        
        return results;
    }

    private static bool ValidatePngSignature(FileStream fs)
    {
        Span<byte> signature = stackalloc byte[8];
        if (fs.Read(signature) != 8)
            return false;
        
        return signature.SequenceEqual(PngSignature);
    }

    private static void ProcessChunks(FileStream fs, List<string> results)
    {
        Span<byte> headerBuffer = stackalloc byte[8]; // 4 bytes length + 4 bytes type

        while (true)
        {
            if (fs.Read(headerBuffer) != 8)
                break;

            int length = BinaryPrimitives.ReadInt32BigEndian(headerBuffer[..4]);
            var chunkType = Encoding.ASCII.GetString(headerBuffer[4..]);

            if (length < 0 || length > MaxChunkLength)
                break; // Malformed chunk

            if (chunkType == "IEND")
                break;

            if (length == 0)
            {
                fs.Seek(4, SeekOrigin.Current); // Skip CRC
                continue;
            }

            if (!IsTextualChunk(chunkType) || length > MaxTextChunkLength)
            {
                fs.Seek(length + 4, SeekOrigin.Current); // Skip data + CRC
                continue;
            }

            var data = new byte[length];
            if (fs.Read(data, 0, length) != length)
                break;
            
            fs.Seek(4, SeekOrigin.Current); // Skip CRC

            var text = ParseTextChunk(chunkType, data);
            if (!string.IsNullOrEmpty(text))
                results.Add(text);
        }
    }

    private static bool IsTextualChunk(string chunkType)
        => chunkType is "tEXt" or "zTXt" or "iTXt";

    private static string? ParseTextChunk(string chunkType, byte[] data)
    {
        return chunkType switch
        {
            "tEXt" => ParseTEXt(data),
            "zTXt" => ParseZTXt(data),
            "iTXt" => ParseITXt(data),
            _ => null
        };
    }

    private static string? ParseTEXt(byte[] data)
    {
        int separator = Array.IndexOf(data, (byte)0);
        if (separator < 0 || separator >= data.Length - 1)
            return null;
        
        return Encoding.Latin1.GetString(data, separator + 1, data.Length - separator - 1);
    }

    private static string? ParseZTXt(byte[] data)
    {
        int separator = Array.IndexOf(data, (byte)0);
        if (separator < 0 || separator >= data.Length - 2)
            return null;

        try
        {
            var compressedData = data.AsSpan((separator + 2)..);
            return DecompressZlib(compressedData);
        }
        catch { return null; }
    }

    private static string? ParseITXt(byte[] data)
    {
        int keywordEnd = Array.IndexOf(data, (byte)0);
        if (keywordEnd < 0 || keywordEnd + 5 >= data.Length)
            return null;

        byte compressionFlag = data[keywordEnd + 1];
        byte compressionMethod = data[keywordEnd + 2];
        
        int pos = keywordEnd + 3;
        int langEnd = Array.IndexOf(data, (byte)0, pos);
        if (langEnd < 0) return null;
        
        pos = langEnd + 1;
        int transEnd = Array.IndexOf(data, (byte)0, pos);
        if (transEnd < 0) return null;
        
        pos = transEnd + 1;
        var textBytes = data.AsSpan(pos..);

        try
        {
            if (compressionFlag == 1 && compressionMethod == 0)
                return DecompressZlib(textBytes);
            
            return Encoding.UTF8.GetString(textBytes);
        }
        catch { return null; }
    }

    private static string DecompressZlib(ReadOnlySpan<byte> compressedData)
    {
        using var ms = new MemoryStream(compressedData.ToArray(), writable: false);
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
