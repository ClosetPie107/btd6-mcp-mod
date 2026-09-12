using System;

namespace AgentBridge;

/// <summary>
/// Validation shared by native checkpoint capture and transfer envelopes.
/// A null label is represented by omission at the request boundary; a supplied
/// label must be a non-empty, bounded, non-control string.
/// </summary>
public static class CheckpointLabelValidation
{
    public const int MaxLength = 128;

    public static bool TryValidate(string? label, bool supplied, out string error)
    {
        if (!supplied)
        {
            error = "";
            return true;
        }

        if (label == null)
        {
            error = "Checkpoint label must be a non-empty string when supplied.";
            return false;
        }

        if (label.Length == 0)
        {
            error = "Checkpoint label must be non-empty.";
            return false;
        }

        if (label.Length > MaxLength)
        {
            error = $"Checkpoint label must be at most {MaxLength} characters.";
            return false;
        }

        foreach (char character in label)
        {
            if (char.IsControl(character))
            {
                error = "Checkpoint label must not contain control characters.";
                return false;
            }
        }

        error = "";
        return true;
    }

    public static bool IsValid(string? label) => TryValidate(label, true, out _);
}
