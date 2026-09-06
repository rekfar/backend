using System.Threading.RateLimiting;
using Azure.Communication.Email;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Rekfar.Accounts;

/// <summary>Named rate-limiting policies this module applies to its own endpoints.</summary>
public static class AccountsRateLimitPolicies
{
    /// <summary>
    /// Requesting a sign-in code, per caller. Registered by the module rather than the host
    /// because the limit is a property of the sign-in flow (NFR-SEC-4), not of the API's
    /// public surface — an endpoint that sends email on an anonymous caller's say-so cannot
    /// share a budget with a map query.
    /// </summary>
    public const string SignInCode = "signin-code";
}

/// <summary>
/// The Auth &amp; Account module's registration surface. The host composes modules; it does
/// not reach inside them.
/// </summary>
public static class AccountsModule
{
    /// <summary>Name of the connection string in configuration.</summary>
    public const string ConnectionStringName = "Rekfar";

    /// <summary>
    /// The session cookie. The <c>__Host-</c> prefix is enforced by the browser: it will only
    /// keep the cookie if it is <c>Secure</c>, path <c>/</c> and has no <c>Domain</c>, which
    /// together mean no sibling subdomain can set or overwrite it.
    /// </summary>
    private const string CookieName = "__Host-rekfar.session";

    public static IServiceCollection AddAccountsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"No '{ConnectionStringName}' connection string is configured. Set it with "
                + $"`dotnet user-secrets set \"ConnectionStrings:{ConnectionStringName}\" \"<value>\"` "
                + "locally, or in the container app's configuration.");
        }

        var options = ReadOptions(configuration);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SignInCodeLedger>();

        AddDatabase(services, configuration, connectionString);
        AddIdentity(services, options);
        AddEmail(services, configuration, environment);

        services.AddRateLimiter(limiter => limiter.AddPolicy(
            AccountsRateLimitPolicies.SignInCode,
            context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.CodeRequestRateLimit,
                    Window = options.CodeRequestRateLimitWindow,
                    QueueLimit = 0,
                })));

        return services;
    }

    private static void AddDatabase(
        IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var commandTimeout = configuration.GetValue("Database:CommandTimeoutSeconds", 60);

        services.AddDbContext<AccountsDbContext>(context => context.UseSqlServer(
            connectionString,
            sqlServer =>
            {
                // The free-offer database is serverless and auto-pauses. Signing in is exactly
                // the request most likely to be the first one after a quiet period, so it is
                // the one that most depends on a resume being retried rather than failing.
                sqlServer.EnableRetryOnFailure(
                    maxRetryCount: 6,
                    maxRetryDelay: TimeSpan.FromSeconds(30),
                    errorNumbersToAdd: null);

                sqlServer.CommandTimeout(commandTimeout);
            }));
    }

    private static void AddIdentity(IServiceCollection services, AccountsOptions options)
    {
        services.AddIdentityCore<RekfarUser>(identity =>
        {
            // One account per address (FR-ACC-1). UX_auth_User_NormalizedEmail enforces it in
            // the database; this is what turns a violation into a message instead of a 500.
            identity.User.RequireUniqueEmail = true;

            // The user name is the email address, and Identity's default character allowlist
            // rejects addresses that are perfectly legal. UserEmail is what validates them.
            identity.User.AllowedUserNameCharacters = string.Empty;

            // A user only ever reaches a session by proving they read the address, and that
            // proof is what sets EmailConfirmed. Requiring it here means a row that has only
            // ever been sent a code cannot become a session by some other route.
            identity.SignIn.RequireConfirmedAccount = true;
        })
            .AddEntityFrameworkStores<AccountsDbContext>()

            // The one token provider this product uses. Not AddDefaultTokenProviders(), which
            // would also register providers for phone numbers, authenticator apps and
            // data-protected links that nothing here issues.
            //
            // It is TOTP-style: the code is derived from the user's security stamp and the
            // clock, so nothing is written down and no token table is needed — which is the
            // assumption rekfar/database#6 is written on.
            .AddTokenProvider<EmailTokenProvider<RekfarUser>>(TokenOptions.DefaultEmailProvider)
            .AddSignInManager()
            .AddClaimsPrincipalFactory<RekfarUserClaimsPrincipalFactory>();

        // AddIdentityCore does not register these; the cookies' OnValidatePrincipal resolves
        // them from the container, and without them a request would fail rather than
        // revalidate. The two-factor one is registered for the same reason its cookie is: it
        // is never used, and a missing registration behind an unused path is still a 500
        // waiting for the day somebody presents that cookie.
        services.TryAddScoped<ISecurityStampValidator, SecurityStampValidator<RekfarUser>>();
        services.TryAddScoped<ITwoFactorSecurityStampValidator, TwoFactorSecurityStampValidator<RekfarUser>>();

        services.Configure<SecurityStampValidatorOptions>(validator =>
            validator.ValidationInterval = options.SecurityStampValidationInterval);

        // All four Identity cookie schemes, not just the application one — and not because
        // external logins or two-factor exist. Identity's own sign-out path addresses every
        // scheme it knows about, including from inside the security-stamp validator when it
        // rejects a revoked session, and signing out of a scheme nobody registered throws.
        // Registering them costs four handlers that are never challenged; leaving them out
        // would put an exception on the revocation path, which is the last place to want one.
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();

        // After AddApplicationCookie, so this configures the options object that already
        // carries its OnValidatePrincipal — replacing Events wholesale would remove it.
        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = CookieName;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.SameSite = options.CookieSameSite;
            cookie.Cookie.Path = "/";

            cookie.ExpireTimeSpan = options.SessionLifetime;
            cookie.SlidingExpiration = true;

            // This is an API. The default cookie handler answers an unauthenticated call with
            // a 302 to a login page that does not exist here, which reaches a fetch() as an
            // opaque CORS failure rather than as "you are not signed in".
            cookie.Events.OnRedirectToLogin = ToStatusCode(StatusCodes.Status401Unauthorized);
            cookie.Events.OnRedirectToAccessDenied = ToStatusCode(StatusCodes.Status403Forbidden);
        });

        services.AddAuthorization();
    }

    private static void AddEmail(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var email = new SignInEmailOptions
        {
            Endpoint = configuration["Auth:Email:Endpoint"] ?? string.Empty,
            SenderAddress = configuration["Auth:Email:SenderAddress"] ?? string.Empty,
            ManagedIdentityClientId = configuration["Auth:Email:ManagedIdentityClientId"] ?? string.Empty,
        };

        services.AddSingleton(email);

        if (email.IsConfigured)
        {
            services.AddSingleton(_ => new EmailClient(new Uri(email.Endpoint), Credential(email)));
            services.AddSingleton<ISignInCodeSender, AcsSignInCodeSender>();
            return;
        }

        if (environment.IsProduction())
        {
            // Configuration, not connectivity — and the failure it prevents is worse than not
            // starting: with no provider the only sender left writes login codes to the log.
            throw new InvalidOperationException(
                "No email provider is configured. Set 'Auth:Email:Endpoint' and "
                + "'Auth:Email:SenderAddress' — infra/main.bicep sets both from the Azure "
                + "Communication Services resource it creates. Without them nobody can sign in.");
        }

        services.AddSingleton<ISignInCodeSender, LogOnlySignInCodeSender>();
    }

    /// <summary>
    /// Names the identity rather than discovering it, for the reason the SQL connection string
    /// gives: <c>DefaultAzureCredential</c> would probe a chain of credential sources at
    /// startup to arrive at the one we already know. The fallback is for a developer signed in
    /// with <c>az login</c>, where there is no managed identity to name.
    /// </summary>
    private static TokenCredential Credential(SignInEmailOptions email) =>
        string.IsNullOrWhiteSpace(email.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(email.ManagedIdentityClientId));

    private static Func<RedirectContext<CookieAuthenticationOptions>, Task> ToStatusCode(int statusCode) =>
        context =>
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        };

    private static AccountsOptions ReadOptions(IConfiguration configuration)
    {
        var defaults = new AccountsOptions();

        return new AccountsOptions
        {
            SessionLifetime = TimeSpan.FromDays(
                configuration.GetValue("Auth:SessionDays", defaults.SessionLifetime.TotalDays)),
            SecurityStampValidationInterval = TimeSpan.FromSeconds(
                configuration.GetValue(
                    "Auth:SecurityStampValidationSeconds",
                    defaults.SecurityStampValidationInterval.TotalSeconds)),
            CookieSameSite = Enum.TryParse<SameSiteMode>(
                configuration["Auth:CookieSameSite"],
                ignoreCase: true,
                out var sameSite)
                    ? sameSite
                    : defaults.CookieSameSite,
            MaxVerificationAttempts = configuration.GetValue(
                "Auth:MaxVerificationAttempts", defaults.MaxVerificationAttempts),
            VerificationWindow = TimeSpan.FromMinutes(
                configuration.GetValue(
                    "Auth:VerificationWindowMinutes", defaults.VerificationWindow.TotalMinutes)),
            MaxCodeRequestsPerAddress = configuration.GetValue(
                "Auth:MaxCodeRequestsPerAddress", defaults.MaxCodeRequestsPerAddress),
            CodeRequestWindow = TimeSpan.FromMinutes(
                configuration.GetValue(
                    "Auth:CodeRequestWindowMinutes", defaults.CodeRequestWindow.TotalMinutes)),
            UsedCodeMemory = TimeSpan.FromMinutes(
                configuration.GetValue(
                    "Auth:UsedCodeMemoryMinutes", defaults.UsedCodeMemory.TotalMinutes)),
            CodeRequestRateLimit = configuration.GetValue(
                "Auth:CodeRequestRateLimit", defaults.CodeRequestRateLimit),
            CodeRequestRateLimitWindow = TimeSpan.FromMinutes(
                configuration.GetValue(
                    "Auth:CodeRequestRateLimitWindowMinutes",
                    defaults.CodeRequestRateLimitWindow.TotalMinutes)),
        };
    }
}
