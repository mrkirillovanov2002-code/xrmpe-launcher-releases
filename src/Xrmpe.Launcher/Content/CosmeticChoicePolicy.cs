namespace Xrmpe.Launcher.Content;

public enum CosmeticDecision { Cancel, Without, Include }

public static class CosmeticChoicePolicy
{
    public static ModWorkspace Apply(ModWorkspace current, CosmeticDecision decision, string actualHash)
    {
        if (decision == CosmeticDecision.Cancel) return current;
        if (decision == CosmeticDecision.Without)
            return current with { ArmbandsEnabled = false, ArmbandsApprovedHash = "", ArmbandsApprovalBuilderHash = "" };
        if (actualHash.Length != 64 || !actualHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Неверный SHA-256 косметического пакета.");
        return current with { ArmbandsEnabled = true, ArmbandsVariant = "emissive", ArmbandsApprovedHash = actualHash,
            ArmbandsApprovalBuilderHash = EmissiveWorkshop.BuilderZipHash };
    }
}
