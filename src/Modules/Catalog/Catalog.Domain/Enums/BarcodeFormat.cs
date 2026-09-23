namespace Catalog.Domain.Enums;

/// <summary>
/// Identifies the barcode format/symbology of a product barcode.
/// </summary>
public enum BarcodeFormat
{
    /// <summary>EAN-13: 13-digit European Article Number. Common retail format.</summary>
    EAN13 = 0,

    /// <summary>EAN-8: 8-digit compact version of EAN.</summary>
    EAN8 = 1,

    /// <summary>UPC-A: 12-digit Universal Product Code. Common in North America.</summary>
    UPC = 2,

    /// <summary>Code 128: High-density linear barcode. Used in logistics/warehousing.</summary>
    Code128 = 3,

    /// <summary>QR Code: 2D matrix barcode.</summary>
    QR = 4,

    /// <summary>Other/unknown format.</summary>
    Other = 99
}
