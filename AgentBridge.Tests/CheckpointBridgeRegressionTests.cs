using System;
using System.IO;
using System.Collections.Generic;

namespace AgentBridge;

internal static class CheckpointBridgeRegressionTests
{
    public static void Run()
    {
        AssertLabelRules();
        AssertRecoveryRules();
        Console.WriteLine("PASS checkpoint label and mailbox recovery regressions");
    }

    private static void AssertLabelRules()
    {
        if (!CheckpointLabelValidation.TryValidate(null, false, out _))
            throw new InvalidOperationException("FAIL omitted checkpoint label should select round-keyed storage");
        if (CheckpointLabelValidation.IsValid("") || CheckpointLabelValidation.IsValid(new string('x', 129)) ||
            CheckpointLabelValidation.IsValid("ok\nlabel"))
            throw new InvalidOperationException("FAIL invalid checkpoint label accepted");
        if (!CheckpointLabelValidation.IsValid(new string('x', 128)))
            throw new InvalidOperationException("FAIL maximum-length checkpoint label rejected");
    }

    private static void AssertRecoveryRules()
    {
        var published = new List<string>();
        var deleted = new List<string>();
        var errors = new List<Exception>();

        MailboxRecoveryRules.ProcessFiles<string>(
            new[] { "malformed", "null-id", "read-fails", "publish-fails", "valid" },
            path => path switch
            {
                "malformed" => throw new InvalidOperationException("malformed processing file"),
                "null-id" => null,
                "read-fails" => throw new MailboxRecoveryReadException(
                    path, new IOException("processing file read failed")),
                "publish-fails" => "publish-fails",
                _ => "valid.request-1"
            },
            MailboxRecoveryRules.IsSafeRequestId,
            value =>
            {
                if (value == "publish-fails")
                    throw new IOException("outbox unavailable");
                published.Add(value);
            },
            deleted.Add,
            errors.Add);

        if (published.Count != 1 || published[0] != "valid.request-1")
            throw new InvalidOperationException("FAIL valid processing file was blocked by an earlier malformed or failed-publication file");
        if (deleted.Count != 3 || deleted.Contains("read-fails") || deleted.Contains("publish-fails") || errors.Count != 3)
            throw new InvalidOperationException("FAIL recovery did not isolate malformed/null files or retain failed read/publication");
        if (!MailboxRecoveryRules.IsSafeRequestId("valid.request-1") ||
            MailboxRecoveryRules.IsSafeRequestId(null) || MailboxRecoveryRules.IsSafeRequestId(""))
            throw new InvalidOperationException("FAIL request ID recovery guard mismatch");

    }
}
