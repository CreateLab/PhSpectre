namespace PhSpectre.Qr;

// Encodes the selected fields into the string after "#b." in a phspectre-view URL.
public interface IQrLinkPayloadEncoder
{
    string Encode(QrLinkFields fields);
}
