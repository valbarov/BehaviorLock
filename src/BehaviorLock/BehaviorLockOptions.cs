namespace BehaviorLock;

/// <summary>What the guard does with an access that is not in the profile.</summary>
public enum GuardMode
{
    /// <summary>The guard does nothing.</summary>
    Off,

    /// <summary>Every resource access is added to the profile. The profile is saved on shutdown.</summary>
    Learn,

    /// <summary>Accesses outside the profile are reported but allowed.</summary>
    Audit,

    /// <summary>Accesses outside the profile are reported and blocked.</summary>
    Enforce,
}

/// <summary>The options of the guard. Bind them to the configuration section <c>BehaviorLock</c>.</summary>
public sealed class BehaviorLockOptions
{
    /// <summary>
    /// The global mode. The default is <see cref="GuardMode.Audit"/>. One endpoint or route group can have
    /// its own mode; see <see cref="BehaviorLockAttribute"/>.
    /// <para>
    /// A change of this value in the configuration applies while the app runs, between Off, Audit and
    /// Enforce. Every other option is read once, when the app starts.
    /// </para>
    /// </summary>
    public GuardMode Mode { get; set; } = GuardMode.Audit;

    /// <summary>
    /// Path of the profile file. A relative path is resolved against the content root of the app,
    /// the same directory that holds appsettings.json.
    /// </summary>
    public string ProfilePath { get; set; } = "behavior.lock.json";

    /// <summary>
    /// Path of the pending file, or null to write none. The guard writes every access that is not in the
    /// profile to this file, in the format of the profile. A person reviews the file and moves the legal
    /// accesses to the approved file. A relative path is resolved like <see cref="ProfilePath"/>.
    /// </summary>
    public string? PendingPath { get; set; }

    /// <summary>
    /// The largest number of accesses in the pending file. Each attack attempt can give a new access,
    /// so the file must not grow without end. Further accesses are still reported, but not written.
    /// </summary>
    public int PendingLimit { get; set; } = 1000;

    /// <summary>
    /// Path of the approved file, or null to use none. It lists accesses that a person reviewed and accepted.
    /// They are allowed like the accesses of the profile. The file has the format of the profile, and the
    /// recording never changes it. A relative path is resolved like <see cref="ProfilePath"/>.
    /// </summary>
    public string? ApprovedPath { get; set; }

    /// <summary>
    /// Records and checks the kind of caller of each endpoint: anonymous, signed in, and the roles.
    /// The check needs the user of the request, so the guard middleware must run after authentication
    /// and authorization. False means that callers are not recorded and not checked.
    /// </summary>
    public bool CheckCallers { get; set; } = true;

    /// <summary>
    /// More claim types that are part of the kind of caller, for example <c>scope</c>. The roles are always
    /// part of it. Each value of such a claim gives one part of the <c>caller:</c> entry, and a value with
    /// spaces counts as a list of values. Use only claims with few values: a claim that differs for each
    /// user, such as the user id, gives one kind of caller per user. A change of this list needs a new recording.
    /// </summary>
    public IList<string> CallerClaims { get; set; } = [];

    /// <summary>
    /// Checks the requests of every <see cref="HttpClient"/> of the process, also of a client that a package
    /// creates by itself, and each redirect hop. False means that only the clients of
    /// <c>IHttpClientFactory</c> are checked, and only their first address.
    /// <para>
    /// The check needs an activity for each request. So a request without an activity around it, for example
    /// in a background job, gets a <c>traceparent</c> header that it did not have before.
    /// </para>
    /// </summary>
    public bool CheckAllHttpClients { get; set; } = true;

    /// <summary>
    /// Records and checks the limits of each endpoint: the rows that a SQL statement returns and the size
    /// of the response body. False means that limits are not recorded and not checked.
    /// </summary>
    public bool CheckLimits { get; set; } = true;

    /// <summary>
    /// False means that a number over a limit is reported, but never blocked, also in Enforce mode.
    /// Every other access is still blocked. Use it while the limits still come from small test data:
    /// the pending file then shows the numbers that production needs.
    /// </summary>
    public bool EnforceLimits { get; set; } = true;

    /// <summary>
    /// The largest number of SQL command texts whose shapes the guard keeps, so that it normalizes each
    /// text once. When the cache is full, it starts again from empty. Zero turns the cache off, and
    /// every command is normalized again.
    /// </summary>
    public int SqlShapeCacheSize { get; set; } = 1000;
}
