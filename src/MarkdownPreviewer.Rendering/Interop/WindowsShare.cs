using MarkdownPreviewer.Application.Abstractions;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace MarkdownPreviewer.Rendering.Interop;

/// <summary>
/// Shows the Windows share sheet for a document file.
/// </summary>
/// <remarks>
/// The point of sharing FROM A PREVIEW is the document itself: the page's URL is
/// a process-local virtual host that means nothing outside this machine, so the
/// share payload is the file, handed over as a <see cref="StorageFile"/> — the
/// receiving app (Mail, Teams, Nearby Share) gets the full document, not a dead
/// link.
///
/// Every call wires a one-shot <c>DataRequested</c> handler so a stale closure
/// from a previous selection can never serve the wrong file.
/// </remarks>
internal static class WindowsShare
{
    /// <summary>IID of <c>IDataTransferManager</c> (Windows SDK).</summary>
    private static readonly Guid DataTransferManagerIid = new("a5caee9b-8708-49d1-8d36-67d25a8da00c");

    public static void ShowForFile(nint windowHandle, string fullPath, IDiagnosticLog log)
    {
        try
        {
            IDataTransferManagerInterop interop = DataTransferManager.As<IDataTransferManagerInterop>();

            Guid iid = DataTransferManagerIid;
            nint abi = interop.GetForWindow(windowHandle, ref iid);
            var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(abi);

            TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = (sender, args) =>
            {
                sender.DataRequested -= handler;
                ProvideFile(args, fullPath, log);
            };

            manager.DataRequested += handler;
            interop.ShowShareUIForWindow(windowHandle);
        }
        catch (Exception ex)
        {
            // Hosts differ (prevhost, Outlook, the harness); when the share
            // flow is unavailable the context menu still offers Copy document.
            log.Warn("The Windows share flow is unavailable here; use 'Copy document' instead.", ex);
        }
    }

    private static async void ProvideFile(DataRequestedEventArgs args, string fullPath, IDiagnosticLog log)
    {
        DataRequestDeferral deferral = args.Request.GetDeferral();
        try
        {
            args.Request.Data.Properties.Title = Path.GetFileName(fullPath);
            args.Request.Data.Properties.Description = fullPath;

            StorageFile file = await StorageFile.GetFileFromPathAsync(fullPath);
            args.Request.Data.SetStorageItems([file]);

            log.Info($"Shared '{Path.GetFileName(fullPath)}'.");
        }
        catch (Exception ex)
        {
            log.Warn("Preparing the document for sharing failed.", ex);
            args.Request.FailWithDisplayText("This document could not be shared.");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
