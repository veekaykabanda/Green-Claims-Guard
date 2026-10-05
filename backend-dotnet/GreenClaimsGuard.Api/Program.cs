using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Endpoints;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Security;
using GreenClaimsGuard.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

// loads the .env file (keeps secrets out of appsettings.json), NoClobber means a real env var always wins over the file so nothing gets silently overwritten
DotNetEnv.Env.NoClobber().Load();

var builder = WebApplication.CreateBuilder(args);

// Map .env variables into configuration
var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (!string.IsNullOrEmpty(openAiKey))
    builder.Configuration["OpenAI:ApiKey"] = openAiKey;

// set to true when there's only one senior editor, lets them sign off their own submissions (flagged in the audit trail), otherwise they can't
var singleEditorMode = Environment.GetEnvironmentVariable("SINGLE_SENIOR_EDITOR");
if (!string.IsNullOrEmpty(singleEditorMode))
    builder.Configuration["Policy:SingleSeniorEditor"] = singleEditorMode;

var sqlConn = Environment.GetEnvironmentVariable("SQL_SERVER_CONNECTION");
if (!string.IsNullOrEmpty(sqlConn))
    builder.Configuration["ConnectionStrings:SqlServer"] = sqlConn;

// auth0 jwt validation is always on, not opt in, if auth0 isn't configured we use a placeholder authority so no token can ever pass and endpoints fail closed with 401 instead of letting people in
var auth0Domain   = Environment.GetEnvironmentVariable("AUTH0_DOMAIN")   ?? "";
var auth0Audience = Environment.GetEnvironmentVariable("AUTH0_AUDIENCE")  ?? "";
var auth0Enabled  = !string.IsNullOrEmpty(auth0Domain) && !string.IsNullOrEmpty(auth0Audience);

// stops here with a plain message instead of starting up looking fine, production without auth0 means nobody could be told apart
StartupChecks.EnsureSafeToStart(builder.Environment, auth0Domain, auth0Audience);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = auth0Enabled ? $"https://{auth0Domain}/" : "https://auth0-not-configured.invalid/";
        options.Audience  = auth0Enabled ? auth0Audience : "auth0-not-configured";
        // keeps claim names exactly as auth0 sends them, by default the handler renames "sub" to a long schema uri which left Identity.Name empty and broke the per-user rate limit
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            NameClaimType = "sub"
        };
    });

builder.Services.AddGreenClaimsAuthorization();

// Configure JSON serialization to use camelCase (match JS frontend expectations)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

// CORS, only allows the web app's own origin(s) from ALLOWED_WEB_ORIGINS, falls back to local dev origins if that's not set
var allowedOrigins = (Environment.GetEnvironmentVariable("ALLOWED_WEB_ORIGINS") ?? "http://localhost:8081,http://localhost:3000")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              // browser only lets the page read these response headers if the server names them
              .WithExposedHeaders("X-Total-Count", "Content-Disposition");
    });
});

// OpenAPI/Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "GreenClaimGuardAPI",
        Version = "v1"
    });
});

// EF Core - SQL Server (optional, only if connection string is set)
var connectionString = builder.Configuration.GetConnectionString("SqlServer");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlServer(connectionString));
}
else
{
    // registers a dbcontext that does nothing, DbService checks the _configured flag before using it
    builder.Services.AddScoped<AppDbContext>(sp =>
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer("Server=(localdb)\\placeholder;Database=unused;");
        return new AppDbContext(optionsBuilder.Options);
    });
}

// Register services
builder.Services.AddSingleton<IRuleEngineService, RuleEngineService>();
builder.Services.AddSingleton<AiHealthTracker>();
builder.Services.AddSingleton<AiBudget>();
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddHostedService<DraftCleanupService>();
builder.Services.AddSingleton<IAiComplianceClient, OpenAiComplianceClient>();
builder.Services.AddSingleton<ILlmService, LlmService>();
builder.Services.AddSingleton<IProductFactsProvider, ProductFactsProvider>();
builder.Services.AddSingleton<ICaseReferenceService, CaseReferenceService>();
builder.Services.AddSingleton<IAggregationService, AggregationService>();
builder.Services.AddSingleton<IDbService, DbService>();
builder.Services.AddSingleton<IComplianceOrchestrator, ComplianceOrchestrator>();
builder.Services.AddScoped<IAuditLedger, AuditLedger>();
builder.Services.AddSingleton<IRegulationUpdateService, RegulationUpdateService>();
builder.Services.AddScoped<IPublicClaimsSeedService, PublicClaimsSeedService>();

// rate limiting, a generous ceiling on analyse requests since the editor checks rules as you type, the openai budget is a separate limit handled by AiBudget which only counts requests that use the ai
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("analyze", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User?.Identity?.Name
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests", message = "You have exceeded the analysis limit. Please wait a minute before trying again." },
            token);
    };
});

// ListenAnyIP binds both IPv4 and IPv6, plain UseUrls only binds IPv4 and windows resolves "localhost" to IPv6 first, so the browser's first connection attempt kept getting refused
var port = builder.Configuration.GetValue<int?>("Port") ?? 8080;
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));

var app = builder.Build();
var defaultPublicClaimsSeedPath = Path.Combine(
    app.Environment.ContentRootPath,
    "Rules",
    "public_claims_seed_fixed.csv");

// `dotnet ef` builds the host just to find the DbContext, it should not migrate or seed the db while doing that
var runningUnderEfTools = string.Equals(
    System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, "ef", StringComparison.OrdinalIgnoreCase);

// Apply migrations if using SQL Server
if (!string.IsNullOrWhiteSpace(connectionString) && !runningUnderEfTools)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.Migrate();
        app.Logger.LogInformation("Database migrations applied successfully");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not connect to SQL Server; DB logging will be unavailable");
    }

    try
    {
        var seedService = scope.ServiceProvider.GetRequiredService<IPublicClaimsSeedService>();
        if (File.Exists(defaultPublicClaimsSeedPath))
        {
            var seedResult = await seedService.SeedIfEmptyAsync(defaultPublicClaimsSeedPath);
            app.Logger.LogInformation(
                "Public claims seed startup check: {Message} (Inserted: {Inserted}, Updated: {Updated})",
                seedResult.Message,
                seedResult.Inserted,
                seedResult.Updated);
        }
        else
        {
            app.Logger.LogWarning("Public claims seed CSV not found at {Path}", defaultPublicClaimsSeedPath);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Public claims seed startup import failed.");
    }
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();

// always in the pipeline, every endpoint needs a signed in user by default (FallbackPolicy in AddGreenClaimsAuthorization), not just when auth0 happens to be set up
app.UseAuthentication();
// Remember who each signed-in person is (their email, from the login token) so screens can name them.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        await context.RequestServices.GetRequiredService<UserDirectory>().RecordAsync(
            context.User,
            context.RequestServices.GetRequiredService<AppDbContext>(),
            context.RequestServices.GetRequiredService<IDbService>());
    }
    await next();
});
// placed after authentication on purpose, the "analyze" limit is per signed in user, before this point the limiter can't see who they are and would fall back to one shared bucket per ip
app.UseRateLimiter();
app.UseAuthorization();

