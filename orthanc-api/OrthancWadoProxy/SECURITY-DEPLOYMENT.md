# Secure lab deployment

This deployment assumes a Windows lab server with IIS, Windows Authentication, URL Rewrite/ARR, and a trusted TLS certificate. IIS is the only lab-network entry point. The local Visual Studio HTTP profiles use `http://localhost:8080`; do not expose the Kestrel development server directly.

## Setup sequence

1. **Install server prerequisites.** On the lab host, install IIS, the ASP.NET Core Hosting Bundle for .NET 8, IIS Windows Authentication, IIS URL Rewrite, and Application Request Routing (ARR). In IIS Manager, enable ARR proxying at the server level.

2. **Select the active Orthanc configuration.** Both provided configurations load `OrthancDicomWeb.dll` and explicitly enable the plugin at `/dicom-web/`: `orthanc-source\OrthancServer\Resources\Configuration.json` points to `orthanc-build\Debug\Plugins\OrthancDicomWeb.dll`, and `orthanc-build\orthanc.json` points to the same DLL. The build config also uses the source config's local storage directory, so either config sees the same studies. For a lab install, set both `Plugins` and `StorageDirectory` to their installed locations. Confirm the DICOMweb plugin is loaded and serves `/dicom-web/` before proceeding.

3. **Keep Orthanc private.** The checked-in source and build configurations now bind Orthanc HTTP to `127.0.0.1` and disable remote HTTP access. Start Orthanc with the selected config on port `8042`. Do not add a LAN firewall exception for `8042`. If modalities require DICOM port `4242`, allow it only from their approved source addresses; otherwise block it from the lab client network.

4. **Publish the existing API.** From the API project directory, publish `OrthancWadoProxy.csproj` in Release mode to a dedicated folder. Keep that folder writable only by administrators and the IIS application-pool identity. The included `web.config` requires Windows Authentication, denies anonymous requests, and rewrites `/dicom-web/...` to `http://127.0.0.1:8042/dicom-web/...`.

5. **Configure IIS.** Create an IIS site/application pointing to the publish folder, use an application pool with **No Managed Code**, enable Windows Authentication, disable Anonymous Authentication, and ensure the URL Rewrite/ARR modules are active. Bind the site to the lab DNS hostname on HTTPS port `5001` and select the trusted certificate for that hostname. Do not expose the API's local Kestrel listener to the LAN.

6. **Point OHIF to the protected origin.** In local development, configure DICOMweb as `http://localhost:8080/dicom-web/` and these API endpoints as `http://localhost:8080/api`; the .NET proxy applies the configured CORS policy and forwards DICOMweb traffic to loopback-only Orthanc. In the lab, use `https://<lab-host>:5001/dicom-web/` and `https://<lab-host>:5001/api`. Prefer serving OHIF from the same HTTPS origin. If using a separate OHIF origin in the lab, configure CORS on IIS/ARR and verify authenticated preflight there; Orthanc itself does not add browser CORS headers. Ensure lab browsers are permitted to use Windows Integrated Authentication for the site.

7. **Restrict the firewall.** Allow inbound HTTPS port `5001` only from approved lab client networks. Keep Orthanc HTTP `8042` blocked externally. Restrict DICOM `4242` to approved modalities if needed.

8. **Verify before use.** From an unauthenticated client, confirm the IIS site returns `401`; authenticate with an authorized lab account and confirm `/api/health` works. Confirm a remote client cannot reach `http://<lab-host>:8042`, then test `https://<lab-host>:5001/dicom-web/studies` and load representative studies in OHIF. Only IIS should be reachable from the client network.

Windows Authentication and the IIS TLS binding are server-level settings and require a lab administrator; they cannot be enabled or certificate-provisioned by the project build alone. The API `launchSettings.json` remains for localhost Visual Studio development and is not the secure lab endpoint.

## Chunked uploads

