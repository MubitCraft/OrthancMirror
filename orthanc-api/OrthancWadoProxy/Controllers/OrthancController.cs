using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OrthancWadoProxy.Services;

namespace OrthancWadoProxy.Controllers;

[ApiController]
[Route("api")]
public sealed class OrthancController(OrthancApiClient orthancApiClient) : ControllerBase
{
    [HttpGet("health")]
    public async Task<IActionResult> GetHealth(CancellationToken cancellationToken)
    {
        try
        {
            if (await orthancApiClient.IsHealthyAsync(cancellationToken))
            {
                return Ok(new { status = "healthy", orthanc = "healthy" });
            }

            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { status = "unhealthy", orthanc = "unavailable" });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { status = "unhealthy", orthanc = "unavailable" });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { status = "unhealthy", orthanc = "unavailable" });
        }
    }

    [HttpGet("studies")]
    public Task<IActionResult> GetStudies(CancellationToken cancellationToken)
    {
        return GetJsonAsync(() => orthancApiClient.GetStudiesAsync(cancellationToken));
    }

    [HttpGet("studies/{studyId}")]
    public Task<IActionResult> GetStudy(string studyId, CancellationToken cancellationToken)
    {
        return GetJsonAsync(() => orthancApiClient.GetStudyAsync(studyId, cancellationToken));
    }

    private async Task<IActionResult> GetJsonAsync(Func<Task<JsonElement>> request)
    {
        try
        {
            return Ok(await request());
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound();
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Orthanc is unavailable." });
        }
        catch (TaskCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Orthanc is unavailable." });
        }
        catch (JsonException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Orthanc returned an invalid response." });
        }
    }
}