// health check, the only public endpoint (azure uses it to check the app is alive)
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// GET /api/me, who the server thinks you are, the screen labels people from this so what you're called and what you can do come from the same place, no role just gets told that
app.MapGet("/api/me", (HttpContext http) =>
{
    var persona = Personas.Of(http.User);
    return Results.Ok(new
    {
        userId = CurrentUser.GetId(http.User),
        persona,
        label = Personas.LabelOf(persona),
        canPublish = persona == Personas.SeniorEditor,
        permissions = http.User.FindAll(AuthorizationSetup.PermissionsClaimType).Select(c => c.Value).ToArray()
    });
}).RequireAuthorization(AuthorizationSetup.AuthenticatedPolicy);

// GET /api/status, what the health ribbon shows (rules loaded, ai check available, database reachable), signed in users only since it names internal state
app.MapGet("/api/status", async (IRuleEngineService rules, AiHealthTracker aiHealth, IDbService dbService, HttpContext http) =>
{
    // copywriters just need to know if the ai check works right now, rule versions and the database are the senior editor's business
    if (Personas.Of(http.User) != Personas.SeniorEditor)
    {
        var (writerAiStatus, _) = aiHealth.Snapshot();
        var available = writerAiStatus == EngineStatus.Ok;
        return Results.Ok(new
        {
            checkedAt = DateTime.UtcNow,
            ai = new { available, label = available ? "AI check available" : "AI check unavailable" }
        });
    }

    var ukRules = rules.RuleCountFor(Markets.Uk);
    var euRules = rules.RuleCountFor(Markets.Eu);
    var (aiStatus, aiLastCheckedAt) = aiHealth.Snapshot();

    var dbStatus = !dbService.IsConfigured
        ? EngineStatus.NotConfigured
        : await dbService.IsAvailableAsync() ? EngineStatus.Ok : EngineStatus.Unavailable;

    return Results.Ok(new
    {
        checkedAt = DateTime.UtcNow,
        rules = new
        {
            status = ukRules > 0 && euRules > 0 ? EngineStatus.Ok : EngineStatus.Failed,
            ukVersion = rules.RulesVersionFor(Markets.Uk),
            euVersion = rules.RulesVersionFor(Markets.Eu),
            ukRuleCount = ukRules,
            euRuleCount = euRules
        },
        ai = new { status = aiStatus, available = aiStatus == EngineStatus.Ok, lastCheckedAt = aiLastCheckedAt },
        database = new { status = dbStatus }
    });
}).RequireAuthorization();

// POST /api/extract-doc-facts, pulls product facts out of an uploaded pdf or text file and checks them
app.MapPost("/api/extract-doc-facts", async (
    HttpRequest httpRequest,
    ILlmService llmService,
    ILogger<Program> logger) =>
{
    try
    {
        if (!httpRequest.HasFormContentType)
            return Results.BadRequest(new { error = "Multipart form expected" });

        var form = await httpRequest.ReadFormAsync();
        var file = form.Files.GetFile("file");
        var claimType = form["claimType"].ToString().Trim();
        var originalClaim = form["originalClaim"].ToString().Trim();

        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "No file provided" });

        if (file.Length > 10 * 1024 * 1024) // 10MB limit
            return Results.BadRequest(new { error = "File too large (max 10MB)" });

        // pulls the text out of the document
        string documentText;
        var fileName = file.FileName?.ToLowerInvariant() ?? "";

        if (fileName.EndsWith(".pdf"))
        {
            using var stream = file.OpenReadStream();
            using var memStream = new MemoryStream();
            await stream.CopyToAsync(memStream);
            memStream.Position = 0;

            var sb = new System.Text.StringBuilder();
            using var pdfDocument = UglyToad.PdfPig.PdfDocument.Open(memStream.ToArray());
            foreach (var page in pdfDocument.GetPages())
            {
                sb.AppendLine(string.Join(" ", page.GetWords().Select(w => w.Text)));
            }
            documentText = sb.ToString().Trim();
        }
        else
        {
            // plain text or anything else readable
            using var reader = new StreamReader(file.OpenReadStream());
            documentText = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(documentText))
            return Results.BadRequest(new { error = "Could not extract text from document" });

        if (documentText.Length > 12000)
            documentText = documentText[..12000];

        // checks the document against CMA compliance requirements
        var validationResult = await llmService.ValidateDocumentClaimAsync(
            documentText,
            claimType,
            string.IsNullOrWhiteSpace(originalClaim) ? null : originalClaim);

        return Results.Ok(new
        {
            validationStatus = validationResult.ValidationStatus,
            validationFeedback = validationResult.ValidationFeedback,
            materialComposition = validationResult.ExtractedFacts?.MaterialComposition,
            certificationsHeld = validationResult.ExtractedFacts?.CertificationsHeld,
            additionalFacts = validationResult.ExtractedFacts?.AdditionalFacts,
            requiredFields = validationResult.RequiredFields
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error extracting doc facts");
        return Results.Problem("Failed to extract facts from document");
    }
}).RequireAuthorization();

// GET /api/regulation-updates, returns cached gov.uk/CMA green claims news
app.MapGet("/api/regulation-updates", async (IRegulationUpdateService regulationService) =>
{
    var updates = await regulationService.GetUpdatesAsync();
    return Results.Ok(updates);
}).RequireAuthorization();

