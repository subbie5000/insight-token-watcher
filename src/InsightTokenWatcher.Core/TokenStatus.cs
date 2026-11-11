namespace InsightTokenWatcher.Core;

public static class TokenStatus
{
    /// <summary>
    /// The latest poll failed because the token does not work – expired, revoked, rejected or
    /// missing. The watcher cannot measure such an account; the popup greys it out instead of
    /// raising an error (11.11.2026). Returns what to call it, or null when the token is fine or
    /// the failure is something else (network, a server error).
    /// </summary>
    public static string? Problem(AccountView v)
    {
        if (v.Snapshot is not { Ok: false } failed) return null;
        if (v.Failure == FailureKind.AuthExpired) return "Token abgelaufen";
        if (v.Failure != FailureKind.AuthInvalid) return null;
        var error = failed.Error ?? "";
        if (error.Contains("revoked", StringComparison.OrdinalIgnoreCase)) return "Token widerrufen";
        if (error.Contains("Kein API-Schlüssel", StringComparison.OrdinalIgnoreCase)) return "kein Schlüssel";
        return "Token ungültig";
    }
}
