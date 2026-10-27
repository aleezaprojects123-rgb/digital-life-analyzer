namespace Dla.Agent.Consent;

/// <summary>What the user chose on the consent window. Closing the window counts as Declined.</summary>
public enum ConsentDecision
{
    Declined,
    Accepted,
}
