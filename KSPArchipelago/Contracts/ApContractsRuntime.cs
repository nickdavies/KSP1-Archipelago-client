namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// Holds the live ContractEnvelope parsed from slot_data. Procedural
    /// contract subclasses read it at Generate / MeetRequirements time.
    /// Set by KSPArchipelagoMod.HandleConnect after slot_data parses
    /// successfully; reset to ContractEnvelope.Empty on disconnect.
    ///
    /// Kept as a separate static class (rather than on KSPArchipelagoMod
    /// or ApContractSlotManager) so the contracts subsystem doesn't
    /// reach across into the mod's main-thread state and so the envelope
    /// can be swapped atomically.
    /// </summary>
    public static class ApContractsRuntime
    {
        public static ContractEnvelope Envelope { get; set; } = ContractEnvelope.Empty;
    }
}
