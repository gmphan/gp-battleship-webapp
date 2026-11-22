namespace GpBattleshipWebapp.Auth;

public static class AuthConstants
{
    /// <summary>Authentication scheme used by the cookie handler.</summary>
    public const string Scheme = "BattleshipAdmin";

    public const string CookieName = "gp_battleship_auth";
    public const string LoginPath = "/index.html";

    /// <summary>Claim carrying the admin's role ("SuperAdmin" | "Admin").</summary>
    public const string RoleClaim = "role";
    public const string SourceClaim = "src";      // "config" | "db"
    public const string UsernameClaim = "username";
}