// POST /api/analyze
app.MapPost("/api/analyze", async (
    AnalyzeRequest? request,
    IComplianceOrchestrator orchestrator,
    IDbService dbService,
    IAuditLedger ledger,
    AppDbContext db,
    AiBudget aiBudget,
    ProductAccess productAccess,
    HttpContext http,
    ILogger<Program> logger) =>
{
    try
    {
        if (request?.Text is null)
        {
            return Results.BadRequest(new
            {
                error = "Missing or invalid request body",
                message = "Request must include JSON body with \"text\" field."
            });
        }

        var text = request.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Results.BadRequest(new
            {
                error = "Invalid input",
                message = "\"text\" cannot be empty."
            });
        }

        // caps input length so it doesn't blow up token costs or slow things down
        if (text.Length > 5000)
        {
            return Results.BadRequest(new
            {
                error = "Input too long",
                message = "Product description must be 5,000 characters or fewer."
            });
        }

        if (request.ProductName is { Length: > 200 })
        {
            return Results.BadRequest(new
            {
                error = "Input too long",
                message = "Product name must be 200 characters or fewer."
            });
        }

        if (!Markets.TryNormalise(request.Market, out var market))
        {
            return Results.BadRequest(new
            {
                error = "Unsupported market",
                message = "Market must be UK or EU."
            });
        }

        // only ai requests count against the budget, the rules run as you type and cost nothing
        if (!request.RulesOnly && !aiBudget.TryTake(CurrentUser.GetId(http.User) ?? "anonymous"))
        {
            return Results.Json(new
            {
                error = "Too many requests",
                message = "You have used your AI checks for this minute. The rules keep checking as you type; try the AI check again shortly."
            }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        // a check on a product is only allowed for someone who can open that product, otherwise its facts could be probed through someone else's product, an unknown product is just ignored and the rules still run
        Guid? productId = null;
        if (request.ProductId is { } claimedProductId && dbService.IsConfigured)
        {
            try
            {
                var opened = await productAccess.OpenAsync(claimedProductId, http.User);
                if (opened.Outcome == ProductAccessOutcome.Forbidden) return opened.Failure;
                if (opened.IsOk) productId = claimedProductId;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not look up the product for a check; continuing without it");
            }
        }

        var result = await orchestrator.EvaluateAsync(new ComplianceRequest
        {
            Text = text,
            Action = ComplianceAction.Analyze,
            RulesOnly = request.RulesOnly,
            Industry = request.Industry,
            Decisions = request.IssueDecisions ?? new List<IssueDecisionInput>(),
            ProductFacts = request.ProductFacts,
            ProductId = productId,
            ProductName = request.ProductName,
            ProductCategory = request.ProductCategory,
            ProductSubcategory = request.ProductSubcategory,
            ProductTags = request.ProductTags,
            Market = market
        });

        // only logs manual checks, not live typing, the audit write times out at 3s so a slow database can't hang the whole response
        if (string.Equals(request.Trigger, "manual", StringComparison.OrdinalIgnoreCase))
        {
            var recordTask = ledger.TryRecordAsync(AuditEntryFactory.From(
                AuditActions.Check, AuditOutcomes.Ok, text, result, CurrentUser.GetId(http.User), productId));
            await Task.WhenAny(recordTask, Task.Delay(TimeSpan.FromSeconds(3)));

            _ = Task.Run(async () =>
            {
                try
                {
                    await dbService.LogAnalysisAsync(text, result, result.RuleFindings);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to log analysis to SQL Server");
                }
            });
        }

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Analyze error");
        return Results.Json(new AnalyzeResponse
        {
            OverallStatus = ComplianceStatus.ChangesRequired,
            OverallRisk = "Low",
            TrafficLight = "\U0001F7E2",
            AiExplanation = $"Error: {ex.Message}. Please check backend logs.",
            SuggestedRewrite = ""
        }, statusCode: 500);
    }
}).RequireRateLimiting("analyze").RequireAuthorization();

// POST /api/data-transfer/seed needs senior editor level trust (publish:product), same trust boundary as mark-ready, not just any logged in staff
app.MapPost("/api/data-transfer/seed", async (IPublicClaimsSeedService seedService, IWebHostEnvironment env, IAuditLedger ledger, HttpContext http) =>
{
    var userId = CurrentUser.GetId(http.User);
    if (!DataImportRules.IsAllowed(env))
    {
        await ledger.TryRecordAsync(DataImportRules.AuditRow(userId, false, "Seed import refused: " + DataImportRules.RefusedDetail));
        return Problems.Conflict(DataImportRules.RefusedDetail);
    }

    // same file the app seeds itself from on startup, used to look in a Data folder that didn't have it
    var result = await seedService.ImportAsync(defaultPublicClaimsSeedPath);
    await ledger.TryRecordAsync(DataImportRules.AuditRow(userId, result.Success, $"Seed import: {result.Message}"));
    return Results.Ok(result);
}).RequireAuthorization("RequirePublishPermission");

// GET /api/claims/recent
app.MapGet("/api/claims/recent", async (AppDbContext db) =>
{
    var recent = await db.AnalysisLogs
        .AsNoTracking()
        .OrderByDescending(l => l.CreatedAt)
        .Take(50)
        .ToListAsync();
    return Results.Ok(recent);
}).RequireAuthorization(AuthorizationSetup.PublishPolicy);

// POST /api/products, id is what makes a product unique not the name, so duplicate names are fine and won't overwrite each other in the review workflow
app.MapPost("/api/products", async (CreateProductRequest? request, AppDbContext db, HttpContext http) =>
{
    var name = (request?.Name ?? string.Empty).Trim();
    var sku = string.IsNullOrWhiteSpace(request?.Sku) ? null : request!.Sku!.Trim();

    if (name.Length == 0)
        return Results.BadRequest(new { message = "Product name is required." });
    if (name.Length > 200)
        return Results.BadRequest(new { message = "Product name must be 200 characters or fewer." });
    if (sku is { Length: > 64 })
        return Results.BadRequest(new { message = "SKU must be 64 characters or fewer." });

    var product = new Product
    {
        Id = Guid.NewGuid(),
        Name = name,
        Sku = sku,
        CreatedAt = DateTime.UtcNow,
        CreatedByUserId = CurrentUser.GetId(http.User)
    };

    db.Products.Add(product);
    await db.SaveChangesAsync();

    return Results.Created($"/api/products/{product.Id}", new { id = product.Id, name = product.Name, sku = product.Sku, createdAt = product.CreatedAt });
}).RequireAuthorization();

// GET /api/products/{id}
app.MapGet("/api/products/{id:guid}", async (Guid id, AppDbContext db, ProductAccess access, HttpContext http) =>
{
    var opened = await access.OpenAsync(id, http.User);
    if (!opened.IsOk) return opened.Failure;

    var product = opened.Product!;
    var newest = await db.ComplianceReviews.AsNoTracking()
        .Where(r => r.ProductId == id)
        .OrderByDescending(r => r.Id)
        .Select(r => new { r.OverallStatus, r.SendBackReason, r.SendBackComment, r.CreatedAt })
        .FirstOrDefaultAsync();

    return Results.Ok(new
    {
        id = product.Id,
        name = product.Name,
        sku = product.Sku,
        createdAt = product.CreatedAt,
        status = ProductStatuses.FromReview(newest?.OverallStatus),
        // why it got sent back, so the editor can show it above the copy, null if it wasn't
        sendBack = newest is null ? null : SendBackEndpoints.Describe(newest.OverallStatus, newest.SendBackReason, newest.SendBackComment, newest.CreatedAt),
    });
}).RequireAuthorization();

// PATCH /api/products/{id}, renames it, only the creator or a senior editor can
app.MapPatch("/api/products/{id:guid}", async (Guid id, RenameProductRequest? request, AppDbContext db, ProductAccess access, HttpContext http) =>
{
    var name = (request?.Name ?? string.Empty).Trim();
    if (name.Length == 0)
        return Results.BadRequest(new { message = "Product name is required." });
    if (name.Length > 200)
        return Results.BadRequest(new { message = "Product name must be 200 characters or fewer." });

    var opened = await access.OpenAsync(id, http.User, track: true);
    if (!opened.IsOk) return opened.Failure;

    // The name is part of the copy, so it is frozen along with the description.
    var current = await ProductStatuses.CurrentAsync(db, id);
    if (ProductStatuses.IsLocked(current)) return Problems.Conflict(ProductStatuses.LockedMessage(current));

    var product = opened.Product!;
    product.Name = name;
    await db.SaveChangesAsync();
    return Results.Ok(new { id = product.Id, name = product.Name, sku = product.Sku, createdAt = product.CreatedAt });
}).RequireAuthorization();

// GET /api/products/{id}/facts, what's been verified about this product, anyone signed in can read it since the editor shows it next to the copy
app.MapGet("/api/products/{id:guid}/facts", async (Guid id, ProductAccess access, HttpContext http) =>
{
    var opened = await access.OpenAsync(id, http.User, withFacts: true);
    if (!opened.IsOk) return opened.Failure;

    var product = opened.Product!;

    var response = new FactsResponse
    {
        ProductId = product.Id,
        ProductName = product.Name,
        Materials = product.Materials.OrderByDescending(m => m.Percentage)
            .Select(m => new MaterialShare { Material = m.Material, Percentage = m.Percentage }).ToList(),
        Certifications = product.Certifications.Select(c => c.Name).ToList(),
        Origin = product.Origin,
        VerifiedByUserId = product.FactsVerifiedByUserId,
        VerifiedAt = product.FactsVerifiedAt
    };
    response.Status = response.Materials.Count == 0 && response.Certifications.Count == 0 && string.IsNullOrWhiteSpace(response.Origin)
        ? FactsStatus.NoneOnFile
        : FactsStatus.Loaded;
    return Results.Ok(response);
}).RequireAuthorization();

// PUT /api/products/{id}/facts, only a senior editor can set what a product is made of since every claim gets measured against it, this replaces the whole list
app.MapPut("/api/products/{id:guid}/facts", async (
    Guid id,
    SaveFactsRequest? request,
    AppDbContext db,
    IProductFactsProvider factsProvider,
    HttpContext http) =>
{
    if (request is null) return Results.BadRequest(new { message = "Missing request payload." });

    var errors = new List<string>();
    var materials = (request.Materials ?? new List<MaterialShare>())
        .Select(m => new MaterialShare { Material = (m.Material ?? "").Trim(), Percentage = m.Percentage })
        .ToList();
    var certifications = (request.Certifications ?? new List<string>())
        .Select(c => (c ?? "").Trim()).Where(c => c.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var origin = string.IsNullOrWhiteSpace(request.Origin) ? null : request.Origin.Trim();

    if (materials.Count > 20) errors.Add("At most 20 materials.");
    if (materials.Any(m => m.Material.Length == 0)) errors.Add("Every material needs a name.");
    if (materials.Any(m => m.Material.Length > 100)) errors.Add("A material name must be 100 characters or fewer.");
    if (materials.Any(m => m.Percentage <= 0 || m.Percentage > 100)) errors.Add("Each percentage must be above 0 and at most 100.");
    if (materials.GroupBy(m => m.Material, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        errors.Add("Each material may be listed once.");
    if (materials.Count > 0 && Math.Abs(materials.Sum(m => m.Percentage) - 100m) > 0.5m)
        errors.Add($"The percentages add up to {materials.Sum(m => m.Percentage):0.##}%. They must total 100%.");
    if (certifications.Count > 30) errors.Add("At most 30 certifications.");
    if (certifications.Any(c => c.Length > 100)) errors.Add("A certification name must be 100 characters or fewer.");
    if (origin is { Length: > 100 }) errors.Add("Origin must be 100 characters or fewer.");
    var reason = (request.Reason ?? string.Empty).Trim();
    if (reason.Length < 10) errors.Add("Say why the facts are changing, in at least 10 characters.");
    if (reason.Length > 1000) errors.Add("The reason must be 1,000 characters or fewer.");

    if (errors.Count > 0) return Results.BadRequest(new { message = string.Join(" ", errors), errors });

    var product = await db.Products
        .Include(p => p.Materials)
        .Include(p => p.Certifications)
        .FirstOrDefaultAsync(p => p.Id == id);
    if (product is null) return Results.NotFound();

    var before = FactsAudit.Snapshot(product.Materials.Select(m => new MaterialShare { Material = m.Material, Percentage = m.Percentage }).ToList(),
        product.Certifications.Select(c => c.Name).ToList(), product.Origin);

    db.RemoveRange(product.Materials);
    db.RemoveRange(product.Certifications);
    product.Materials = materials.Select(m => new ProductMaterial { ProductId = id, Material = m.Material, Percentage = m.Percentage }).ToList();
    product.Certifications = certifications.Select(c => new ProductCertification { ProductId = id, Name = c }).ToList();
    product.Origin = origin;

    var isEmpty = materials.Count == 0 && certifications.Count == 0 && origin is null;
    product.FactsVerifiedByUserId = isEmpty ? null : CurrentUser.GetId(http.User);
    product.FactsVerifiedAt = isEmpty ? null : DateTime.UtcNow;

    // saved together with the change, who changed what, from what, to what and why
    db.AuditLedger.Add(FactsAudit.Row(id, CurrentUser.GetId(http.User), reason, before, FactsAudit.Snapshot(materials, certifications, origin)));
    await db.SaveChangesAsync();
    factsProvider.Invalidate(id);

    return Results.Ok(new FactsResponse
    {
        ProductId = product.Id,
        ProductName = product.Name,
        Status = isEmpty ? FactsStatus.NoneOnFile : FactsStatus.Loaded,
        Materials = materials.OrderByDescending(m => m.Percentage).ToList(),
        Certifications = certifications,
        Origin = origin,
        VerifiedByUserId = product.FactsVerifiedByUserId,
        VerifiedAt = product.FactsVerifiedAt
    });
}).RequireAuthorization(AuthorizationSetup.EditFactsPolicy);

// POST /api/claims/mark-ready, anyone signed in can submit but the server checks the text itself, it never just trusts what the client sends for the text or the checks
app.MapPost("/api/claims/mark-ready", async (
    MarkReadyRequest? request,
    AppDbContext db,
    IDbService dbService,
    IComplianceOrchestrator orchestrator,
    IAuditLedger ledger,
    ProductAccess productAccess,
    HttpContext http,
    ILogger<Program> logger) =>
{
    if (request is null)
    {
        return Results.BadRequest(new MarkReadyResponse { Message = "Missing request payload." });
    }

    if (!Markets.TryNormalise(request.Market, out var market))
    {
        return Results.BadRequest(new MarkReadyResponse { Message = "Market must be UK or EU." });
    }

    var finalDescription = (request.FinalDescription ?? string.Empty).Trim();
    // product details card has no draft to fall back on like the description does, so whatever's sent with this submission is what gets checked and saved
    var submittedCategory = (request.Category ?? string.Empty).Trim();
    var submittedSubcategory = (request.Subcategory ?? string.Empty).Trim();
    var submittedTags = (request.Tags ?? string.Empty).Trim();
    var errors = new List<string>();
    var userId = CurrentUser.GetId(http.User);
    Guid? knownProductId = null;
    string? submittedName = null;
    var usedDraft = false;

    try
    {
        if (request.ProductId == Guid.Empty)
            errors.Add("productId is required.");
        else if (!dbService.IsConfigured)
            errors.Add("The database is not configured, so a submission cannot be saved.");
        else
        {
            var opened = await productAccess.OpenAsync(request.ProductId, http.User);
            if (opened.Outcome == ProductAccessOutcome.Forbidden) return opened.Failure;
            if (opened.IsOk)
            {
                // copy is frozen while in review or published and has to be withdrawn to change, unless it's your own in-review submission, which you can resubmit live without withdrawing
                var current = await ProductStatuses.CurrentAsync(db, request.ProductId);
                if (ProductStatuses.IsLocked(current))
                {
                    var isOwnInReviewEdit = current == ProductStatus.InReview && userId is not null
                        && await db.ComplianceReviews.AsNoTracking()
                            .Where(r => r.ProductId == request.ProductId)
                            .OrderByDescending(r => r.Id)
                            .Select(r => r.ActorUserId)
                            .FirstOrDefaultAsync() == userId;

                    if (!isOwnInReviewEdit)
                    {
                        var locked = ProductStatuses.LockedMessage(current);
                        return Results.Json(new MarkReadyResponse
                        {
                            Message = locked,
                            ValidationErrors = { locked }
                        }, statusCode: StatusCodes.Status409Conflict);
                    }
                }
                knownProductId = request.ProductId;
                submittedName = opened.Product!.Name;

                // submits the writer's saved draft, not whatever the client sends, a copywriter always submits what they saved, a senior editor's sent text wins unless they sent nothing
                var draftApplies = Personas.Of(http.User) == Personas.Copywriter || string.IsNullOrWhiteSpace(request.FinalDescription);
                if (draftApplies && userId is not null && opened.Product.CreatedByUserId == userId)
                {
                    var draft = await db.ProductDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.ProductId == request.ProductId);
                    if (draft is not null)
                    {
                        finalDescription = draft.Text.Trim();
                        market = draft.Market;
                        usedDraft = true;
                    }
                }
            }
            else
            {
                errors.Add("Unknown product. Create the product before submitting it for review.");
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Could not verify the product before submitting");
        return Results.Json(new MarkReadyResponse
        {
            DbStatus = EngineStatus.Unavailable,
            Message = "The database is unavailable, so nothing was submitted.",
            ValidationErrors = { "The database is unavailable, so nothing was submitted." }
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var evaluation = await orchestrator.EvaluateAsync(new ComplianceRequest
    {
        Text = finalDescription,
        Action = ComplianceAction.Submit,
        Decisions = request.IssueDecisions ?? new List<IssueDecisionInput>(),
        ProductFacts = request.ProductFacts,
        ProductId = knownProductId,
        ProductName = submittedName,
        ProductCategory = submittedCategory,
        ProductSubcategory = submittedSubcategory,
        ProductTags = submittedTags,
        Market = market
    });

    errors.AddRange(evaluation.SubmitBlockingReasons);

    var response = new MarkReadyResponse
    {
        RulesStatus = evaluation.RulesStatus,
        AiStatus = evaluation.AiStatus,
        DbStatus = evaluation.DbStatus,
        Market = evaluation.Market,
        RulesVersion = evaluation.RulesVersion,
        UsedDraft = usedDraft,
        ValidationErrors = errors
    };

    if (errors.Count > 0)
    {
        await ledger.TryRecordAsync(AuditEntryFactory.From(
            AuditActions.Submit, AuditOutcomes.Blocked, finalDescription, evaluation, userId, knownProductId,
            detail: string.Join(" | ", errors)));

        response.OverallStatus = ComplianceStatus.ChangesRequired;
        response.Message = "Not submitted. Resolve the items below first.";
        return Results.Ok(response);
    }

    try
    {
        // audit trail is append only, a submission adds a new row instead of changing an old one, the workflow row and its ledger entries save together so one can't exist without the other
        foreach (var decided in evaluation.GroupedFindings.Where(f => f.UserDecision is not null))
        {
            db.AuditLedger.Add(AuditEntryFactory.From(
                decided.UserDecision == ComplianceDecision.AppliedSuggestion ? AuditActions.Apply : AuditActions.Keep,
                AuditOutcomes.Ok, finalDescription, evaluation, userId, request.ProductId,
                justification: decided.UserJustification,
                detail: $"{decided.IssueId}: {decided.MatchedPatterns.FirstOrDefault()}"
                    + (decided.UserDecision == ComplianceDecision.KeptOriginalWithJustification && string.Equals(decided.Severity, "high", StringComparison.OrdinalIgnoreCase)
                        ? " (critical, kept: needs a Senior Editor override to publish)"
                        : string.Empty)));
        }
        // applying a rewrite changes the sentence so it can't be matched back to a finding, still logged as who replaced what, though the server just trusts the client that it was applied
        var matchedIssueIds = evaluation.GroupedFindings.Select(f => f.IssueId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var applied in (request.IssueDecisions ?? new List<IssueDecisionInput>())
                     .Where(d => d.UserDecision == ComplianceDecision.AppliedSuggestion
                                 && !string.IsNullOrWhiteSpace(d.IssueId)
                                 && !matchedIssueIds.Contains(d.IssueId.Trim()))
                     .Take(50))
        {
            db.AuditLedger.Add(AuditEntryFactory.From(
                AuditActions.Apply, AuditOutcomes.Ok, finalDescription, evaluation, userId, request.ProductId,
                detail: $"{applied.IssueId}: rewrite applied by the writer (reported by the client; the flagged wording is no longer in the submitted text)"));
        }

        db.AuditLedger.Add(AuditEntryFactory.From(
            AuditActions.Submit, AuditOutcomes.Ok, finalDescription, evaluation, userId, request.ProductId,
            detail: $"Product name: {submittedName}"));

        // submission replaces whatever draft the writer had of this product
        var draft = await db.ProductDrafts.FindAsync(request.ProductId);
        if (draft is not null) db.ProductDrafts.Remove(draft);

        db.ComplianceReviews.Add(new ComplianceReview
        {
            ProductId = request.ProductId,
            ActorUserId = userId,
            Market = evaluation.Market,
            RulesVersion = evaluation.RulesVersion,
            AiStatus = evaluation.AiStatus,
            OpenIssueCount = evaluation.OpenIssueCount,
            FinalDescription = finalDescription,
            OverallStatus = ComplianceStatus.ReadyToPublishSubjectToReview,
            NeedsOverride = evaluation.NeedsOverride,
            ProductName = submittedName,
            Category = submittedCategory.Length > 0 ? submittedCategory : null,
            Subcategory = submittedSubcategory.Length > 0 ? submittedSubcategory : null,
            Tags = submittedTags.Length > 0 ? submittedTags : null,
            // what each decision was about, as the server saw it, not how the client described it
            DecisionsJson = JsonSerializer.Serialize(DecisionSnapshot.From(request.IssueDecisions, evaluation)),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to save the submission");
        response.DbStatus = EngineStatus.Unavailable;
        response.OverallStatus = ComplianceStatus.ChangesRequired;
        response.Message = "The submission could not be saved because the database is unavailable. Nothing was submitted.";
        response.ValidationErrors = new List<string> { response.Message };
        return Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    response.OverallStatus = ComplianceStatus.ReadyToPublishSubjectToReview;
    response.ReadyToPublishSubjectToReview = true;
    response.NeedsOverride = evaluation.NeedsOverride;
    response.Message = evaluation.AiStatus == EngineStatus.Ok
        ? "Submitted for review."
        : "Submitted for review. The AI check did not complete, so it must be re-run before publishing.";
    if (evaluation.NeedsOverride)
    {
        response.Message += " You kept a critical issue, so a Senior Editor must override before this can be published.";
    }
    return Results.Ok(response);
})
// any signed in staff member can submit for sign off, this only ever gets to ReadyToPublishSubjectToReview, never Published, publishing is a separate higher trust action below
.RequireAuthorization();

// GET /api/claims/pending-review, items waiting on a senior editor's sign off, the latest audit row per product where it was submitted but not published yet
app.MapGet("/api/claims/pending-review", async (AppDbContext db, HttpContext http, IConfiguration config) =>
{
    // table is append only, so the highest id per product is its most recent action
    var latestIdPerProduct = db.ComplianceReviews
        .GroupBy(r => r.ProductId)
        .Select(g => g.Max(r => r.Id));

    var pending = await db.ComplianceReviews
        .Where(r => latestIdPerProduct.Contains(r.Id)
                    && r.OverallStatus == ComplianceStatus.ReadyToPublishSubjectToReview)
        .Join(db.Products, r => r.ProductId, p => p.Id, (r, p) => new PendingReviewItem
        {
            ProductId = p.Id,
            ProductName = p.Name,
            FinalDescription = r.FinalDescription,
            SubmittedAt = r.CreatedAt,
            SubmittedByUserId = r.ActorUserId,
            SubmittedByEmail = db.UserDirectory.Where(u => u.UserId == r.ActorUserId).Select(u => u.Email).FirstOrDefault(),
            Market = r.Market,
            AiStatus = r.AiStatus,
            OpenIssueCount = r.OpenIssueCount,
            NeedsOverride = r.NeedsOverride
        })
        .OrderByDescending(item => item.SubmittedAt)
        .ToListAsync();

    // a senior editor can't sign off what they submitted themselves, unless they're the only one
    var me = CurrentUser.GetId(http.User);
    var singleEditor = config.GetValue<bool>("Policy:SingleSeniorEditor");
    foreach (var item in pending)
    {
        item.IsOwnSubmission = me is not null && item.SubmittedByUserId == me;
        item.CanSignOff = !item.IsOwnSubmission || singleEditor;
    }

    return Results.Ok(pending);
// anyone signed in can see the full team queue, not just a senior editor, publish/override/send-back on this data still needs RequirePublishPermission below
}).RequireAuthorization();

// POST /api/claims/publish, server rechecks the text right now so nothing publishes off an old result, if blocked only a senior editor's written override can push it through
app.MapPost("/api/claims/publish", async (
    PublishRequest? request,
    AppDbContext db,
    IComplianceOrchestrator orchestrator,
    IAuditLedger ledger,
    IConfiguration config,
    HttpContext http,
    ILogger<Program> logger) =>
{
    const int minOverrideReasonLength = 10;
    var userId = CurrentUser.GetId(http.User);

    var productId = request?.ProductId ?? Guid.Empty;
    if (productId == Guid.Empty)
    {
        return Results.BadRequest(new PublishResponse { Success = false, Message = "productId is required." });
    }

    var latest = await db.ComplianceReviews
        .Where(r => r.ProductId == productId)
        .OrderByDescending(r => r.Id)
        .FirstOrDefaultAsync();

    if (latest is null || latest.OverallStatus != ComplianceStatus.ReadyToPublishSubjectToReview)
    {
        return Results.BadRequest(new PublishResponse
        {
            Success = false,
            Message = "This product has not been submitted for review, or already needs changes."
        });
    }

    // four eyes rule, whoever submitted it can't sign off on it, unless they're the only senior editor, then it's allowed but flagged in the audit trail
    var selfSignOff = userId is not null && latest.ActorUserId == userId;
    if (selfSignOff && !config.GetValue<bool>("Policy:SingleSeniorEditor"))
    {
        const string ownMessage = "You submitted this yourself, so another Senior Editor has to sign it off.";
        await ledger.TryRecordAsync(new AuditEntry
        {
            ProductId = latest.ProductId,
            Action = AuditActions.Publish,
            Outcome = AuditOutcomes.Blocked,
            CopySnapshot = latest.FinalDescription,
            CopyHash = AuditEntryFactory.HashOf(latest.FinalDescription),
            Market = latest.Market ?? Markets.Uk,
            RulesVersion = latest.RulesVersion ?? string.Empty,
            Detail = "Self sign-off refused: " + ownMessage,
            UserId = userId,
        });
        return Results.Json(new PublishResponse { Success = false, Message = ownMessage, BlockingReasons = new List<string> { ownMessage } },
            statusCode: StatusCodes.Status409Conflict);
    }

    List<IssueDecisionInput> decisions;
    try
    {
        decisions = JsonSerializer.Deserialize<List<IssueDecisionInput>>(latest.DecisionsJson) ?? new List<IssueDecisionInput>();
    }
    catch (JsonException)
    {
        decisions = new List<IssueDecisionInput>();
    }

    var evaluation = await orchestrator.EvaluateAsync(new ComplianceRequest
    {
        Text = latest.FinalDescription,
        Action = ComplianceAction.Publish,
        Decisions = decisions,
        ProductId = latest.ProductId,
        ProductName = latest.ProductName,
        ProductCategory = latest.Category,
        ProductSubcategory = latest.Subcategory,
        ProductTags = latest.Tags,
        Market = latest.Market ?? Markets.Uk
    });

    var overrideReason = (request!.OverrideReason ?? string.Empty).Trim();
    var overridden = false;

    if (!evaluation.PublishAllowed)
    {
        var reasons = string.Join(" | ", evaluation.BlockingReasons);

        if (overrideReason.Length == 0)
        {
            await ledger.TryRecordAsync(AuditEntryFactory.From(
                AuditActions.Publish, AuditOutcomes.Blocked, latest.FinalDescription, evaluation, userId, latest.ProductId,
                detail: reasons));

            return Results.Json(new PublishResponse
            {
                Success = false,
                Message = "Publishing is blocked.",
                BlockingReasons = evaluation.BlockingReasons
            }, statusCode: StatusCodes.Status409Conflict);
        }

        if (overrideReason.Length < minOverrideReasonLength)
        {
            await ledger.TryRecordAsync(AuditEntryFactory.From(
                AuditActions.Publish, AuditOutcomes.Blocked, latest.FinalDescription, evaluation, userId, latest.ProductId,
                detail: $"Override refused: reason too short. {reasons}"));

            return Results.Json(new PublishResponse
            {
                Success = false,
                Message = $"An override needs a written reason of at least {minOverrideReasonLength} characters.",
                BlockingReasons = evaluation.BlockingReasons
            }, statusCode: StatusCodes.Status409Conflict);
        }

        // overriding a block is its own permission, on top of being a senior editor
        if (!http.User.HasClaim(AuthorizationSetup.PermissionsClaimType, AuthorizationSetup.OverridePermission))
        {
            await ledger.TryRecordAsync(AuditEntryFactory.From(
                AuditActions.Publish, AuditOutcomes.Blocked, latest.FinalDescription, evaluation, userId, latest.ProductId,
                detail: $"Override refused: the account does not have the {AuthorizationSetup.OverridePermission} permission. {reasons}"));

            return Results.Json(new PublishResponse
            {
                Success = false,
                Message = $"Your account is not allowed to override a block. It needs the {AuthorizationSetup.OverridePermission} permission.",
                BlockingReasons = evaluation.BlockingReasons
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        overridden = true;
    }

    try
    {
        // publication and its ledger entries are saved together
        if (overridden)
        {
            db.AuditLedger.Add(AuditEntryFactory.From(
                AuditActions.Override, AuditOutcomes.Ok, latest.FinalDescription, evaluation, userId, latest.ProductId,
                justification: overrideReason,
                detail: string.Join(" | ", evaluation.BlockingReasons)));
        }
        db.AuditLedger.Add(AuditEntryFactory.From(
            AuditActions.Publish, AuditOutcomes.Ok, latest.FinalDescription, evaluation, userId, latest.ProductId,
            detail: string.Join(" ", new[]
            {
                overridden ? "Published with a senior editor override." : null,
                selfSignOff ? "Self sign-off: the only Senior Editor signed off their own submission." : null,
            }.Where(text => text is not null)) is { Length: > 0 } published ? published : null));

        db.ComplianceReviews.Add(new ComplianceReview
        {
            ProductId = latest.ProductId,
            ActorUserId = userId,
            Market = evaluation.Market,
            RulesVersion = evaluation.RulesVersion,
            AiStatus = evaluation.AiStatus,
            OpenIssueCount = evaluation.OpenIssueCount,
            OverrideReason = overridden ? overrideReason : null,
            ProductName = latest.ProductName,
            Category = latest.Category,
            Subcategory = latest.Subcategory,
            Tags = latest.Tags,
            FinalDescription = latest.FinalDescription,
            OverallStatus = ComplianceStatus.Published,
            DecisionsJson = latest.DecisionsJson,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to record the publication");
        return Results.Json(new PublishResponse
        {
            Success = false,
            Message = "The publication could not be saved because the database is unavailable. Nothing was published."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new PublishResponse
    {
        Success = true,
        Overridden = overridden,
        SelfSignOff = selfSignOff,
        Message = overridden ? "Published with a senior editor override." : "Published.",
        BlockingReasons = overridden ? evaluation.BlockingReasons : new List<string>()
    });
}).RequireAuthorization(AuthorizationSetup.PublishPolicy);

// the ledger, filtered, newest first is applied by whoever calls this
static IQueryable<AuditEntryDto> AuditRows(AppDbContext db, Guid? productId, DateTime? from, DateTime? to)
{
    var query = db.AuditLedger.AsNoTracking().AsQueryable();
    if (productId.HasValue) query = query.Where(e => e.ProductId == productId.Value);
    if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
    if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);

    return query.OrderByDescending(e => e.Id).Select(e => new AuditEntryDto
    {
        Id = e.Id,
        Timestamp = e.Timestamp,
        ProductId = e.ProductId,
        ProductName = e.Product != null ? e.Product.Name : null,
        UserId = e.UserId,
        UserEmail = db.UserDirectory.Where(u => u.UserId == e.UserId).Select(u => u.Email).FirstOrDefault(),
        Action = e.Action,
        Outcome = e.Outcome,
        Market = e.Market,
        RulesVersion = e.RulesVersion,
        RulesStatus = e.RulesStatus,
        AiStatus = e.AiStatus,
        Justification = e.Justification,
        Detail = e.Detail,
        CopyHash = e.CopyHash,
        CopySnapshot = e.CopySnapshot,
        IssuesJson = e.IssuesJson
    });
}

// GET /api/audit, the audit ledger newest first, read only on purpose, there's no way to change or delete an entry through the api and the db blocks it too
app.MapGet("/api/audit", async (Guid? productId, DateTime? from, DateTime? to, int? take, int? skip, HttpContext http, AppDbContext db) =>
{
    var query = AuditRows(db, productId, from, to);
    // the whole count for these filters, so a screen can page through it
    http.Response.Headers["X-Total-Count"] = (await query.CountAsync()).ToString();
    var rows = await query.Skip(Math.Max(skip ?? 0, 0)).Take(Math.Clamp(take ?? 200, 1, 1000)).ToListAsync();
    return Results.Ok(rows);
}).RequireAuthorization(AuthorizationSetup.PublishPolicy);

// GET /api/audit/export, same rows but as a csv file
app.MapGet("/api/audit/export", async (Guid? productId, DateTime? from, DateTime? to, AppDbContext db) =>
{
    var rows = await AuditRows(db, productId, from, to).Take(10000).ToListAsync();
    var bytes = System.Text.Encoding.UTF8.GetPreamble()
        .Concat(System.Text.Encoding.UTF8.GetBytes(AuditCsv.Build(rows)))
        .ToArray();
    return Results.File(bytes, "text/csv; charset=utf-8", $"audit-ledger-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
}).RequireAuthorization(AuthorizationSetup.PublishPolicy);


// POST /api/claims/import-public-seed
app.MapPost("/api/claims/import-public-seed", async (
    PublicClaimsSeedImportRequest? request,
    IPublicClaimsSeedService seedService,
    IWebHostEnvironment env,
    IAuditLedger ledger,
    HttpContext http) =>
{
    var userId = CurrentUser.GetId(http.User);
    if (!DataImportRules.IsAllowed(env))
    {
        await ledger.TryRecordAsync(DataImportRules.AuditRow(userId, false, "Public seed import refused: " + DataImportRules.RefusedDetail));
        return Problems.Conflict(DataImportRules.RefusedDetail);
    }

    var csvPath = string.IsNullOrWhiteSpace(request?.CsvPath)
        ? defaultPublicClaimsSeedPath
        : request!.CsvPath!.Trim();

    var result = await seedService.ImportAsync(csvPath);
    await ledger.TryRecordAsync(DataImportRules.AuditRow(userId, result.Success, $"Public seed import: {result.Message}"));
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
}).RequireAuthorization("RequirePublishPermission");

// GET /api/dashboard/overview, numbers for the overview page counted from the ledger and review workflow, nothing personal in it so anyone signed in can see it
app.MapGet("/api/dashboard/overview", async (AppDbContext db, IDbService dbService, ILogger<Program> logger) =>
{
    if (!dbService.IsConfigured)
    {
        return Results.Json(new { message = "The database is not configured, so there is nothing to count." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        return Results.Ok(await DashboardOverview.BuildAsync(db, DateTime.UtcNow));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Could not build the dashboard overview");
        return Results.Json(new { message = "The database is unavailable, so the overview cannot be shown." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireAuthorization(AuthorizationSetup.PublishPolicy);

// GET /api/db-status
app.MapGet("/api/db-status", async (IDbService dbService) =>
{
    try
    {
        var status = await dbService.CheckStatusAsync();
        return Results.Ok(status);
    }
    catch (Exception ex)
    {
        return Results.Json(new DbStatus
        {
            Configured = false,
            Connected = false,
            TableExists = false,
            Message = ex.Message ?? "Check failed."
        }, statusCode: 500);
    }
}).RequireAuthorization(AuthorizationSetup.PublishPolicy);

// the copywriter's own products, submissions and history live in their own file
app.MapCopywriterEndpoints();
app.MapDraftEndpoints();
app.MapSendBackEndpoints();
app.MapFactsRequestEndpoints();
app.MapHomeAndGuidanceEndpoints();
app.MapReviewSupportEndpoints();

// /health is the only endpoint that's public on purpose (azure uses it for liveness probes), everything else needs auth by default, and auth0 is the only auth provider, no azure ad path
app.Logger.LogInformation("Green Claims Guard API running on http://localhost:{Port}", port);
app.Logger.LogInformation(
    "Auth0 authentication: {Status}",
    auth0Enabled
        ? $"using real tenant (domain: {auth0Domain})"
        : "NOT CONFIGURED — using placeholder authority, all protected endpoints will reject every request (set AUTH0_DOMAIN + AUTH0_AUDIENCE to enable)");

app.Run();

// needed so WebApplicationFactory<Program> in tests can reference this entry point, top level statements make an internal Program class by default
public partial class Program { }
