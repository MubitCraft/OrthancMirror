# Local DICOMweb and WSI plugin setup

`Configuration.json` in this directory expects the full Windows workspace layout:
`orthanc-source` and `orthanc-build` are sibling directories. Install the
compatible `OrthancDicomWeb.dll` and `OrthancWSI.dll` in
`orthanc-build/Debug/Plugins`; the configured paths are relative to this
configuration file. Plugin binaries are build outputs and are not committed.

Build or install plugin releases compatible with the Orthanc executable before
starting the server. OrthancWSI routes return actual pyramid and tile data only
for DICOM whole-slide microscopy instances.

This configuration is local-development-only: Orthanc binds to `127.0.0.1`
and disallows remote access. CORS is limited to the OHIF development origin
`http://localhost:3000`; update it to the exact trusted viewer origin when the
viewer is hosted elsewhere. Do not expose this configuration to a network
without reviewing authentication, TLS, binding, and access-control settings.