1. Create a session with `POST /api/instances/chunked-upload` and JSON fields `fileName` and `fileSizeBytes`. Set `convertToDicomWsi` to `true` only for a supported TIFF whole-slide source; omit it or set it to `false` for a regular DICOM instance. The response contains an `uploadId` and initial `nextOffset`.
2. Send raw file slices sequentially with `PUT /api/instances/chunked-upload/{uploadId}`, an `Upload-Offset` header equal to the returned offset, an `Upload-Chunk-Sha256` header for that slice, and a body no larger than 10 MiB. Repeat from the latest `nextOffset`; do not send the whole file as one form upload. The server checks each chunk and computes the final checksum from disk, so the browser need not buffer/hash the full file.
3. Call `POST /api/instances/chunked-upload/{uploadId}/complete`. A `202` response means ingestion was queued; poll `GET /api/instances/chunked-upload/{uploadId}` until its status is `completed` or `failed`. A regular DICOM result includes its SOP Instance UID; a WSI result includes `orthancSeriesId`, `instancesStored`, and `instancesTotal`.

Uploads and conversion output are staged under the service account's temporary directory by default. For deployment, set `ChunkedUpload:TempDirectory` to a dedicated, access-restricted volume outside the web root. WSI uploads require at least twice the declared source size free on that volume for the source plus derived data; pyramid generation can require more. The API validates the declared size, sequential offsets, each chunk's SHA-256, and computes a final SHA-256 on disk. Abandoned sessions expire automatically.

### Whole-slide TIFF conversion

Install/build the official [Orthanc WSI command-line tools](https://orthanc.uclouvain.be/book/plugins/wsi.html#command-line-tools), then set `WsiDicomizer:ExecutablePath` (or `WsiDicomizer__ExecutablePath`) to the full path of `OrthancWSIDicomizer.exe`. The WSI endpoint returns `503` until the executable exists. The current API accepts `.tif` and `.tiff` inputs; use only formats supported by the installed DICOM-izer. Other formats, including SVS/NDPI and already-DICOM WSI, are not enabled by this API yet.

Example session request:

```json
{
  "fileName": "slide.tif",
  "fileSizeBytes": 32212254720,
  "convertToDicomWsi": true
}
```

The worker runs `OrthancWSIDicomizer` against the staged source, writes the multi-resolution DICOM WSI files into the job's temporary directory, and streams them one at a time to Orthanc's supported `POST /instances` REST endpoint. The DICOM-izer defaults to output files below 10 MB; the worker also rejects any output exceeding `ChunkedUpload:MaxOrthancCStoreBytes` (128 MiB by default). Progress and the resulting series ID are available from the existing status endpoint. Conversion settings `GeneratePyramid`, `SmoothPyramid`, `Threads`, and `TimeoutHours` are configurable under `WsiDicomizer`.

Do not write directly into Orthanc's storage directory or inject metadata into its database. Orthanc must create and index every instance through its REST API. REST ingestion still buffers each individual DICOM instance internally, so the per-instance limit remains important; this pipeline avoids sending the original 30 GB TIFF as one Orthanc instance. Regular DICOM uploads over the 128 MiB guard remain staged and completion returns `413`; increasing that limit does not make a large single-instance import memory-safe.

After successful ingestion, the staged source TIFF and generated temporary DICOM files are deleted. Archive the original separately before upload if it must be retained.

## Multiresolution viewing

The proxy streams DICOMweb responses, including WADO-RS frames, and forwards GET/HEAD requests under `/wsi/` to Orthanc without buffering tile responses in application memory. For tiled WSI, the OrthancWSI plugin provides pyramid metadata at `/wsi/pyramids/{seriesId}` and individual tiles at `/wsi/tiles/{seriesId}/{z}/{x}/{y}`. The matching OrthancWSI plugin must be built/installed and added to the active Orthanc configuration before these routes return tile data; this workspace currently contains only the DICOMweb plugin binary. Do not add a plugin path until its DLL exists.

Use the existing TIFF-to-DICOM-WSI conversion job to create/import a tiled pyramid, then configure a WSI-capable viewer to request DICOMweb frames or the OrthancWSI tile endpoints. Ordinary CT/MR can use WADO-RS frame retrieval, but neither DICOMweb nor this proxy creates arbitrary ROI crops for those modalities. No Orthanc-core changes are required.
