using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Options;
using OrthancWadoProxy.Configuration;

namespace OrthancWadoProxy.Services;

public sealed class DicomStoreClient(IOptions<OrthancDicomOptions> options)
{
    private readonly OrthancDicomOptions settings = options.Value;

    public async Task<string> StoreFileAsync(string filePath, CancellationToken cancellationToken)
    {
        DicomFile file;
        try
        {
            file = await Task.Run(
                () => DicomFile.Open(filePath, FileReadOption.ReadLargeOnDemand),
                cancellationToken);
        }
        catch (DicomException)
        {
            throw new UploadValidationException("The staged file is not a valid DICOM Part 10 file.");
        }

        if (file.FileMetaInfo.TransferSyntax == DicomTransferSyntax.DeflatedExplicitVRLittleEndian)
        {
            throw new UploadValidationException(
                "Deflated DICOM transfer syntax is not supported for file-backed ingestion; re-encode the file before uploading.");
        }

        string sopInstanceUid;
        try
        {
            sopInstanceUid = file.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
        }
        catch (DicomException)
        {
            throw new UploadValidationException("The DICOM file does not contain a valid SOP Instance UID.");
        }
        var client = DicomClientFactory.Create(
            settings.Host,
            settings.Port,
            false,
            settings.CallingAet,
            settings.CalledAet);
        var responseCompletion = new TaskCompletionSource<DicomStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new DicomCStoreRequest(file)
        {
            OnResponseReceived = (_, response) => responseCompletion.TrySetResult(response.Status)
        };

        await client.AddRequestAsync(request);
        await client.SendAsync(cancellationToken);

        var status = await responseCompletion.Task.WaitAsync(TimeSpan.FromHours(12), cancellationToken);
        if (status.Code != DicomStatus.Success.Code)
        {
            throw new InvalidOperationException(
                $"Orthanc rejected the DICOM C-STORE request with status 0x{status.Code:X4}.");
        }

        return sopInstanceUid;
    }
}
