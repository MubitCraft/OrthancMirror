using FellowOakDicom;
using OrthancWadoProxy.Configuration;
using OrthancWadoProxy.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddFellowOakDicom();

var orthancBaseUrl = builder.Configuration["Orthanc:BaseUrl"];
if (!Uri.TryCreate(orthancBaseUrl, UriKind.Absolute, out var orthancUri) ||
    (orthancUri.Scheme != Uri.UriSchemeHttp && orthancUri.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("Orthanc:BaseUrl must be an absolute HTTP or HTTPS URL.");
}

var orthancBaseUri = new Uri(orthancUri.AbsoluteUri.TrimEnd('/') + "/");

builder.Services.AddHttpClient<OrthancApiClient>(client =>
{
    client.BaseAddress = orthancBaseUri;
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.Configure<ChunkedUploadOptions>(builder.Configuration.GetSection("ChunkedUpload"));
builder.Services.Configure<OrthancDicomOptions>(builder.Configuration.GetSection("OrthancDicom"));
builder.Services.Configure<WsiDicomizerOptions>(builder.Configuration.GetSection("WsiDicomizer"));
builder.Services.AddSingleton<UploadIngestionQueue>();
builder.Services.AddSingleton<ChunkedUploadStore>();
builder.Services.AddSingleton<DicomStoreClient>();
builder.Services.AddSingleton<WsiDicomizerClient>();
builder.Services.AddHostedService<UploadIngestionWorker>();
builder.Services.AddHostedService<UploadCleanupWorker>();

builder.Services.AddHttpClient<OrthancInstancesClient>(client =>
{
    client.BaseAddress = orthancBaseUri;
    client.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddHttpClient("OrthancDicomWeb", client =>
{
    client.BaseAddress = orthancBaseUri;
    client.Timeout = Timeout.InfiniteTimeSpan;
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials().WithExposedHeaders("Upload-Offset", "Upload-Chunk-Sha256", "Location", "Content-Range");
        }
    });

    options.AddPolicy("DicomWebProxy", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials().WithExposedHeaders("Upload-Offset", "Upload-Chunk-Sha256", "Location", "Content-Range");
        }
    });
});

var app = builder.Build();
DicomSetupBuilder.UseServiceProvider(app.Services);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors();
app.UseAuthorization();

app.MapControllers();

var hopByHopHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Connection",
    "Keep-Alive",
    "Proxy-Authenticate",
    "Proxy-Authorization",
    "TE",
    "Trailer",
    "Transfer-Encoding",
    "Upgrade",
    "Host"
};

void MapOrthancProxy(string localRoot, string upstreamRoot, string[] methods)
{
    app.MapMethods($"{localRoot}/{{**path}}", methods,
        async (HttpContext context, IHttpClientFactory httpClientFactory, ILogger<Program> logger) =>
        {
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            var orthancClient = httpClientFactory.CreateClient("OrthancDicomWeb");
            var requestPath = context.Request.Path.ToUriComponent();
            var suffix = requestPath[localRoot.Length..].TrimStart('/');
            var targetUri = new Uri(orthancClient.BaseAddress!,
                upstreamRoot.TrimEnd('/') + "/" + suffix + context.Request.QueryString.ToUriComponent());

            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
            var hasBody = context.Request.ContentLength is > 0 ||
                          context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                          HttpMethods.IsPost(context.Request.Method) ||
                          HttpMethods.IsPut(context.Request.Method) ||
                          HttpMethods.IsPatch(context.Request.Method);

            if (hasBody)
            {
                request.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentLength.HasValue)
                {
                    request.Content.Headers.ContentLength = context.Request.ContentLength.Value;
                }
            }

            var requestConnectionHeaders = context.Request.Headers["Connection"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var header in context.Request.Headers)
            {
                if (hopByHopHeaders.Contains(header.Key) || requestConnectionHeaders.Contains(header.Key))
                {
                    continue;
                }

                var values = header.Value.ToArray();
                if (!request.Headers.TryAddWithoutValidation(header.Key, values))
                {
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, values);
                }
            }

            try
            {
                using var response = await orthancClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

                context.Response.StatusCode = (int)response.StatusCode;
                var responseConnectionHeaders = response.Headers.TryGetValues("Connection", out var values)
                    ? values.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (!hopByHopHeaders.Contains(header.Key) && !responseConnectionHeaders.Contains(header.Key) && !header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Orthanc {UpstreamRoot} request could not be completed.", upstreamRoot);
                if (context.Response.HasStarted)
                {
                    throw;
                }

                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        })
        .RequireCors("DicomWebProxy");
}

MapOrthancProxy("/dicom-web", "dicom-web",
    new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" });
MapOrthancProxy("/wsi", "wsi", new[] { "GET", "HEAD", "OPTIONS" });

app.Run();
