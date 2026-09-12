using System;
using System.Collections.Generic;
using System.IO;

namespace AgentBridge;

/// <summary>
/// Signals a processing-file read failure that should preserve the file for a
/// later startup attempt rather than discard it as malformed input.
/// </summary>
public sealed class MailboxRecoveryReadException : IOException
{
    public MailboxRecoveryReadException(string path, Exception inner)
        : base($"Unable to read processing file '{path}'.", inner)
    {
    }
}

/// <summary>
/// Managed guards and per-file isolation used while recovering processing files
/// before the game thread is available. Processing files are acknowledged, not
/// replayed.
/// </summary>
public static class MailboxRecoveryRules
{
    public static void ProcessFiles<T>(IEnumerable<string> paths, Func<string, T?> read,
        Func<T, bool> isValid, Action<T> publish, Action<string> delete, Action<Exception> report)
        where T : class
    {
        foreach (string path in paths)
        {
            bool deleteAfterProcessing = false;
            bool validRequest = false;
            try
            {
                T? value = read(path);
                if (value == null || !isValid(value))
                {
                    deleteAfterProcessing = true;
                }
                else
                {
                    validRequest = true;
                    publish(value);
                    deleteAfterProcessing = true;
                }
            }
            catch (Exception ex)
            {
                report(ex);
                // A valid request whose terminal result could not be published
                // must remain for the next startup; malformed/null-ID files do
                // not represent replayable commands and are discarded.
                deleteAfterProcessing = ex is not MailboxRecoveryReadException && !validRequest;
            }
            finally
            {
                if (deleteAfterProcessing)
                {
                    try
                    {
                        delete(path);
                    }
                    catch (Exception ex)
                    {
                        report(ex);
                    }
                }
            }
        }
    }

    public static bool IsSafeRequestId(string? requestId)
    {
        if (string.IsNullOrEmpty(requestId) || requestId.Length > 128)
            return false;

        foreach (char character in requestId)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
                return false;
        }

        return true;
    }
}
