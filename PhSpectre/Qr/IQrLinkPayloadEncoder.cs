namespace PhSpectre.Qr;

// Encodes a QrLinkFields selection into the payload string that goes after "#b." in a
// phspectre-view URL, per the bit-packed format described in that repo's index.html header
// comment. QrLinkBuilder's real implementation is BitPackedQrLinkPayloadEncoder; this interface
// is the seam a test (or a future alternate format) can swap in place of it.
public interface IQrLinkPayloadEncoder
{
    string Encode(QrLinkFields fields);
}
