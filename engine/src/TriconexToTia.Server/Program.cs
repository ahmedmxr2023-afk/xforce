using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using System.Text.Json.Serialization;
using TriconexToTia.Status;
using TriconexToTia.Core.Enums;
using TriconexToTia.Core.Results;
using TriconexToTia.Engine;
using TriconexToTia.Server;

var builder = WebApplication.CreateBuilder(args);

var maxUploadBytes = ReadLong("MSW_MAX_UPLOAD_BYTES", 512L * 1024 * 1024);
var authToken = Environment.GetEnvironmentVariable("XFORCEMSW_ENGINE_TOKEN")
    ?? Environment.GetEnvironmentVariable("MSW_ENGINE_TOKEN");

// A hosted engine streams its results back and keeps nothing, which leaves the dashboard's
// evidence panels empty. When MSW_OUTPUT_DIR is set, every successful conversion rewrites
// the reports there so /api/status and /api/evidence have something honest to read.
var evidenceDir = Environment.GetEnvironmentVariable("MSW_OUTPUT_DIR");

// Evidence-backed status. Every value is computed from files on disk; nothing is hard-coded.
var statusEngine = new StatusEngine();
var statusJson = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = maxUploadBytes;

    var port = Environment.GetEnvironmentVariable("PORT");
    if (int.TryParse(port, out var parsedPort))
    {
        kestrel.ListenAnyIP(parsedPort);
    }
    else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    {
        kestrel.ListenAnyIP(5080);
    }
});

builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = maxUploadBytes;
    form.ValueLengthLimit = 1024 * 1024;
    form.MultipartHeadersLengthLimit = 64 * 1024;
});

var app = builder.Build();
var engine = new ConversionEngine();

// Always answer with a JSON body, even for an unhandled exception. Without this the client sees
// a bare 500 with no diagnostic, which is impossible to support from the website.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception exception)
    {
        if (context.Response.HasStarted)
        {
            throw;
        }
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Engine exception: " + exception.Message,
            detail = exception.ToString(),
        });
    }
});

// Building an openable TIA project needs Siemens Openness, which only exists where TIA Portal is
// installed. When it is absent (e.g. the hosted Linux engine) POST /convert/project is refused and
// the plain package endpoint is used instead. TIA is single-instance, so imports are serialised.
var tiaInstallPath = Environment.GetEnvironmentVariable("MSW_TIA_INSTALL_PATH")
    ?? @"C:\Program Files\Siemens\Automation\Portal V16";
var tiaCpuOrderNumber = Environment.GetEnvironmentVariable("MSW_TIA_CPU_ORDER_NUMBER");
var tiaProjectsDir = Environment.GetEnvironmentVariable("MSW_TIA_PROJECTS_DIR");
var tiaImportAvailable = OperatingSystem.IsWindows() && Directory.Exists(tiaInstallPath);
var tiaImportGate = new SemaphoreSlim(1, 1);

app.MapGet("/", () => Results.Text(
    "TriconexToTia engine. Endpoints: GET /health, POST /convert, POST /convert/project (multipart/form-data).",
    "text/plain"));

app.MapGet("/health", () => Results.Json(new
{
    ready = true,
    engineVersion = ConversionEngine.Version,
    converter = "triconex-to-tia",
    maxUploadBytes,
    tiaImport = new
    {
        available = tiaImportAvailable,
        installPath = tiaInstallPath,
        cpuOrderNumber = tiaCpuOrderNumber,
    },
}));

