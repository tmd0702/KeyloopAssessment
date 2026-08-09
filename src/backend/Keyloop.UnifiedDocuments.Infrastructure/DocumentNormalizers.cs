using Keyloop.UnifiedDocuments.Application;

namespace Keyloop.UnifiedDocuments.Infrastructure;

internal static class DocumentNormalizers
{
    public static Document FromSales(string dealDocumentId, string documentName, string documentCategory, DateTimeOffset createdAt) =>
        new($"SALES:{dealDocumentId}", dealDocumentId, documentName, documentCategory, createdAt, DocumentSource.Sales);

    public static Document FromService(string recordId, string description, string recordType, DateTimeOffset documentDate) =>
        new($"SERVICE:{recordId}", recordId, description, recordType, documentDate, DocumentSource.Service);
}