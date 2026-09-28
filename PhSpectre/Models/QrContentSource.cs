namespace PhSpectre.Models;

// Whether the QR encodes a link built from the shot's own data, or an arbitrary
// user-supplied URL (portfolio, social profile, etc.) with no PhSpectre data in it.
public enum QrContentSource { GeneratedLink, CustomUrl }
