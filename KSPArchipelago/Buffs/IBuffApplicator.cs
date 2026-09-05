namespace KSPArchipelago.Buffs
{
    /// <summary>
    /// Applies one family of buffs to a single part (a prefab or a live
    /// instance — the caller decides which, the applicator can't tell).
    /// </summary>
    /// <remarks>
    /// CONTRACT: every write must be ABSOLUTE — <c>stock * factor</c> from a
    /// snapshot taken the first time the part is seen — never a
    /// read-modify-write on the current value.
    ///
    /// This is not a style preference. Traps/RadioSilenceTrap.cs:16-18
    /// documents the exact failure mode: a second effect that captures the
    /// already-modified value as its "original" and restores that, permanently
    /// corrupting the field. Buffs re-apply on every item receipt, every
    /// vessel load, and every reconnect, so a compounding write would drift
    /// without bound. Absolute-from-snapshot makes re-application idempotent
    /// and removes the need for any "already applied?" bookkeeping.
    ///
    /// Snapshots are keyed by AvailablePart name, not by live instance, so a
    /// prefab and everything instantiated from it share one stock record.
    /// </remarks>
    public interface IBuffApplicator
    {
        /// <summary>Stable id, for logging.</summary>
        string Id { get; }

        /// <summary>
        /// Write buffed values into <paramref name="part"/>. Called with the
        /// full per-type totals because one applicator may consume several
        /// buff types (see EngineApplicator).
        /// </summary>
        /// <param name="partName">
        /// AvailablePart name — the snapshot key. Passed in rather than read
        /// off the part because a prefab and a live instance reach it by
        /// different routes.
        /// </param>
        void ApplyToPart(Part part, string partName, BuffTotals totals);

        /// <summary>Drop all snapshots (session teardown).</summary>
        void Reset();
    }
}
