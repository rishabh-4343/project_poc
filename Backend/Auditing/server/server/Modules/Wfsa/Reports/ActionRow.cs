using server.Modules.Wfsa.Data;
using server.Modules.Wfsa.Ingestion;

namespace server.Modules.Wfsa.Reports;

// One report/live row. Shared by the REST report, the CSV export and the ActionsAppended push.
public sealed record ActionRow(
    Guid ServerId, long Seq, DateTimeOffset OccurredAt, string ObjectType, string Operation, bool Success, string Certainty,
    string Path, string? NewPath, string? ShareName, string ActorSid, string? ActorName, string? ClientIp, string? ProcessName,
    string? OldSecurity, string? NewSecurity, string ContentType, int RiskLevel, string[] ComplianceTags, decimal MonetaryValue,
    string? Details)
{
    public static ActionRow From(AuditAction a, IReadOnlyDictionary<long, FileClassificationRef> ddc)
    {
        ddc.TryGetValue(a.PathHash, out var c);
        return new ActionRow(
            a.ServerId, a.Seq, a.OccurredAt.ToUniversalTime(), a.ObjectType.ToString(), a.Operation.ToString(), a.Success,
            a.Certainty.ToString(), a.Path ?? "", NullIfEmpty(a.NewPath), NullIfEmpty(a.ShareName), a.ActorSid ?? "",
            NullIfEmpty(a.ActorName), NullIfEmpty(a.ClientIp), NullIfEmpty(a.ProcessName), NullIfEmpty(a.OldSecurity),
            NullIfEmpty(a.NewSecurity), c?.ContentType ?? "", c?.RiskLevel ?? -1, c?.ComplianceTags ?? [], c?.MonetaryValue ?? 0m,
            NullIfEmpty(a.Details));
    }

    public static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
