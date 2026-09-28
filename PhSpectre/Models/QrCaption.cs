namespace PhSpectre.Models;

// What text (if any) is drawn beside the QR block — independent of Placement and of the QR's
// own encoded Content preset (v4 spec §1b): a card can show no caption at all while the QR
// itself still encodes the full recipe, or show "Camera + Lens" while the QR encodes nothing
// more than a custom URL. CameraAndLens renders as two lines (camera bold/larger, lens
// lighter/smaller below it), not one line joined by " · ", so a long lens name never garbles
// mid-wrap.
public enum QrCaption { None, Camera, CameraAndLens }