app.MapPost("/convert", async (HttpRequest request) =>
{
    var (file, filename, form, error) = await ReadUploadAsync(request, authToken);
    if (file is null || form is null) return error!;

    var target = (form["target"].ToString() ?? string.Empty).Trim().ToLowerInvariant();
    var progressId = (form["progressId"].ToString() ?? string.Empty).Trim();
    var conversionId = progressId.Length > 0 ? progressId : Guid.NewGuid().ToString("N");
    var sourceKind = ClassifySource(filename!);
    var startedAt = DateTimeOffset.UtcNow;
    var succeeded = false;
    var failure = (string?)null;
    var artifactCount = (int?)null;

    ConversionProgress.Report(progressId, "preparing");

    try
    {
    // Normalize the upload to TriStation PLCopen XML before any target runs: a ladder PDF/listing
    // goes through the ladder pipeline, a Siemens SimaticML export (AP) is translated by pt2conv,
    // and a TriStation XML passes straight through. Every target then consumes the same file.
    var prepared = await PrepareSourceAsync(file, filename!, form, stage => ConversionProgress.Report(progressId, stage),
        request.HttpContext.RequestAborted);
    if (prepared.Error is not null)
    {
        failure = "The source file could not be normalized to TriStation XML.";
        return prepared.Error;
    }

    var sourceBytes = prepared.Xml!;
    filename = prepared.FileName;
    var ladderReport = prepared.LadderReport;
    var importReport = prepared.ImportReport;

    // The XML branch stops at the interchange format: for a ladder upload this is the
    // TriStation PLCopen XML the pipeline produced; for an XML upload it is echoed back.
    if (target is "xml")
    {
        var xmlArtifacts = new List<object>
        {
            new
            {
                name = filename!,
                mimeType = "application/xml",
                kind = "primary",
                contentBase64 = Convert.ToBase64String(sourceBytes),
            },
        };
        if (!string.IsNullOrWhiteSpace(ladderReport))
        {
            xmlArtifacts.Add(new
            {
                name = "ladder_report.txt",
                mimeType = "text/plain",
                kind = "report",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ladderReport)),
            });
        }
        if (!string.IsNullOrWhiteSpace(importReport))
        {
            xmlArtifacts.Add(new
            {
                name = "simatic_report.txt",
                mimeType = "text/plain",
                kind = "report",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(importReport)),
            });
        }

        PersistEvidence(evidenceDir,
            ("ladder_report.txt", ladderReport),
            ("simatic_report.txt", importReport));

        succeeded = true;
        artifactCount = xmlArtifacts.Count;
        return Results.Json(
            new
            {
                artifacts = xmlArtifacts,
                warnings = Array.Empty<string>(),
                summary = new Dictionary<string, object?>
                {
                    ["target"] = "xml",
                    ["bytes"] = sourceBytes.Length,
                },
                engineVersion = ConversionEngine.Version,
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    // The PT2X branch builds a TriStation container straight from the XML and never
    // touches the TIA generator, so it short-circuits before the engine runs.
    if (target is "pt2x")
    {
        ConversionProgress.Report(progressId, "building pt2x");
        await using var source = new MemoryStream(sourceBytes, writable: false);
        var built = await Pt2xBuilder.BuildAsync(source, filename!, request.HttpContext.RequestAborted);
        if (!built.Success)
        {
            failure = built.Error ?? "PT2X build failed.";
            return Results.Json(
                new { error = failure, report = built.Report, ladderReport },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var artifacts = new List<object>
        {
            new
            {
                name = $"{Sanitize(Path.GetFileNameWithoutExtension(filename!))}_project.pt2x",
                mimeType = "application/octet-stream",
                kind = "primary",
                contentBase64 = Convert.ToBase64String(built.Bytes!),
            },
        };
        if (!string.IsNullOrWhiteSpace(built.Report))
        {
            artifacts.Add(new
            {
                name = "pt2x_build_report.txt",
                mimeType = "text/plain",
                kind = "report",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(built.Report)),
            });
        }
        if (!string.IsNullOrWhiteSpace(built.LintReport))
        {
            artifacts.Add(new
            {
                name = "pt2x_lint.txt",
                mimeType = "text/plain",
                kind = "report",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(built.LintReport!)),
            });
        }
        if (!string.IsNullOrWhiteSpace(ladderReport))
        {
            artifacts.Add(new
            {
                name = "ladder_report.txt",
                mimeType = "text/plain",
                kind = "report",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ladderReport)),
            });
        }
        if (!string.IsNullOrWhiteSpace(importReport))
        {
            artifacts.Add(new
            {
                name = "simatic_report.txt",
                mimeType = "text/plain",
                kind = "report",
                contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(importReport)),
            });
        }

        // The linter is the guard against editor-only defects that a successful build does not
        // catch (stale library identities, out-of-date FB interfaces). Surface it on the job.
        var warnings = new List<string>();
        if (built.LintIssues > 0)
        {
            warnings.Add($"PT2X consistency check: {built.LintIssues} issue(s). TriStation may show out-of-date (red X) blocks; see pt2x_lint.txt.");
            foreach (var message in (built.LintMessages ?? Array.Empty<string>()).Take(10))
                warnings.Add(message);
            if ((built.LintMessages?.Count ?? 0) > 10)
                warnings.Add($"... and {(built.LintMessages!.Count - 10)} more (see pt2x_lint.txt).");
        }

        PersistEvidence(evidenceDir,
            ("pt2x_build_report.txt", built.Report),
            ("pt2x_lint.txt", built.LintReport),
            ("ladder_report.txt", ladderReport),
            ("simatic_report.txt", importReport));

        succeeded = true;
        artifactCount = artifacts.Count;
        return Results.Json(
            new
            {
                artifacts,
                warnings,
                summary = new Dictionary<string, object?>
                {
                    ["target"] = "pt2x",
                    ["lintIssues"] = built.LintIssues,
                },
                engineVersion = ConversionEngine.Version,
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    ConversionProgress.Report(progressId, "converting");
    ConversionResult result;
    try
    {
        using var buffer = new MemoryStream(sourceBytes, writable: false);
        var options = ReadOptions(form["options"].ToString(), form["notes"].ToString());
        result = engine.Run(buffer, options, filename!);
    }
    catch (Exception exception)
    {
        failure = $"Conversion crashed: {exception.Message}";
        return Results.Json(
            new { error = failure, ladderReport },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    if (!result.Success)
    {
        var fatal = result.Validation.Fatal.Select(issue => issue.Message).ToArray();
        failure = result.FatalError ?? "Conversion failed.";
        return Results.Json(
            new { error = failure, fatal, ladderReport },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    var tiaArtifacts = new List<object>
    {
        new
        {
            name = $"{Sanitize(result.Project.Name)}_tia_package.zip",
            mimeType = "application/zip",
            kind = "primary",
            contentBase64 = Convert.ToBase64String(BuildZip(result)),
        },
    };
    AddReportArtifact(tiaArtifacts, result);
    if (!string.IsNullOrWhiteSpace(ladderReport))
    {
        tiaArtifacts.Add(new
        {
            name = "ladder_report.txt",
            mimeType = "text/plain",
            kind = "report",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ladderReport)),
        });
    }

    PersistEvidence(evidenceDir,
        ("report.json", result.ReportJson),
        ("conversion_report.txt", result.Report),
        ("ladder_report.txt", ladderReport));

    succeeded = true;
    artifactCount = tiaArtifacts.Count;
    return Results.Json(
        new
        {
            artifacts = tiaArtifacts,
            warnings = result.Validation.Warnings.Select(issue => issue.Message).ToArray(),
            summary = BuildSummary(result),
            engineVersion = ConversionEngine.Version,
        },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
    finally
    {
        ConversionProgress.Complete(progressId, failure);
        ConversionHistory.Record(
            evidenceDir,
            conversionId,
            Path.GetFileName(filename!) ?? "upload",
            sourceKind,
            target.Length > 0 ? target : "tia",
            succeeded,
            (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            artifactCount,
            failure);
    }
});

// Converts the upload and then drives TIA Portal (Siemens Openness) to produce an openable .ap16
// project, returned as a zip of the project folder. Only usable where TIA Portal is installed; the
// answer to the user's "convert straight to a project" request.
app.MapPost("/convert/project", async (HttpRequest request) =>
{
    if (!tiaImportAvailable)
    {
        return Results.Json(
            new
            {
                error = "Building a TIA project needs TIA Portal, which was not found on the engine host.",
                detail = $"Expected an installation at '{tiaInstallPath}'. Set MSW_TIA_INSTALL_PATH, or use the package download instead.",
            },
            statusCode: StatusCodes.Status501NotImplemented);
    }

    var (result, error) = await ConvertUploadAsync(request, authToken, engine);
    if (result is null) return error!;

    var build = await TiaProjectBuilder.BuildAsync(
        result, tiaInstallPath, tiaCpuOrderNumber, tiaProjectsDir, tiaImportGate, request.HttpContext.RequestAborted);

    if (!build.Success || build.Zip is null)
    {
        return Results.Json(
            new
            {
                error = build.Error ?? "The TIA project could not be built.",
                projectName = build.ProjectName,
                log = build.Log,
            },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    var artifacts = new List<object>
    {
        new
        {
            name = $"{build.ProjectName}_tia_project.zip",
            mimeType = "application/zip",
            kind = "primary",
            contentBase64 = Convert.ToBase64String(build.Zip),
        },
        new
        {
            name = $"{Sanitize(result.Project.Name)}_tia_package.zip",
            mimeType = "application/zip",
            kind = "package",
            contentBase64 = Convert.ToBase64String(BuildZip(result)),
        },
    };
    AddReportArtifact(artifacts, result);
    PersistEvidence(evidenceDir,
        ("report.json", result.ReportJson),
        ("conversion_report.txt", result.Report));

    return Results.Json(
        new
        {
            artifacts,
            warnings = result.Validation.Warnings.Select(issue => issue.Message).ToArray(),
            summary = BuildSummary(result),
            projectName = build.ProjectName,
            engineVersion = ConversionEngine.Version,
        },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
});

// ---------------------------------------------------------------------------
// Evidence-backed status API (consumed by the React frontend and the WPF shell).
// output dir holds report.json + roundtrip.json; status dir holds tests.trx.
// Both are resolved from ?output=/?status= or MSW_OUTPUT_DIR / MSW_STATUS_DIR.
// ---------------------------------------------------------------------------
app.MapGet("/api/status", (HttpRequest request) =>
{
    if (!TryResolveDirs(request, out var output, out var status, out var error))
    {
        return Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
    }

    var projectStatus = statusEngine.Compute(output, status);
    return Results.Json(projectStatus, statusJson);
});

app.MapGet("/api/evidence/{name}", (string name, HttpRequest request) =>
{
    if (!TryResolveDirs(request, out var output, out var status, out var error))
    {
        return Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
    }

    var path = name.ToLowerInvariant() switch
    {
        "report" => Path.Combine(output, "report.json"),
        "roundtrip" => Path.Combine(output, "roundtrip.json"),
        "tests" => Path.Combine(status, "tests.trx"),
        "conversion-report" => Path.Combine(output, "conversion_report.txt"),
        _ => null,
    };

    if (path is null) return Results.NotFound(new { error = $"Unknown evidence '{name}'." });
    if (!File.Exists(path)) return Results.NotFound(new { error = $"Evidence not found: {path}" });

    var contentType = path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)
        ? "application/xml"
        : path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/plain";
    return Results.Text(File.ReadAllText(path), contentType);
});

// In-flight progress: the browser sends a random progressId with the upload and polls this
// while the synchronous /convert request is still running. Requires the same bearer token as
// /convert so only whoever can convert can watch.
app.MapGet("/api/progress/{id}", (string id, HttpRequest request) =>
{
    if (!IsAuthorized(request, authToken))
    {
        return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var state = ConversionProgress.Get(id);
    return state is null
        ? Results.NotFound(new { error = "Unknown progress id." })
        : Results.Json(state);
});

// What the engine has converted, newest first, from the append-only history.jsonl kept next
// to the evidence reports. Empty (not an error) when no directory is configured.
app.MapGet("/api/history", (HttpRequest request) =>
{
    if (!TryResolveDirs(request, out var output, out _, out var error))
    {
        return Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
    }

    var limit = int.TryParse(request.Query["limit"], out var parsed) ? parsed : 50;
    return Results.Json(ConversionHistory.Read(output, limit), statusJson);
});

// Opens a TriStation project (.pt2/.pt2x) for review: decompiles the container to a JSON
// model (POUs, interface variables, FBD sheets with geometry and wires, global tags) that
// the web viewer renders in the browser. Same bearer token as /convert.
app.MapPost("/api/inspect", async (HttpRequest request) =>
{
    var (file, filename, _, error) = await ReadUploadAsync(request, authToken);
    if (file is null) return error!;

    await using var stream = file.OpenReadStream();
    using var buffer = new MemoryStream();
    await stream.CopyToAsync(buffer, request.HttpContext.RequestAborted);
    var bytes = Pt2Importer.DecompressIfGzip(buffer.ToArray());

    if (!Pt2Importer.LooksLikePt2(bytes))
    {
        return Results.Json(
            new { error = $"'{filename}' is not a TriStation project container (.pt2/.pt2x)." },
            statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    var inspected = await Pt2xInspector.InspectAsync(bytes, filename!, request.HttpContext.RequestAborted);
    if (!inspected.Success)
    {
        return Results.Json(
            new { error = inspected.Error ?? "The project could not be opened.", report = inspected.Report },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    return Results.Text(inspected.Json!, "application/json");
});

// Renders a TriStation project (.pt2/.pt2x) to a printable PDF. The optional form field
// "mode" selects the content: "fbd" (default) mirrors the editor's own FBD sheets (frame,
// title block, blocks, terminals, wires, comments); "reports" reproduces the report suite
// (POU Variables, Instance Variable Connections, Program Variable Listing, Tagnames
// Complete Listing, POU Cross-Reference, Shared Libraries, Project Implementation) from the
// embedded Telerik .trdp templates; "both" emits the FBD sheets then the reports. All pure
// managed drawing -- no TriStation, licence or Windows dependency. Same bearer token as /convert.
app.MapPost("/api/pdf", async (HttpRequest request) =>
{
    // Two clients call this. A browser upload posts multipart/form-data; the site's job page
    // reads a stored .pt2x from disk and forwards it as the raw body ("View as PDF" on a
    // finished job). Accept both, and take the name from the query string for the raw form.
    byte[] bytes;
    string filename;
    IFormCollection? form = null;

    if (request.HasFormContentType)
    {
        var (file, uploadedName, uploadedForm, error) = await ReadUploadAsync(request, authToken);
        if (file is null) return error!;

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, request.HttpContext.RequestAborted);
        bytes = buffer.ToArray();
        filename = uploadedName!;
        form = uploadedForm;
    }
    else
    {
        if (!IsAuthorized(request, authToken))
        {
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, request.HttpContext.RequestAborted);
        if (buffer.Length == 0)
        {
            return Results.Json(new { error = "No source file was uploaded." }, statusCode: StatusCodes.Status400BadRequest);
        }

        bytes = buffer.ToArray();
        var rawName = request.Query["name"].FirstOrDefault();
        filename = string.IsNullOrWhiteSpace(rawName) ? "project.pt2x" : Path.GetFileName(rawName);
    }

    bytes = Pt2Importer.DecompressIfGzip(bytes);

    if (!Pt2Importer.LooksLikePt2(bytes))
    {
        return Results.Json(
            new { error = $"'{filename}' is not a TriStation project container (.pt2/.pt2x)." },
            statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    var projectName = form?["name"].FirstOrDefault() ?? request.Query["name"].FirstOrDefault();
    var mode = Pt2xPdf.ParseMode(form?["mode"].FirstOrDefault() ?? request.Query["mode"].FirstOrDefault());
    var rendered = await Pt2xPdf.RenderAsync(bytes, filename, projectName, mode, request.HttpContext.RequestAborted);
    if (!rendered.Success)
    {
        return Results.Json(
            new { error = rendered.Error ?? "The project could not be rendered.", report = rendered.Report },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    var pdfName = Path.GetFileNameWithoutExtension(filename);
    return Results.File(rendered.Pdf!, "application/pdf", $"{pdfName}.pdf");
});

app.Run();

static bool TryResolveDirs(HttpRequest request, out string output, out string status, out string? error)
{
    output = request.Query["output"].FirstOrDefault()
        ?? Environment.GetEnvironmentVariable("MSW_OUTPUT_DIR") ?? string.Empty;
    status = request.Query["status"].FirstOrDefault()
        ?? Environment.GetEnvironmentVariable("MSW_STATUS_DIR") ?? string.Empty;
    error = null;

    if (string.IsNullOrWhiteSpace(output))
    {
        error = "No output directory configured. Set MSW_OUTPUT_DIR or pass ?output=<dir>.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(status))
    {
        status = Path.Combine(output, "status");
    }

    return true;
}

static bool IsAuthorized(HttpRequest request, string? token)
{
    if (string.IsNullOrWhiteSpace(token)) return true;

    var header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;

    var presented = header["Bearer ".Length..].Trim();
    return string.Equals(presented, token, StringComparison.Ordinal);
}

static async Task<(IFormFile? File, string? FileName, IFormCollection? Form, IResult? Error)> ReadUploadAsync(
    HttpRequest request,
    string? authToken)
{
    if (!IsAuthorized(request, authToken))
    {
        return (null, null, null, Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized));
    }

    if (!request.HasFormContentType)
    {
        return (null, null, null, Results.Json(new { error = "Expected multipart/form-data." }, statusCode: StatusCodes.Status400BadRequest));
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync();
    }
    catch (Exception exception)
    {
        return (null, null, null, Results.Json(new { error = $"Malformed upload: {exception.Message}" }, statusCode: StatusCodes.Status400BadRequest));
    }

    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
    {
        return (null, null, null, Results.Json(new { error = "No source file was uploaded." }, statusCode: StatusCodes.Status400BadRequest));
    }

    var filename = string.IsNullOrWhiteSpace(file.FileName) ? "project.xml" : file.FileName;
    return (file, filename, form, null);
}

static async Task<(ConversionResult? Result, IResult? Error)> ConvertUploadAsync(
    HttpRequest request,
    string? authToken,
    ConversionEngine engine)
{
    var (file, filename, form, error) = await ReadUploadAsync(request, authToken);
    if (file is null || form is null) return (null, error!);

    var prepared = await PrepareSourceAsync(file, filename!, form, stage => ConversionProgress.Report(form["progressId"], stage),
        request.HttpContext.RequestAborted);
    if (prepared.Error is not null) return (null, prepared.Error);

    var options = ReadOptions(form["options"].ToString(), form["notes"].ToString());

    ConversionResult result;
    try
    {
        using var buffer = new MemoryStream(prepared.Xml!, writable: false);
        result = engine.Run(buffer, options, prepared.FileName);
    }
    catch (Exception exception)
    {
        return (null, Results.Json(new { error = $"Conversion crashed: {exception.Message}" }, statusCode: StatusCodes.Status500InternalServerError));
    }

    if (!result.Success)
    {
        var fatal = result.Validation.Fatal.Select(issue => issue.Message).ToArray();
        return (null, Results.Json(
            new { error = result.FatalError ?? "Conversion failed.", fatal },
            statusCode: StatusCodes.Status422UnprocessableEntity));
    }

    return (result, null);
}

/// <summary>
/// Turns an upload into the TriStation PLCopen XML the rest of the pipeline expects. A ladder
/// PDF/listing is run through the ladder pipeline; a Siemens SimaticML export (AP) is translated by
/// pt2conv; a TriStation XML is returned unchanged. Non-XML bytes that match none of these fail
/// with guidance instead of the XML reader's opaque "root level is invalid".
/// </summary>
static async Task<(byte[]? Xml, string FileName, string LadderReport, string ImportReport, IResult? Error)> PrepareSourceAsync(
    IFormFile file,
    string filename,
    IFormCollection form,
    Action<string>? reportStage,
    CancellationToken cancellationToken)
{
    var extension = Path.GetExtension(filename).ToLowerInvariant();

    if (extension is ".pdf" or ".lpt" or ".txt")
    {
        reportStage?.Invoke("reading ladder");
        await using var readStream = file.OpenReadStream();
        using var rawBuffer = new MemoryStream();
        await readStream.CopyToAsync(rawBuffer, cancellationToken);
        var rawBytes = rawBuffer.ToArray();

        byte[] sourceBytes = rawBytes;
        string sourceName = filename;

        if (extension == ".pdf")
        {
            sourceBytes = rawBytes;
            sourceName = filename;
        }
        // A .lpt/.txt listing is fed to the reader as-is. circuit-reader's TextListingReader
        // parses the ASCII listing directly (it identifies a real PDF by the "%PDF-" signature,
        // anything else is a text listing). Rendering the listing to PDF first used to drop lines
        // that overflowed a page and depended on a Windows font, so it is deliberately skipped.

        await using var sourceStream = new MemoryStream(sourceBytes);
        await using var dptStream = form.Files.GetFile("dpt")?.OpenReadStream();
        await using var templateStream = form.Files.GetFile("template")?.OpenReadStream();
        var ladder = await LadderPipeline.BuildXmlAsync(sourceStream, sourceName, dptStream, templateStream, reportStage, cancellationToken, rawBytes);
        if (!ladder.Success)
        {
            return (null, sourceName, string.Empty, string.Empty, Results.Json(
                new { error = ladder.Error ?? "Ladder conversion failed.", report = ladder.Report },
                statusCode: StatusCodes.Status422UnprocessableEntity));
        }

        return (ladder.Xml!, Path.GetFileNameWithoutExtension(sourceName) + ".xml", ladder.Report, string.Empty, null);
    }

    await using var stream = file.OpenReadStream();
    using var buffer = new MemoryStream();
    await stream.CopyToAsync(buffer, cancellationToken);
    var bytes = buffer.ToArray();

    // A raw TriStation project container (.pt2/.pt2x) is decompiled to TriStation PLCopen XML.
    if (Pt2Importer.LooksLikePt2(bytes))
    {
        reportStage?.Invoke("decompiling project");
        var decompiled = await Pt2Importer.ConvertAsync(bytes, filename, cancellationToken);
        if (!decompiled.Success)
        {
            return (null, filename, string.Empty, decompiled.Report, Results.Json(
                new { error = decompiled.Error ?? "The TriStation project could not be decompiled.", report = decompiled.Report },
                statusCode: StatusCodes.Status422UnprocessableEntity));
        }

        return (decompiled.Xml!, Path.GetFileNameWithoutExtension(filename) + ".xml", string.Empty, decompiled.Report, null);
    }

    // The inverse direction: a Siemens SimaticML export becomes TriStation PLCopen XML.
    if (SimaticImporter.LooksLikeSimaticMl(bytes))
    {

        var imported = await SimaticImporter.ConvertAsync(bytes, filename, cancellationToken);
        if (!imported.Success)
        {
            return (null, filename, string.Empty, imported.Report, Results.Json(
                new { error = imported.Error ?? "The SimaticML export could not be translated.", report = imported.Report },
                statusCode: StatusCodes.Status422UnprocessableEntity));
        }

        return (imported.Xml!, Path.GetFileNameWithoutExtension(filename) + ".xml", string.Empty, imported.Report, null);
    }

    if (!LooksLikeXml(bytes))
    {
        return (null, filename, string.Empty, string.Empty, Results.Json(
            new
            {
                error = "The uploaded file is not TriStation PLCopen XML, and it was not recognized as a Siemens "
                    + "SimaticML export or a TriStation project. " + DescribeUpload(bytes, filename) + " Supported inputs are a ladder PDF "
                    + "(.pdf), a TRISTATION listing (.lpt/.txt), a TriStation XML export (.xml), a TriStation "
                    + "project (.pt2/.pt2x), or a SimaticML export (the .zip of Blocks/Tags/UDTs from TIA Openness, "
                    + "or a single block .xml).",

            },
            statusCode: StatusCodes.Status422UnprocessableEntity));
    }

    return (bytes, filename, string.Empty, string.Empty, null);
}

/// <summary>Builds a short, factual description of an unrecognized upload so the operator can see
/// what was actually handed in (extension, container type, a peek at the bytes).</summary>
static string DescribeUpload(byte[] bytes, string filename)
{
    var extension = Path.GetExtension(filename).ToLowerInvariant();
    var shown = string.IsNullOrEmpty(extension) ? "(no extension)" : extension;

    if (bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
    {
        try
        {
            using var memory = new MemoryStream(bytes, writable: false);
            using var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Read);
            var roots = archive.Entries
                .Select(entry => entry.FullName.Replace('\\', '/').Split('/')[0])
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            var names = roots.Length > 0 ? string.Join(", ", roots) : "(empty)";
            var looksProject = archive.Entries.Any(entry =>
            {
                var name = entry.FullName.Replace('\\', '/');
                return name.EndsWith(".ap16", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".ap17", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".ap18", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".ap19", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".ap20", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("System/", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("IM/", StringComparison.OrdinalIgnoreCase);
            });
            return looksProject
                ? $"It is a ZIP ({shown}) whose entries ({names}) look like a TIA Portal project, not an Openness export."
                : $"It is a ZIP ({shown}) with entries: {names}.";
        }
        catch
        {
            return $"It is a ZIP ({shown}) that could not be read.";
        }
    }

    if (Pt2Importer.LooksLikePt2(bytes))
    {
        return $"It is an OLE compound file ({shown}) that could not be read as a TriStation project.";
    }

    var preview = new string(bytes.Take(12).Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
    return $"It has extension {shown} and starts with \"{preview}\".";
}

static bool LooksLikeXml(ReadOnlySpan<byte> bytes)
{
    var index = 0;
    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) index = 3;
    while (index < bytes.Length && bytes[index] is 0x20 or 0x09 or 0x0A or 0x0D) index++;
    return index < bytes.Length && bytes[index] == (byte)'<';
}

static void AddReportArtifact(List<object> artifacts, ConversionResult result)
{
    var report = result.Report;
    if (string.IsNullOrEmpty(report)) return;

    artifacts.Add(new
    {
        name = "conversion_report.txt",
        mimeType = "text/plain",
        kind = "report",
        contentBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(report)),
    });
}

// Keeps the last run's reports in MSW_OUTPUT_DIR so /api/status and /api/evidence have
// something to read on a host the caller cannot share a work directory with. Best effort
// by design: evidence is a convenience, and must never fail a conversion that succeeded.
static void PersistEvidence(string? output, params (string Name, string? Text)[] items)
{
    if (string.IsNullOrWhiteSpace(output)) return;

    try
    {
        Directory.CreateDirectory(output);
        foreach (var (name, text) in items)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            File.WriteAllText(Path.Combine(output, name), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
    catch (Exception)
    {
        // Reporting is optional; a read-only or missing directory must not break the response.
    }
}

static Dictionary<string, object?> BuildSummary(ConversionResult result) => new()
{
    ["blocks"] = result.Project.Blocks.Count,
    ["udts"] = result.Project.DataTypes.Count,
    ["tags"] = result.Project.IoPoints.Count + result.Project.GlobalVariables.Count,
    ["approximations"] = result.Mapping.Count(TriconexToTia.Core.Enums.ConversionStatus.Approximated),
    ["manualActions"] = result.Mapping.ManualActions.Count,
    ["unrecognized"] = result.Project.UnrecognizedElements.Count,
};

static ConverterOptions ReadOptions(string? optionsJson, string? notes)
{
    var options = new ConverterOptions { TiaVersion = 20 };
    if (string.IsNullOrWhiteSpace(optionsJson)) return options;

    try
    {
        using var document = JsonDocument.Parse(optionsJson);
        var root = document.RootElement;

        if (root.TryGetProperty("tiaVersion", out var tiaVersion) && tiaVersion.TryGetInt32(out var parsed))
        {
            options.TiaVersion = parsed;
        }
        else if (root.TryGetProperty("tia_version", out var tiaVersionSnake) && tiaVersionSnake.TryGetInt32(out var parsedSnake))
        {
            options.TiaVersion = parsedSnake;
        }

        if (root.TryGetProperty("cpu", out var cpu) && cpu.ValueKind == JsonValueKind.String)
        {
            options.PlcCpu = cpu.GetString();
        }
        else if (root.TryGetProperty("plcCpu", out var plcCpu) && plcCpu.ValueKind == JsonValueKind.String)
        {
            options.PlcCpu = plcCpu.GetString();
        }

        // How FBD-language blocks are emitted. Without this the HTTP path could only ever produce
        // SCL, so the web "Build openable TIA project" button could not draw FBD diagrams.
        if (TryReadOptionString(root, "fbdOutput", "fbd_output", out var fbdOutput))
        {
            options.FbdOutput = fbdOutput.Trim().ToLowerInvariant() switch
            {
                "fbd" => FbdOutputMode.Fbd,
                "auto" => FbdOutputMode.Auto,
                "scl" => FbdOutputMode.Scl,
                _ => options.FbdOutput,
            };
        }

        // Which V16 box-template dialect to target; the importer's build decides this (V16.0 GA vs
        // V16 Update 3). Aliases are accepted because the CLI spells the GA dialect "v16-ga".
        if (TryReadOptionString(root, "fbdDialect", "fbd_dialect", out var fbdDialect))
        {
            options.FbdDialect = fbdDialect.Trim().ToLowerInvariant() switch
            {
                "v16-ga" or "v16ga" or "ga" or "v16.0-ga" => FbdBoxDialect.V16Ga,
                "v16-update3" or "v16update3" or "update3" or "u3" => FbdBoxDialect.V16Update3,
                _ => options.FbdDialect,
            };
        }
    }
    catch (JsonException)
    {
        // Ignore malformed options and use defaults.
    }

    _ = notes;
    return options;
}

/// <summary>Reads a string option under either its camelCase or snake_case spelling.</summary>
static bool TryReadOptionString(JsonElement root, string camelCase, string snakeCase, out string value)
{
    if (root.TryGetProperty(camelCase, out var camel) && camel.ValueKind == JsonValueKind.String)
    {
        value = camel.GetString()!;
        return true;
    }

    if (root.TryGetProperty(snakeCase, out var snake) && snake.ValueKind == JsonValueKind.String)
    {
        value = snake.GetString()!;
        return true;
    }

    value = string.Empty;
    return false;
}

static byte[] BuildZip(ConversionResult result)
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var file in result.Files)
        {
            var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            var bytes = file.ToBytes();
            entryStream.Write(bytes, 0, bytes.Length);
        }
    }
    return memory.ToArray();
}

static string Sanitize(string name)
{
    if (string.IsNullOrWhiteSpace(name)) return "Converted";
    var invalid = Path.GetInvalidFileNameChars();
    return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
}

/// <summary>A coarse source-family label for the conversion history (ladder, xml, project, simaticml).</summary>
static string ClassifySource(string filename)
{
    var extension = Path.GetExtension(filename).ToLowerInvariant();
    return extension switch
    {
        ".pdf" or ".lpt" or ".txt" => "ladder",
        ".xml" => "xml",
        ".pt2" or ".pt2x" => "project",
        ".zip" or ".ap16" or ".ap17" or ".ap18" or ".ap19" or ".ap20" => "simaticml",
        _ => "unknown",
    };
}

static long ReadLong(string name, long fallback)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return long.TryParse(raw, out var value) && value > 0 ? value : fallback;
}

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program { }
