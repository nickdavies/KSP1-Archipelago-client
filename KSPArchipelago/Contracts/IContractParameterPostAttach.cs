namespace KSPArchipelago.Contracts
{
    /// <summary>
    /// A contract parameter that must build part of its child subtree only
    /// AFTER it has been attached to its contract, not in its constructor.
    ///
    /// KSP's <c>ContractParameter.AddParameter</c> re-roots ONLY the parameter it
    /// is handed (<c>NestToParent</c> sets that one parameter's <c>Root</c>); it
    /// never recurses into that parameter's pre-existing children. So a parameter
    /// that nests stock KSP parameters in its ctor — before it has a Root — leaves
    /// those grandchildren with <c>Root == null</c>. Stock parameters like
    /// <c>KerbalTourParameter</c> dereference <c>Root</c> in <c>GetHashString</c>
    /// (via <c>SystemUtilities.SuperSeed(Root)</c>), so <c>Contract.Generate</c>'s
    /// hash pass then throws a NullReferenceException and the contract never
    /// offers.
    ///
    /// <see cref="ApGenericContract.GeneratePopulate"/> calls
    /// <see cref="OnContractAttached"/> immediately after adding the parameter —
    /// when <c>Root</c> is set, and still before the contract registers, so the
    /// children are present for <c>Register</c>'s recursion. Only the force-offer
    /// creation path runs GeneratePopulate; on reload KSP restores the subtree
    /// from the save, so this never re-runs.
    /// </summary>
    internal interface IContractParameterPostAttach
    {
        /// <summary>Build the deferred child subtree; the parameter's Root is set.</summary>
        void OnContractAttached();
    }
}
