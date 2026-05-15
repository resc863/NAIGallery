using System;
using System.Diagnostics;

namespace NAIGallery;

internal static class AppLog
{
    [Conditional("DEBUG")]
    public static void Debug(string message, Exception? exception = null)
    {
        Write("DEBUG", message, exception);
    }

    [Conditional("DEBUG")]
    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    [Conditional("DEBUG")]
    public static void Warning(string message, Exception? exception = null)
    {
        Write("WARN", message, exception);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        System.Diagnostics.Debug.WriteLine(exception == null
            ? $"[{level}] {message}"
            : $"[{level}] {message}: {exception}");
    }
}
