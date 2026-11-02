# Secure lab deployment

This deployment assumes a Windows lab server with IIS, Windows Authentication, URL Rewrite/ARR, and a trusted TLS certificate. IIS is the only lab-network entry point. The local Visual Studio HTTP profiles use `http://localhost:8080`; do not expose the Kestrel development server directly.

## Setup sequence

1. **Install server prerequisites.** On the lab host, install IIS, the ASP.NET Core Hosting Bundle for .NET 8, and IIS Windows Authentication.

2. **Select the active Orthanc configuration.** Both provided configurations load `OrthancDicomWeb.dll` and explicitly enable the plugin at `/dicom-web/`: `orthanc-source\OrthancServer\Resources\Configuration.json` points to `orthanc-build\Debug\Plugins\OrthancDicomWeb.dll`, and `orthanc-build\orthanc.json` points to the same DLL. The build config also uses the source config's local storage directory, so either config sees the same studies. For a lab install, set both `Plugins` and `StorageDirectory` to their installed locations. Confirm the DICOMweb plugin is loaded and serves `/dicom-web/` before proceeding.

3. **Keep Orthanc private.** The checked-in source and build configurations now bind Orthanc HTTP to `127.0.0.1` and disable remote HTTP access. Start Orthanc with the selected config on port `8042`. Do not add a LAN firewall exception for `8042`. If modalities require DICOM port `4242`, allow it only from their approved source addresses; otherwise block it from the lab client network.

4. **Publish the existing API.** From the API project directory, publish `OrthancWadoProxy.csproj` in Release mode to a dedicated folder. Keep that folder writable only by administrators and the IIS application-pool identity. The included `web.config` requires Windows Authentication and denies anonymous requests; DICOMweb and WSI traffic is forwarded by the .NET proxy, which enforces the read-only method allow-list.

5. **Configure IIS.** Create an IIS site/application pointing to the publish folder, use an application pool with **No Managed Code**, enable Windows Authentication, disable Anonymous Authentication, and ensure the URL Rewrite/ARR modules are active. Bind the site to the lab DNS hostname on HTTPS port `5001` and select the trusted certificate for that hostname. Do not expose the API's local Kestrel listener to the LAN.

6. **Point OHIF to the protected origin.** In local development, configure DICOMweb as `http://localhost:8080/dicom-web/` and these API endpoints as `http://localhost:8080/api`; the .NET proxy applies the configured CORS policy and forwards DICOMweb traffic to loopback-only Orthanc. In the lab, use `https://<lab-host>:5001/dicom-web/` and `https://<lab-host>:5001/api`. Prefer serving OHIF from the same HTTPS origin. If using a separate OHIF origin in the lab, set `Cors:AllowedOrigins` in the proxy's production configuration to the exact OHIF origin and verify authenticated preflight. Orthanc itself does not add browser CORS headers. Ensure lab browsers are permitted to use Windows Integrated Authentication for the site.

7. **Restrict the firewall.** Allow inbound HTTPS port `5001` only from approved lab client networks. Keep Orthanc HTTP `8042` blocked externally. Restrict DICOM `4242` to approved modalities if needed.

8. **Verify before use.** From an unauthenticated client, confirm the IIS site returns `401`; authenticate with an authorized lab account and confirm `/api/health` works. Confirm a remote client cannot reach `http://<lab-host>:8042`, then test `https://<lab-host>:5001/dicom-web/studies` and load representative studies in OHIF. Only IIS should be reachable from the client network.

Windows Authentication and the IIS TLS binding are server-level settings and require a lab administrator; they cannot be enabled or certificate-provisioned by the project build alone. The API `launchSettings.json` remains for localhost Visual Studio development and is not the secure lab endpoint.

## Uploading DICOM files

The custom chunked upload endpoint (`/api/instances/chunked-upload`) and its TIFF-to-DICOM-WSI conversion pipeline have been removed. The OHIF Viewers integration no longer uploads through this proxy (`dicomUploadEnabled: false`); OHIF only reads via DICOMweb (QIDO-RS / WADO-RS). All DICOM ingestion must go through Orthanc's built-in capabilities:

1. **Orthanc Explorer** — open `http://localhost:8042` and drag-and-drop files, studies, or entire folder trees onto the UI.
2. **Orthanc REST API** — `POST /instances` with raw DICOM bytes in the body.
3. **C-STORE SCP** — send from any DICOM node pointing to Orthanc's AE title (`ORTHANC`, port `4242`).

Inform users that the **Upload** button inside OHIF Viewers has been removed: they must upload via Orthanc Explorer, then open the study in OHIF for viewing. Keep Orthanc Explorer accessible on port `8042` for authorized lab workstations (it is bound to loopback; expose it locally or via an authenticated administrative channel only).

## Multiresolution viewing

The proxy streams DICOMweb responses, including WADO-RS frames, and forwards GET/HEAD requests under `/wsi/` to Orthanc without buffering tile responses in application memory. For tiled WSI, the OrthancWSI plugin provides pyramid metadata at `/wsi/pyramids/{seriesId}` and individual tiles at `/wsi/tiles/{seriesId}/{z}/{x}/{y}`. Both `OrthancDicomWeb.dll` and `OrthancWSI.dll` are present in `orthanc-build\Debug\Plugins` and are referenced by the active Orthanc configurations.

Upload already-DICOM tiled WSI instances via Orthanc Explorer (or convert whole-slide images offline with the official [Orthanc WSI command-line tools](https://orthanc.uclouvain.be/book/plugins/wsi.html#command-line-tools) and upload the resulting DICOM files the same way), then configure a WSI-capable viewer to request DICOMweb frames or the OrthancWSI tile endpoints. Ordinary CT/MR can use WADO-RS frame retrieval, but neither DICOMweb nor this proxy creates arbitrary ROI crops for those modalities. No Orthanc-core changes are required.
