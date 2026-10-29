using Microsoft.AspNetCore.Mvc;
using OrthancWadoProxy.Models;
using OrthancWadoProxy.Services;

namespace OrthancWadoProxy.Controllers;

[ApiController]
[Route("api/instances/chunked-upload")]
public sealed class ChunkedUploadController(
    ChunkedUploadStore uploads,
    WsiDicomizerClient wsiDicomizer) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Create(
        [FromBody] CreateChunkedUploadRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.ConvertToDicomWsi)
            {
                wsiDicomizer.EnsureConfigured();
            }

            var status = await uploads.CreateAsync(
                GetOwner(), request.FileName, request.FileSizeBytes,
                request.ConvertToDicomWsi, cancellationToken);
            return CreatedAtAction(nameof(Get), new { uploadId = status.UploadId }, status);
        }
        catch (WsiPipelineUnavailableException exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = exception.Message });
        }
        catch (UploadValidationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (UploadConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (UploadStorageException exception)
        {
            return StatusCode(StatusCodes.Status507InsufficientStorage, new { error = exception.Message });
        }
    }

    [HttpGet("{uploadId:guid}")]
    public async Task<IActionResult> Get(Guid uploadId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await uploads.GetStatusAsync(uploadId, GetOwner(), cancellationToken));
        }
        catch (UploadNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{uploadId:guid}")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> AppendChunk(
        Guid uploadId,
        [FromHeader(Name = "Upload-Offset")] long offset,
        [FromHeader(Name = "Upload-Chunk-Sha256")] string? chunkSha256,
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength is not long contentLength)
        {
            return StatusCode(StatusCodes.Status411LengthRequired);
        }

        try
        {
            return Ok(await uploads.AppendChunkAsync(
                uploadId, GetOwner(), offset, contentLength, chunkSha256, Request.Body, cancellationToken));
        }
        catch (UploadNotFoundException)
        {
            return NotFound();
        }
        catch (UploadValidationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (UploadConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (UploadStorageException exception)
        {
            return StatusCode(StatusCodes.Status507InsufficientStorage, new { error = exception.Message });
        }
    }

    [HttpPost("{uploadId:guid}/complete")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Complete(Guid uploadId, CancellationToken cancellationToken)
    {
        try
        {
            return AcceptedAtAction(
                nameof(Get),
                new { uploadId },
                await uploads.CompleteAsync(uploadId, GetOwner(), cancellationToken));
        }
        catch (UploadNotFoundException)
        {
            return NotFound();
        }
        catch (UploadValidationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (UploadConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (UploadIngestionLimitException exception)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = exception.Message });
        }
        catch (UploadStorageException exception)
        {
            return StatusCode(StatusCodes.Status507InsufficientStorage, new { error = exception.Message });
        }
    }

    [HttpDelete("{uploadId:guid}")]
    public async Task<IActionResult> Cancel(Guid uploadId, CancellationToken cancellationToken)
    {
        try
        {
            await uploads.DeleteAsync(uploadId, GetOwner(), cancellationToken);
            return NoContent();
        }
        catch (UploadNotFoundException)
        {
            return NotFound();
        }
        catch (UploadConflictException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    private string GetOwner()
    {
        return User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? name
            : "local-development";
    }
}
